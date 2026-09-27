using ClipDock.Core.Models;

namespace ClipDock.Core.Services;

public static class FormatPlanner
{
    public static IReadOnlyList<QualityOption> BuildOptions(VideoMetadata metadata, OutputContainer container)
    {
        return SupportedQualities.All.Select(height => BuildOption(metadata, height, container)).ToArray();
    }

    private static QualityOption BuildOption(VideoMetadata metadata, int targetHeight, OutputContainer container)
    {
        var candidates = metadata.Formats
            .Where(format => format.Height == targetHeight && !format.IsAudioOnly && IsContainerCompatible(format, container))
            .OrderByDescending(format => CodecScore(format, container))
            .ThenByDescending(format => format.VideoBitrateKbps ?? 0)
            .ThenByDescending(format => format.FileSize ?? format.ApproximateFileSize ?? 0)
            .ToArray();

        if (candidates.Length == 0)
        {
            return new QualityOption(targetHeight, SupportedQualities.Label(targetHeight), false, null, false, null, null, null);
        }

        var video = candidates[0];
        MediaFormat? audio = null;
        var needsAudio = video.IsVideoOnly || string.Equals(video.AudioCodec, "none", StringComparison.OrdinalIgnoreCase);
        if (needsAudio)
        {
            audio = metadata.Formats
                .Where(format => format.IsAudioOnly && IsAudioCompatible(format, container))
                .OrderByDescending(format => AudioScore(format, container))
                .ThenByDescending(format => format.AudioBitrateKbps ?? 0)
                .FirstOrDefault();
            if (audio is null)
            {
                return new QualityOption(targetHeight, SupportedQualities.Label(targetHeight), false, null, false, null, null, null);
            }
        }

        var (size, exact) = CalculateSize(metadata.Duration, video, audio);
        var expression = audio is null ? video.Id : $"{video.Id}+{audio.Id}";
        return new QualityOption(
            targetHeight,
            SupportedQualities.Label(targetHeight),
            true,
            size,
            exact,
            expression,
            video.VideoCodec,
            audio?.AudioCodec ?? video.AudioCodec);
    }

    private static (long? Bytes, bool Exact) CalculateSize(TimeSpan duration, MediaFormat video, MediaFormat? audio)
    {
        var videoExact = video.FileSize;
        var audioExact = audio?.FileSize;
        if (videoExact.HasValue && (audio is null || audioExact.HasValue))
        {
            return (videoExact.Value + (audioExact ?? 0), true);
        }

        var knownVideo = video.FileSize ?? video.ApproximateFileSize;
        var knownAudio = audio?.FileSize ?? audio?.ApproximateFileSize;
        if (knownVideo.HasValue && (audio is null || knownAudio.HasValue))
        {
            return (knownVideo.Value + (knownAudio ?? 0), false);
        }

        var totalKbps = (video.VideoBitrateKbps ?? 0) + (audio?.AudioBitrateKbps ?? video.AudioBitrateKbps ?? 0);
        if (totalKbps <= 0 || duration <= TimeSpan.Zero)
        {
            return (null, false);
        }

        return ((long)(totalKbps * 1000d / 8d * duration.TotalSeconds), false);
    }

    private static bool IsContainerCompatible(MediaFormat format, OutputContainer container)
    {
        var codec = format.VideoCodec ?? string.Empty;
        return container switch
        {
            OutputContainer.Mp4 => codec.StartsWith("avc1", StringComparison.OrdinalIgnoreCase) ||
                                   codec.StartsWith("h264", StringComparison.OrdinalIgnoreCase) ||
                                   codec.StartsWith("av01", StringComparison.OrdinalIgnoreCase) ||
                                   codec.StartsWith("vp9", StringComparison.OrdinalIgnoreCase),
            OutputContainer.WebM => codec.StartsWith("vp9", StringComparison.OrdinalIgnoreCase) ||
                                    codec.StartsWith("vp0", StringComparison.OrdinalIgnoreCase) ||
                                    codec.StartsWith("av01", StringComparison.OrdinalIgnoreCase),
            _ => true
        };
    }

    private static bool IsAudioCompatible(MediaFormat format, OutputContainer container)
    {
        var codec = format.AudioCodec ?? string.Empty;
        return container switch
        {
            OutputContainer.Mp4 => codec.StartsWith("mp4a", StringComparison.OrdinalIgnoreCase) ||
                                   codec.StartsWith("aac", StringComparison.OrdinalIgnoreCase) ||
                                   codec.StartsWith("opus", StringComparison.OrdinalIgnoreCase),
            OutputContainer.WebM => codec.StartsWith("opus", StringComparison.OrdinalIgnoreCase) ||
                                    codec.StartsWith("vorbis", StringComparison.OrdinalIgnoreCase),
            _ => true
        };
    }

    private static int CodecScore(MediaFormat format, OutputContainer container)
    {
        var codec = format.VideoCodec ?? string.Empty;
        if (container == OutputContainer.Mp4 && codec.StartsWith("avc1", StringComparison.OrdinalIgnoreCase)) return 4;
        if (container == OutputContainer.Mp4 && codec.StartsWith("av01", StringComparison.OrdinalIgnoreCase)) return 3;
        if (container == OutputContainer.WebM && codec.StartsWith("vp9", StringComparison.OrdinalIgnoreCase)) return 4;
        return 1;
    }

    private static int AudioScore(MediaFormat format, OutputContainer container)
    {
        var codec = format.AudioCodec ?? string.Empty;
        if (container == OutputContainer.Mp4 && codec.StartsWith("mp4a", StringComparison.OrdinalIgnoreCase)) return 3;
        if (container == OutputContainer.WebM && codec.StartsWith("opus", StringComparison.OrdinalIgnoreCase)) return 3;
        return 1;
    }
}
