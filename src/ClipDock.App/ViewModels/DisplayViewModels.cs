using ClipDock.Core.Models;

namespace ClipDock.App.ViewModels;

public sealed class QualityItemViewModel(QualityOption option) : ObservableObject
{
    public QualityOption Option { get; } = option;
    public int Height => Option.Height;
    public string Label => Option.Label;
    public string Detail => Height switch { 2160 => "Ultra HD", 1440 => "Quad HD", 1080 => "Full HD", 720 => "HD", _ => "Standard" };
    public string SizeLabel => Option.SizeLabel;
    public bool IsAvailable => Option.IsAvailable;
}

public sealed class DownloadItemViewModel : ObservableObject
{
    private DownloadJob _job;
    public DownloadItemViewModel(DownloadJob job) => _job = job;
    public Guid Id => _job.Id;
    public string Title => _job.Title;
    public string Subtitle => $"{_job.Height}p  •  {_job.Container.ToString().ToUpperInvariant()}";
    public string? ThumbnailUrl => _job.ThumbnailUrl;
    public double Progress => _job.Progress;
    public string ProgressLabel => _job.State switch
    {
        DownloadState.Waiting => "Waiting",
        DownloadState.Preparing => "Preparing engine",
        DownloadState.Merging => "Merging streams",
        DownloadState.Paused => "Paused",
        DownloadState.Completed => "Complete",
        DownloadState.Failed => "Failed",
        DownloadState.Cancelled => "Cancelled",
        _ => $"{_job.Progress:0}%"
    };
    public string StateLabel => _job.State.ToString();
    public string TransferLabel
    {
        get
        {
            var amount = $"{ByteSize.Format(_job.DownloadedBytes)} / {(_job.TotalBytes.HasValue ? ByteSize.Format(_job.TotalBytes.Value) : "Unknown")}";
            var speed = _job.BytesPerSecond.HasValue ? $"  •  {ByteSize.Format((long)_job.BytesPerSecond.Value)}/s" : string.Empty;
            var eta = _job.Eta.HasValue ? $"  •  {FormatEta(_job.Eta.Value)} left" : string.Empty;
            return amount + speed + eta;
        }
    }
    public bool CanPause => _job.State == DownloadState.Downloading;
    public bool CanResume => _job.State is DownloadState.Paused or DownloadState.Failed;
    public bool CanCancel => _job.State is DownloadState.Waiting or DownloadState.Preparing or DownloadState.Downloading or DownloadState.Paused or DownloadState.Failed;
    public bool CanOpenFolder => _job.State == DownloadState.Completed && !string.IsNullOrWhiteSpace(_job.OutputPath);
    public string? OutputPath => _job.OutputPath;
    public string OutputDirectory => _job.OutputDirectory;
    public string? ErrorMessage => _job.ErrorMessage;

    public void Update(DownloadJob job)
    {
        _job = job;
        OnPropertyChanged(string.Empty);
    }

    private static string FormatEta(TimeSpan value) => value.TotalHours >= 1 ? value.ToString("h\\:mm\\:ss") : value.ToString("m\\:ss");
}

public sealed class HistoryItemViewModel(HistoryEntry entry)
{
    public HistoryEntry Entry { get; } = entry;
    public Guid Id => Entry.Id;
    public string Title => Entry.Title;
    public string Subtitle => $"{Entry.Height}p  •  {Entry.Container.ToString().ToUpperInvariant()}  •  {(Entry.FileSize.HasValue ? ByteSize.Format(Entry.FileSize.Value) : "Size unknown")}";
    public string CompletedLabel => Entry.CompletedAt.Date == DateTimeOffset.Now.Date
        ? $"Completed today at {Entry.CompletedAt:t}"
        : $"Completed {Entry.CompletedAt:g}";
}
