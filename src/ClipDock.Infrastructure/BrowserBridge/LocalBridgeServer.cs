using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClipDock.Core.Models;
using ClipDock.Core.Services;

namespace ClipDock.Infrastructure.BrowserBridge;

public sealed class LocalBridgeServer : IAsyncDisposable
{
    public const int Port = 38765;
    private const int MaximumHeaderBytes = 16 * 1024;
    private const int MaximumBodyBytes = 64 * 1024;
    private readonly IVideoAnalyzer _analyzer;
    private readonly DownloadQueueManager _queue;
    private readonly AppSettings _settings;
    private readonly IAppLogger _logger;
    private readonly TcpListener _listener = new(IPAddress.Loopback, Port);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _analysisGate = new(2, 2);
    private readonly object _rateGate = new();
    private readonly Queue<DateTimeOffset> _requests = new();
    private Task? _acceptLoop;

    public LocalBridgeServer(IVideoAnalyzer analyzer, DownloadQueueManager queue, AppSettings settings, IAppLogger logger)
    {
        _analyzer = analyzer;
        _queue = queue;
        _settings = settings;
        _logger = logger;
    }

    public void Start()
    {
        if (_acceptLoop is not null) return;
        _listener.Start(16);
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_shutdown.Token));
        _logger.Information("Browser bridge started", new { Port });
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _ = Task.Run(() => HandleClientAsync(client, cancellationToken), CancellationToken.None);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                _logger.Error("Browser bridge accept failed", exception);
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            client.ReceiveTimeout = 15_000;
            client.SendTimeout = 15_000;
            try
            {
                await using var stream = client.GetStream();
                var request = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
                var response = await RouteAsync(request, cancellationToken).ConfigureAwait(false);
                await WriteResponseAsync(stream, response, request.Origin, cancellationToken).ConfigureAwait(false);
            }
            catch (BridgeHttpException exception)
            {
                try
                {
                    await using var stream = client.GetStream();
                    await WriteResponseAsync(stream,
                        JsonResponse(exception.StatusCode, new BridgeErrorResponse(exception.Message)), null, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception writeException)
                {
                    _logger.Warning("Could not write bridge error response", new { writeException.Message });
                }
            }
            catch (Exception exception)
            {
                _logger.Error("Browser bridge request failed", exception);
            }
        }
    }

    private async Task<HttpResponse> RouteAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (!IsAllowedOrigin(request.Origin))
            return JsonResponse(403, new BridgeErrorResponse("This browser extension is not allowed to connect."));

        if (request.Method == "OPTIONS") return new HttpResponse(204, "application/json", []);
        if (request.Method == "GET" && request.Path == "/health")
            return JsonResponse(200, new { status = "ok", version = "1.0.0" });
        if (!WithinRateLimit()) return JsonResponse(429, new BridgeErrorResponse("Too many requests. Wait a moment and try again."));
        if (!IsAuthenticated(request)) return JsonResponse(401, new BridgeErrorResponse("Pair the browser extension with ClipDock first."));

        if (request.Method == "POST" && request.Path == "/analyze")
        {
            var body = Deserialize<AnalyzeRequest>(request.Body);
            if (!YouTubeUrlValidator.TryNormalize(body.Url, out var url))
                return JsonResponse(400, new BridgeErrorResponse("Enter a supported YouTube video link."));

            await _analysisGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var metadata = await _analyzer.AnalyzeAsync(url, cancellationToken).ConfigureAwait(false);
                return JsonResponse(200, ToResponse(metadata, body.Container));
            }
            catch (VideoAnalysisException exception)
            {
                return JsonResponse(422, new BridgeErrorResponse(exception.Message));
            }
            finally { _analysisGate.Release(); }
        }

        if (request.Method == "POST" && request.Path == "/download")
        {
            var body = Deserialize<DownloadRequest>(request.Body);
            if (!YouTubeUrlValidator.TryNormalize(body.Url, out var url) || !SupportedQualities.All.Contains(body.Height))
                return JsonResponse(400, new BridgeErrorResponse("The video link or quality is invalid."));

            await _analysisGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var metadata = await _analyzer.AnalyzeAsync(url, cancellationToken).ConfigureAwait(false);
                var option = FormatPlanner.BuildOptions(metadata, body.Container).First(item => item.Height == body.Height);
                if (!option.IsAvailable || option.FormatExpression is null)
                    return JsonResponse(409, new BridgeErrorResponse($"{option.Label} is not available for this video."));

                var job = new DownloadJob
                {
                    Url = metadata.Url,
                    VideoId = metadata.Id,
                    Title = metadata.Title,
                    Channel = metadata.Channel,
                    ThumbnailUrl = metadata.ThumbnailUrl,
                    Height = option.Height,
                    Container = body.Container,
                    FormatExpression = option.FormatExpression,
                    OutputDirectory = _settings.DownloadDirectory,
                    FilenameTemplate = _settings.FilenameTemplate,
                    EstimatedBytes = option.EstimatedBytes
                };
                await _queue.EnqueueAsync(job, cancellationToken).ConfigureAwait(false);
                return JsonResponse(202, new BridgeDownloadResponse(job.Id, "queued"));
            }
            catch (VideoAnalysisException exception)
            {
                return JsonResponse(422, new BridgeErrorResponse(exception.Message));
            }
            finally { _analysisGate.Release(); }
        }

        if (request.Method == "GET" && request.Path == "/status")
        {
            var values = ParseQuery(request.Query);
            if (!Guid.TryParse(values.GetValueOrDefault("id"), out var id))
                return JsonResponse(400, new BridgeErrorResponse("A valid download ID is required."));
            var job = _queue.Jobs.FirstOrDefault(item => item.Id == id);
            return job is null
                ? JsonResponse(404, new BridgeErrorResponse("Download not found."))
                : JsonResponse(200, new BridgeStatusResponse(job.Id, job.State.ToString().ToLowerInvariant(), job.Progress, job.DownloadedBytes, job.TotalBytes));
        }

        return JsonResponse(404, new BridgeErrorResponse("Endpoint not found."));
    }

    private bool IsAllowedOrigin(string? origin)
    {
        // Chromium extension workers may omit Origin or send the opaque value "null".
        // Sensitive endpoints still require the independently generated 256-bit token.
        if (string.IsNullOrWhiteSpace(origin) || origin.Equals("null", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals("chrome-extension", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Length != 32 || uri.Host.Any(character => character is < 'a' or > 'p'))
            return false;

        return string.IsNullOrWhiteSpace(_settings.AllowedExtensionId) ||
               uri.Host.Equals(_settings.AllowedExtensionId, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsAuthenticated(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("x-clipdock-token", out var token) || token.Length != _settings.BridgeToken.Length)
            return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(_settings.BridgeToken));
    }

    private bool WithinRateLimit()
    {
        lock (_rateGate)
        {
            var cutoff = DateTimeOffset.UtcNow.AddMinutes(-1);
            while (_requests.TryPeek(out var timestamp) && timestamp < cutoff) _requests.Dequeue();
            if (_requests.Count >= 30) return false;
            _requests.Enqueue(DateTimeOffset.UtcNow);
            return true;
        }
    }

    private static BridgeVideoResponse ToResponse(VideoMetadata metadata, OutputContainer container) => new(
        metadata.Id,
        metadata.Title,
        metadata.Channel,
        metadata.Duration.TotalSeconds,
        metadata.ThumbnailUrl,
        FormatPlanner.BuildOptions(metadata, container)
            .Select(option => new BridgeQualityResponse(option.Height, option.Label, option.IsAvailable, option.EstimatedBytes, option.IsExactSize))
            .ToArray());

    private static T Deserialize<T>(byte[] body)
    {
        try { return JsonSerializer.Deserialize<T>(body, JsonOptions.Default) ?? throw new JsonException(); }
        catch (JsonException) { throw new BridgeHttpException(400, "The request body is invalid."); }
    }

    private static async Task<HttpRequest> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var memory = new MemoryStream();
        var headerEnd = -1;
        while (headerEnd < 0)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new BridgeHttpException(400, "Incomplete request.");
            memory.Write(buffer, 0, read);
            if (memory.Length > MaximumHeaderBytes) throw new BridgeHttpException(431, "Request headers are too large.");
            headerEnd = FindHeaderEnd(memory.GetBuffer(), (int)memory.Length);
        }

        var all = memory.ToArray();
        var headerText = Encoding.ASCII.GetString(all, 0, headerEnd);
        var lines = headerText.Split("\r\n", StringSplitOptions.None);
        var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length != 3) throw new BridgeHttpException(400, "Invalid request line.");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var separator = line.IndexOf(':');
            if (separator > 0) headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        var contentLength = headers.TryGetValue("content-length", out var lengthValue) && int.TryParse(lengthValue, out var length) ? length : 0;
        if (contentLength is < 0 or > MaximumBodyBytes) throw new BridgeHttpException(413, "Request body is too large.");
        var bodyOffset = headerEnd + 4;
        var body = new byte[contentLength];
        var available = Math.Min(contentLength, all.Length - bodyOffset);
        if (available > 0) Array.Copy(all, bodyOffset, body, 0, available);
        var position = available;
        while (position < contentLength)
        {
            var read = await stream.ReadAsync(body.AsMemory(position, contentLength - position), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new BridgeHttpException(400, "Incomplete request body.");
            position += read;
        }

        var target = requestLine[1];
        var question = target.IndexOf('?');
        return new HttpRequest(
            requestLine[0].ToUpperInvariant(),
            question >= 0 ? target[..question] : target,
            question >= 0 ? target[(question + 1)..] : string.Empty,
            headers,
            body,
            headers.GetValueOrDefault("origin"));
    }

    private static int FindHeaderEnd(byte[] bytes, int length)
    {
        for (var index = 3; index < length; index++)
        {
            if (bytes[index - 3] == 13 && bytes[index - 2] == 10 && bytes[index - 1] == 13 && bytes[index] == 10)
                return index - 3;
        }
        return -1;
    }

    private static async Task WriteResponseAsync(NetworkStream stream, HttpResponse response, string? origin, CancellationToken cancellationToken)
    {
        var reason = response.StatusCode switch
        {
            200 => "OK", 202 => "Accepted", 204 => "No Content", 400 => "Bad Request", 401 => "Unauthorized",
            403 => "Forbidden", 404 => "Not Found", 409 => "Conflict", 413 => "Content Too Large",
            422 => "Unprocessable Content", 429 => "Too Many Requests", 431 => "Request Header Fields Too Large", _ => "Error"
        };
        var cors = !string.IsNullOrWhiteSpace(origin) && origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)
            ? $"Access-Control-Allow-Origin: {origin}\r\nAccess-Control-Allow-Headers: Content-Type, X-ClipDock-Token\r\nAccess-Control-Allow-Methods: GET, POST, OPTIONS\r\nVary: Origin\r\n"
            : string.Empty;
        var headers = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {response.StatusCode} {reason}\r\nContent-Type: {response.ContentType}\r\nContent-Length: {response.Body.Length}\r\n{cors}Cache-Control: no-store\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(headers, cancellationToken).ConfigureAwait(false);
        if (response.Body.Length > 0) await stream.WriteAsync(response.Body, cancellationToken).ConfigureAwait(false);
    }

    private static HttpResponse JsonResponse(int statusCode, object value) =>
        new(statusCode, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions.Default));

    private static Dictionary<string, string> ParseQuery(string query) => query
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split('=', 2))
        .Where(parts => parts.Length == 2)
        .ToDictionary(parts => Uri.UnescapeDataString(parts[0]), parts => Uri.UnescapeDataString(parts[1]), StringComparer.OrdinalIgnoreCase);

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _listener.Stop();
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _analysisGate.Dispose();
        _shutdown.Dispose();
    }

    private sealed record HttpRequest(string Method, string Path, string Query, Dictionary<string, string> Headers, byte[] Body, string? Origin);
    private sealed record HttpResponse(int StatusCode, string ContentType, byte[] Body);
    private sealed class BridgeHttpException(int statusCode, string message) : Exception(message)
    {
        public int StatusCode { get; } = statusCode;
    }
}
