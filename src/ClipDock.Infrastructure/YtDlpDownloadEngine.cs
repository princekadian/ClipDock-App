using System.Globalization;
using ClipDock.Core.Models;
using ClipDock.Core.Services;

namespace ClipDock.Infrastructure;

public sealed class YtDlpDownloadEngine(
    AppPaths paths,
    IEngineManager engineManager,
    ProcessRunner runner,
    IAppLogger logger) : IDownloadEngine
{
    private const string ProgressPrefix = "clipdock-progress:";
    private const string FilePrefix = "clipdock-file:";

    public async Task<DownloadResult> DownloadAsync(
        DownloadJob job,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        await engineManager.EnsureYtDlpAsync(cancellationToken).ConfigureAwait(false);
        await engineManager.EnsureFfmpegAsync(cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(job.OutputDirectory);

        string? outputPath = null;
        var lastReport = DateTimeOffset.MinValue;
        var progressGate = new object();
        var downloadedByFormat = new Dictionary<string, long>(StringComparer.Ordinal);
        var totalByFormat = new Dictionary<string, long>(StringComparer.Ordinal);
        long lastDownloaded = 0;
        long? lastTotal = job.EstimatedBytes;
        var arguments = new[]
        {
            "--progress", "--newline", "--progress-delta", "0.2", "--no-color", "--no-playlist", "--no-call-home", "--continue", "--windows-filenames",
            "--ffmpeg-location", paths.FfmpegDirectory,
            "--format", job.FormatExpression,
            "--merge-output-format", job.Container.ToString().ToLowerInvariant(),
            "--output", Path.Combine(job.OutputDirectory, job.FilenameTemplate),
            "--progress-template", $"download:{ProgressPrefix}%(progress.downloaded_bytes)s|%(progress.total_bytes,progress.total_bytes_estimate)s|%(progress.speed)s|%(progress.eta)s|%(info.format_id)s",
            "--print", $"after_move:{FilePrefix}%(filepath)s",
            "--", job.Url
        };

        void ReadLine(string line)
        {
            if (line.StartsWith(FilePrefix, StringComparison.Ordinal))
            {
                outputPath = line[FilePrefix.Length..].Trim();
                return;
            }

            if (line.StartsWith("[Merger]", StringComparison.Ordinal))
            {
                progress.Report(new DownloadProgress(
                    Math.Min(99.5, job.Progress), lastDownloaded, lastTotal,
                    null, null, DownloadState.Merging));
                return;
            }

            if (!line.StartsWith(ProgressPrefix, StringComparison.Ordinal)) return;
            var parts = line[ProgressPrefix.Length..].Split('|');
            if (parts.Length != 5) return;
            var downloaded = ParseLong(parts[0]) ?? 0;
            var streamTotal = ParseLong(parts[1]);
            var speed = ParseDouble(parts[2]);
            var etaSeconds = ParseDouble(parts[3]);
            var formatId = string.IsNullOrWhiteSpace(parts[4]) || parts[4].Equals("NA", StringComparison.OrdinalIgnoreCase)
                ? "default"
                : parts[4];

            double percentage;
            lock (progressGate)
            {
                downloadedByFormat[formatId] = downloaded;
                if (streamTotal.HasValue) totalByFormat[formatId] = streamTotal.Value;
                lastDownloaded = downloadedByFormat.Values.Sum();
                var knownTotal = totalByFormat.Values.Sum();
                var overallTotal = Math.Max(job.EstimatedBytes ?? 0, Math.Max(knownTotal, lastDownloaded));
                lastTotal = overallTotal > 0 ? overallTotal : null;
                percentage = lastTotal > 0
                    ? Math.Clamp(lastDownloaded * 100d / lastTotal.Value, 0, 99.5)
                    : 0;
            }

            var now = DateTimeOffset.UtcNow;
            if (now - lastReport < TimeSpan.FromMilliseconds(150) && percentage < 99.5) return;
            lastReport = now;
            progress.Report(new DownloadProgress(
                percentage,
                lastDownloaded,
                lastTotal,
                speed,
                etaSeconds.HasValue ? TimeSpan.FromSeconds(etaSeconds.Value) : null,
                DownloadState.Downloading));
        }

        try
        {
            var result = await runner.RunAsync(paths.YtDlp, arguments, ReadLine, ReadLine, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                var userMessage = ErrorMessageMapper.ForYtDlp(result.StandardError);
                logger.Warning("yt-dlp download failed", new { job.Id, result.ExitCode, result.StandardError });
                return new DownloadResult(false, null, userMessage, result.StandardError);
            }

            if (string.IsNullOrWhiteSpace(outputPath))
            {
                outputPath = FindNewestOutput(job);
            }

            progress.Report(new DownloadProgress(100, job.TotalBytes ?? job.EstimatedBytes ?? 0,
                job.TotalBytes ?? job.EstimatedBytes, null, TimeSpan.Zero, DownloadState.Completed, outputPath));
            return new DownloadResult(true, outputPath, null, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.Error("Download process failed", exception, new { job.Id });
            return new DownloadResult(false, null, "The download engine could not be started.", exception.ToString());
        }
    }

    private static string? FindNewestOutput(DownloadJob job)
    {
        var extension = "." + job.Container.ToString().ToLowerInvariant();
        return Directory.Exists(job.OutputDirectory)
            ? Directory.EnumerateFiles(job.OutputDirectory, "*" + extension)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;
    }

    private static long? ParseLong(string value) =>
        long.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;
    private static double? ParseDouble(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;
}
