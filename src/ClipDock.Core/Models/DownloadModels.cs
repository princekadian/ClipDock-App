namespace ClipDock.Core.Models;

public sealed class DownloadJob
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Url { get; init; }
    public required string VideoId { get; init; }
    public required string Title { get; init; }
    public required string Channel { get; init; }
    public string? ThumbnailUrl { get; init; }
    public required int Height { get; init; }
    public required OutputContainer Container { get; init; }
    public required string FormatExpression { get; init; }
    public required string OutputDirectory { get; init; }
    public required string FilenameTemplate { get; init; }
    public long? EstimatedBytes { get; init; }
    public DownloadState State { get; set; } = DownloadState.Waiting;
    public double Progress { get; set; }
    public long DownloadedBytes { get; set; }
    public long? TotalBytes { get; set; }
    public double? BytesPerSecond { get; set; }
    public TimeSpan? Eta { get; set; }
    public string? OutputPath { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
}

public sealed record DownloadProgress(
    double Percentage,
    long DownloadedBytes,
    long? TotalBytes,
    double? BytesPerSecond,
    TimeSpan? Eta,
    DownloadState State,
    string? OutputPath = null);

public sealed record DownloadResult(bool Succeeded, string? OutputPath, string? UserMessage, string? TechnicalDetails);

public sealed record HistoryEntry(
    Guid Id,
    string Title,
    int Height,
    OutputContainer Container,
    long? FileSize,
    string FilePath,
    DateTimeOffset CompletedAt);
