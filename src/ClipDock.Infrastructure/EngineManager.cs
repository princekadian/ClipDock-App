using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using ClipDock.Core.Services;

namespace ClipDock.Infrastructure;

public sealed class EngineManager : IEngineManager, IDisposable
{
    private const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    private const string YtDlpChecksumsUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS";
    private const string FfmpegReleaseUrl = "https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/tags/latest";
    private readonly AppPaths _paths;
    private readonly ProcessRunner _runner;
    private readonly IAppLogger _logger;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _installationGate = new(1, 1);

    public EngineManager(AppPaths paths, ProcessRunner runner, IAppLogger logger)
    {
        _paths = paths;
        _runner = runner;
        _logger = logger;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ClipDock/1.0");
    }

    public Task EnsureYtDlpAsync(CancellationToken cancellationToken) =>
        File.Exists(_paths.YtDlp) ? Task.CompletedTask : InstallYtDlpAsync(cancellationToken);

    public Task EnsureFfmpegAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(_paths.Ffmpeg) && File.Exists(_paths.Ffprobe)) return Task.CompletedTask;
        return TryUseSystemFfmpeg() ? Task.CompletedTask : InstallFfmpegAsync(cancellationToken);
    }

    public async Task<string?> GetYtDlpVersionAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.YtDlp)) return null;
        var result = await _runner.RunAsync(_paths.YtDlp, ["--version"], cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0 ? result.StandardOutput.Trim() : null;
    }

    public async Task<string?> GetFfmpegVersionAsync(CancellationToken cancellationToken)
    {
        TryUseSystemFfmpeg();
        if (!File.Exists(_paths.Ffmpeg)) return null;
        var result = await _runner.RunAsync(_paths.Ffmpeg, ["-version"], cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0 ? result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() : null;
    }

    public Task UpdateYtDlpAsync(CancellationToken cancellationToken) => InstallYtDlpAsync(cancellationToken, true);

    private async Task InstallYtDlpAsync(CancellationToken cancellationToken, bool replace = false)
    {
        await _installationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!replace && File.Exists(_paths.YtDlp)) return;
            _paths.EnsureDirectories();
            var temporary = Path.Combine(_paths.Tools, $"yt-dlp-{Guid.NewGuid():N}.tmp");
            try
            {
                await DownloadAsync(YtDlpUrl, temporary, cancellationToken).ConfigureAwait(false);
                var checksums = await _httpClient.GetStringAsync(YtDlpChecksumsUrl, cancellationToken).ConfigureAwait(false);
                var expected = FindChecksum(checksums, "yt-dlp.exe");
                VerifyChecksum(temporary, expected);
                File.Move(temporary, _paths.YtDlp, true);
                _logger.Information("yt-dlp installed", new { path = _paths.YtDlp });
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        finally { _installationGate.Release(); }
    }

    private async Task InstallFfmpegAsync(CancellationToken cancellationToken)
    {
        await _installationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(_paths.Ffmpeg) && File.Exists(_paths.Ffprobe)) return;
            if (TryUseSystemFfmpeg()) return;
            _paths.EnsureDirectories();
            DeleteStaleFfmpegDownloads();
            using var releaseStream = await _httpClient.GetStreamAsync(FfmpegReleaseUrl, cancellationToken).ConfigureAwait(false);
            using var release = await JsonDocument.ParseAsync(releaseStream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var assets = release.RootElement.GetProperty("assets").EnumerateArray().ToArray();
            var archiveAsset = assets.FirstOrDefault(asset =>
                asset.GetProperty("name").GetString()?.Equals("ffmpeg-master-latest-win64-gpl.zip", StringComparison.OrdinalIgnoreCase) == true);
            var checksumAsset = assets.FirstOrDefault(asset =>
                asset.GetProperty("name").GetString()?.Equals("checksums.sha256", StringComparison.OrdinalIgnoreCase) == true);
            if (archiveAsset.ValueKind == JsonValueKind.Undefined || checksumAsset.ValueKind == JsonValueKind.Undefined)
            {
                throw new InvalidOperationException("The trusted FFmpeg release did not contain the expected Windows build.");
            }

            var archiveUrl = archiveAsset.GetProperty("browser_download_url").GetString()!;
            var checksumUrl = checksumAsset.GetProperty("browser_download_url").GetString()!;
            var archiveName = archiveAsset.GetProperty("name").GetString()!;
            var temporaryArchive = Path.Combine(_paths.Tools, $"ffmpeg-{Guid.NewGuid():N}.zip");
            var temporaryDirectory = Path.Combine(_paths.Tools, $"ffmpeg-{Guid.NewGuid():N}");
            try
            {
                await DownloadAsync(archiveUrl, temporaryArchive, cancellationToken).ConfigureAwait(false);
                var checksums = await _httpClient.GetStringAsync(checksumUrl, cancellationToken).ConfigureAwait(false);
                VerifyChecksum(temporaryArchive, FindChecksum(checksums, archiveName));
                ZipFile.ExtractToDirectory(temporaryArchive, temporaryDirectory);
                var ffmpeg = Directory.EnumerateFiles(temporaryDirectory, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
                var ffprobe = Directory.EnumerateFiles(temporaryDirectory, "ffprobe.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (ffmpeg is null || ffprobe is null)
                {
                    throw new InvalidDataException("The downloaded FFmpeg package is incomplete.");
                }

                File.Copy(ffmpeg, _paths.Ffmpeg, true);
                File.Copy(ffprobe, _paths.Ffprobe, true);
                _logger.Information("FFmpeg installed", new { path = _paths.Ffmpeg });
            }
            finally
            {
                if (File.Exists(temporaryArchive)) File.Delete(temporaryArchive);
                if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, true);
            }
        }
        finally { _installationGate.Release(); }
    }

    private bool TryUseSystemFfmpeg()
    {
        if (!_paths.FfmpegDirectory.Equals(_paths.Tools, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(_paths.Ffmpeg) && File.Exists(_paths.Ffprobe))
            return true;

        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue)) return false;
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var ffmpeg = Path.Combine(directory, "ffmpeg.exe");
                var ffprobe = Path.Combine(directory, "ffprobe.exe");
                if (!File.Exists(ffmpeg) || !File.Exists(ffprobe)) continue;
                _paths.UseFfmpegDirectory(directory);
                DeleteStaleFfmpegDownloads();
                _logger.Information("Using system FFmpeg", new { directory });
                return true;
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                _logger.Warning("Ignored invalid PATH entry while locating FFmpeg", new { directory });
            }
        }

        return false;
    }

    private void DeleteStaleFfmpegDownloads()
    {
        foreach (var file in Directory.EnumerateFiles(_paths.Tools, "ffmpeg-*.zip"))
        {
            try { File.Delete(file); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        foreach (var directory in Directory.EnumerateDirectories(_paths.Tools, "ffmpeg-*"))
        {
            try { Directory.Delete(directory, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private async Task DownloadAsync(string url, string destination, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = File.Create(destination);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    }

    private static string FindChecksum(string manifest, string filename)
    {
        foreach (var line in manifest.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[^1].TrimStart('*').Equals(filename, StringComparison.OrdinalIgnoreCase))
            {
                return parts[0];
            }
        }

        throw new InvalidDataException($"No checksum was published for {filename}.");
    }

    private static void VerifyChecksum(string file, string expected)
    {
        using var stream = File.OpenRead(file);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The downloaded engine failed integrity validation.");
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _installationGate.Dispose();
    }
}
