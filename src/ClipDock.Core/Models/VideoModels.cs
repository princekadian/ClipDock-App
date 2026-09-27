namespace ClipDock.Core.Models;

public sealed record MediaFormat(
    string Id,
    int? Height,
    string Extension,
    string? VideoCodec,
    string? AudioCodec,
    double? VideoBitrateKbps,
    double? AudioBitrateKbps,
    long? FileSize,
    long? ApproximateFileSize,
    bool IsVideoOnly,
    bool IsAudioOnly,
    double? FramesPerSecond,
    string? Protocol);

public sealed record VideoMetadata(
    string Id,
    string Url,
    string Title,
    string Channel,
    TimeSpan Duration,
    string? ThumbnailUrl,
    DateOnly? UploadDate,
    IReadOnlyList<MediaFormat> Formats);

public sealed record QualityOption(
    int Height,
    string Label,
    bool IsAvailable,
    long? EstimatedBytes,
    bool IsExactSize,
    string? FormatExpression,
    string? VideoCodec,
    string? AudioCodec)
{
    public string SizeLabel => !IsAvailable
        ? "Unavailable"
        : EstimatedBytes is null
            ? "Size unknown"
            : $"{(IsExactSize ? string.Empty : "~")}{ByteSize.Format(EstimatedBytes.Value)}";
}

public static class ByteSize
{
    public static string Format(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var format = unit == 0 || value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00";
        return $"{value.ToString(format, System.Globalization.CultureInfo.CurrentCulture)} {units[unit]}";
    }
}
