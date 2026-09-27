using ClipDock.Core.Models;

namespace ClipDock.Core.Services;

public interface IVideoAnalyzer
{
    Task<VideoMetadata> AnalyzeAsync(string url, CancellationToken cancellationToken);
}

public interface IDownloadEngine
{
    Task<DownloadResult> DownloadAsync(
        DownloadJob job,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken);
}

public interface IEngineManager
{
    Task EnsureYtDlpAsync(CancellationToken cancellationToken);
    Task EnsureFfmpegAsync(CancellationToken cancellationToken);
    Task<string?> GetYtDlpVersionAsync(CancellationToken cancellationToken);
    Task<string?> GetFfmpegVersionAsync(CancellationToken cancellationToken);
    Task UpdateYtDlpAsync(CancellationToken cancellationToken);
}

public interface ISettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

public interface IHistoryStore
{
    Task<IReadOnlyList<HistoryEntry>> LoadAsync(CancellationToken cancellationToken = default);
    Task AddAsync(HistoryEntry entry, CancellationToken cancellationToken = default);
    Task RemoveAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface IQueueStore
{
    Task<IReadOnlyList<DownloadJob>> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(IReadOnlyCollection<DownloadJob> jobs, CancellationToken cancellationToken = default);
}

public interface IAppLogger
{
    void Information(string message, object? data = null);
    void Warning(string message, object? data = null);
    void Error(string message, Exception? exception = null, object? data = null);
}
