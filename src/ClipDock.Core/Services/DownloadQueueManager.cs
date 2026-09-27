using System.Collections.Concurrent;
using System.Threading.Channels;
using ClipDock.Core.Models;

namespace ClipDock.Core.Services;

public sealed class DownloadQueueManager : IAsyncDisposable
{
    private readonly IDownloadEngine _engine;
    private readonly IHistoryStore _history;
    private readonly IQueueStore _queueStore;
    private readonly IAppLogger _logger;
    private readonly Channel<Guid> _pending = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = false
    });
    private readonly ConcurrentDictionary<Guid, DownloadJob> _jobs = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _active = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<Task> _workers = [];

    public DownloadQueueManager(
        IDownloadEngine engine,
        IHistoryStore history,
        IQueueStore queueStore,
        IAppLogger logger,
        int concurrency)
    {
        _engine = engine;
        _history = history;
        _queueStore = queueStore;
        _logger = logger;
        for (var index = 0; index < Math.Clamp(concurrency, 1, 3); index++)
        {
            _workers.Add(Task.Run(() => WorkerAsync(_shutdown.Token)));
        }
    }

    public event EventHandler<DownloadJob>? JobChanged;
    public IReadOnlyCollection<DownloadJob> Jobs => _jobs.Values.OrderBy(job => job.CreatedAt).ToArray();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var stored = await _queueStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        foreach (var job in stored.Where(job => job.State is not DownloadState.Completed and not DownloadState.Cancelled))
        {
            if (job.State is DownloadState.Preparing or DownloadState.Downloading or DownloadState.Merging)
            {
                job.State = DownloadState.Waiting;
            }

            _jobs[job.Id] = job;
            if (job.State == DownloadState.Waiting)
            {
                await _pending.Writer.WriteAsync(job.Id, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task EnqueueAsync(DownloadJob job, CancellationToken cancellationToken = default)
    {
        if (!_jobs.TryAdd(job.Id, job))
        {
            throw new InvalidOperationException("The download already exists in the queue.");
        }

        await PersistAsync(cancellationToken).ConfigureAwait(false);
        OnJobChanged(job);
        await _pending.Writer.WriteAsync(job.Id, cancellationToken).ConfigureAwait(false);
    }

    public async Task PauseAsync(Guid id)
    {
        if (!_jobs.TryGetValue(id, out var job) || job.State is DownloadState.Completed or DownloadState.Cancelled)
        {
            return;
        }

        job.State = DownloadState.Paused;
        if (_active.TryGetValue(id, out var cancellation))
        {
            cancellation.Cancel();
        }

        await PersistAsync().ConfigureAwait(false);
        OnJobChanged(job);
    }

    public async Task ResumeAsync(Guid id)
    {
        if (!_jobs.TryGetValue(id, out var job) || job.State is not (DownloadState.Paused or DownloadState.Failed))
        {
            return;
        }

        job.State = DownloadState.Waiting;
        job.ErrorMessage = null;
        await PersistAsync().ConfigureAwait(false);
        OnJobChanged(job);
        await _pending.Writer.WriteAsync(id).ConfigureAwait(false);
    }

    public async Task CancelAsync(Guid id)
    {
        if (!_jobs.TryGetValue(id, out var job))
        {
            return;
        }

        job.State = DownloadState.Cancelled;
        if (_active.TryGetValue(id, out var cancellation))
        {
            cancellation.Cancel();
        }

        await PersistAsync().ConfigureAwait(false);
        OnJobChanged(job);
    }

    private async Task WorkerAsync(CancellationToken shutdown)
    {
        await foreach (var id in _pending.Reader.ReadAllAsync(shutdown).ConfigureAwait(false))
        {
            if (!_jobs.TryGetValue(id, out var job) || job.State != DownloadState.Waiting)
            {
                continue;
            }

            using var operation = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
            if (!_active.TryAdd(id, operation))
            {
                continue;
            }

            try
            {
                job.State = DownloadState.Preparing;
                OnJobChanged(job);
                await PersistAsync(shutdown).ConfigureAwait(false);

                var progress = new InlineProgress<DownloadProgress>(value =>
                {
                    job.Progress = value.Percentage;
                    job.DownloadedBytes = value.DownloadedBytes;
                    job.TotalBytes = value.TotalBytes ?? job.EstimatedBytes;
                    job.BytesPerSecond = value.BytesPerSecond;
                    job.Eta = value.Eta;
                    job.State = value.State;
                    job.OutputPath = value.OutputPath ?? job.OutputPath;
                    OnJobChanged(job);
                });
                var result = await _engine.DownloadAsync(job, progress, operation.Token).ConfigureAwait(false);

                if (job.State is DownloadState.Paused or DownloadState.Cancelled)
                {
                    continue;
                }

                if (!result.Succeeded || string.IsNullOrWhiteSpace(result.OutputPath))
                {
                    job.State = DownloadState.Failed;
                    job.ErrorMessage = result.UserMessage ?? "The download could not be completed.";
                    _logger.Warning("Download failed", new { job.Id, result.TechnicalDetails });
                }
                else
                {
                    job.State = DownloadState.Completed;
                    job.Progress = 100;
                    job.OutputPath = result.OutputPath;
                    var size = File.Exists(result.OutputPath) ? new FileInfo(result.OutputPath).Length : job.TotalBytes;
                    await _history.AddAsync(new HistoryEntry(
                        job.Id, job.Title, job.Height, job.Container, size, result.OutputPath, DateTimeOffset.Now), shutdown).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (job.State is DownloadState.Paused or DownloadState.Cancelled || shutdown.IsCancellationRequested)
            {
                // State was set by the initiating operation; yt-dlp leaves resumable part files intact.
            }
            catch (Exception exception)
            {
                job.State = DownloadState.Failed;
                job.ErrorMessage = "An unexpected error stopped this download.";
                _logger.Error("Unhandled queue worker failure", exception, new { job.Id });
            }
            finally
            {
                _active.TryRemove(id, out _);
                if (!shutdown.IsCancellationRequested)
                {
                    await PersistAsync().ConfigureAwait(false);
                    OnJobChanged(job);
                }
            }
        }
    }

    private Task PersistAsync(CancellationToken cancellationToken = default) =>
        _queueStore.SaveAsync(_jobs.Values.ToArray(), cancellationToken);

    private void OnJobChanged(DownloadJob job) => JobChanged?.Invoke(this, job);

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _pending.Writer.TryComplete();
        foreach (var cancellation in _active.Values)
        {
            cancellation.Cancel();
        }

        try
        {
            await Task.WhenAll(_workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _shutdown.Dispose();
    }
}
