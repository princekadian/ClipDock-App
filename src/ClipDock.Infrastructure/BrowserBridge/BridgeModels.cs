using ClipDock.Core.Models;

namespace ClipDock.Infrastructure.BrowserBridge;

public sealed record AnalyzeRequest(string Url, OutputContainer Container = OutputContainer.Mp4);
public sealed record DownloadRequest(string Url, int Height, OutputContainer Container = OutputContainer.Mp4);
public sealed record PairingInfo(string Token, int Port);

public sealed record BridgeVideoResponse(
    string Id,
    string Title,
    string Channel,
    double DurationSeconds,
    string? ThumbnailUrl,
    IReadOnlyList<BridgeQualityResponse> Qualities);

public sealed record BridgeQualityResponse(int Height, string Label, bool Available, long? EstimatedBytes, bool ExactSize);
public sealed record BridgeDownloadResponse(Guid JobId, string Status);
public sealed record BridgeStatusResponse(Guid JobId, string Status, double Progress, long DownloadedBytes, long? TotalBytes);
public sealed record BridgeErrorResponse(string Error, string? Detail = null);
