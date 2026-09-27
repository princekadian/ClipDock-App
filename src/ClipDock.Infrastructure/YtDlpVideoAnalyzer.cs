using System.Globalization;
using System.Text.Json;
using ClipDock.Core.Models;
using ClipDock.Core.Services;

namespace ClipDock.Infrastructure;

public sealed class YtDlpVideoAnalyzer(
    AppPaths paths,
    IEngineManager engineManager,
    ProcessRunner runner,
    IAppLogger logger) : IVideoAnalyzer
{
    public async Task<VideoMetadata> AnalyzeAsync(string url, CancellationToken cancellationToken)
    {
        if (!YouTubeUrlValidator.TryNormalize(url, out var normalized))
        {
            throw new ArgumentException("Enter a supported YouTube video link.", nameof(url));
        }

        await engineManager.EnsureYtDlpAsync(cancellationToken).ConfigureAwait(false);
        var result = await runner.RunAsync(paths.YtDlp,
            ["--dump-single-json", "--no-playlist", "--no-warnings", "--no-call-home", "--", normalized],
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            logger.Warning("yt-dlp analysis failed", new { result.ExitCode, result.StandardError });
            throw new VideoAnalysisException(ErrorMessageMapper.ForYtDlp(result.StandardError), result.StandardError);
        }

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            var root = document.RootElement;
            var formats = root.GetProperty("formats").EnumerateArray().Select(ParseFormat).ToArray();
            return new VideoMetadata(
                GetString(root, "id") ?? throw new InvalidDataException("Video ID is missing."),
                normalized,
                GetString(root, "title") ?? "Untitled video",
                GetString(root, "channel") ?? GetString(root, "uploader") ?? "Unknown channel",
                TimeSpan.FromSeconds(GetDouble(root, "duration") ?? 0),
                GetString(root, "thumbnail"),
                ParseUploadDate(GetString(root, "upload_date")),
                formats);
        }
        catch (JsonException exception)
        {
            logger.Error("Invalid yt-dlp metadata JSON", exception);
            throw new VideoAnalysisException("The video information returned by YouTube could not be read.", result.StandardOutput, exception);
        }
    }

    private static MediaFormat ParseFormat(JsonElement value)
    {
        var videoCodec = GetString(value, "vcodec");
        var audioCodec = GetString(value, "acodec");
        var videoNone = string.IsNullOrWhiteSpace(videoCodec) || videoCodec.Equals("none", StringComparison.OrdinalIgnoreCase);
        var audioNone = string.IsNullOrWhiteSpace(audioCodec) || audioCodec.Equals("none", StringComparison.OrdinalIgnoreCase);
        return new MediaFormat(
            GetString(value, "format_id") ?? string.Empty,
            GetInt(value, "height"),
            GetString(value, "ext") ?? string.Empty,
            videoCodec,
            audioCodec,
            GetDouble(value, "vbr") ?? (!videoNone ? GetDouble(value, "tbr") : null),
            GetDouble(value, "abr"),
            GetLong(value, "filesize"),
            GetLong(value, "filesize_approx"),
            !videoNone && audioNone,
            videoNone && !audioNone,
            GetDouble(value, "fps"),
            GetString(value, "protocol"));
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static double? GetDouble(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : null;
    private static int? GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
    private static long? GetLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
    private static DateOnly? ParseUploadDate(string? value) =>
        DateOnly.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
}

public sealed class VideoAnalysisException(string message, string technicalDetails, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string TechnicalDetails { get; } = technicalDetails;
}
