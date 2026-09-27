namespace ClipDock.Core.Models;

public sealed class AppSettings
{
    public string DownloadDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "ClipDock");
    public int DefaultHeight { get; set; } = 1080;
    public OutputContainer DefaultContainer { get; set; } = OutputContainer.Mp4;
    public int MaximumConcurrentDownloads { get; set; } = 2;
    public string FilenameTemplate { get; set; } = "%(title)s.%(ext)s";
    public bool ShowNotifications { get; set; } = true;
    public bool ConfirmBeforeCancelling { get; set; } = true;
    public bool MinimizeToTray { get; set; }
    public bool StartWithWindows { get; set; }
    public string BridgeToken { get; set; } = string.Empty;
    public string? AllowedExtensionId { get; set; }
    public string? WallpaperPath { get; set; }

    public void Normalize()
    {
        MaximumConcurrentDownloads = Math.Clamp(MaximumConcurrentDownloads, 1, 3);
        DefaultHeight = SupportedQualities.All.Contains(DefaultHeight) ? DefaultHeight : 1080;
        if (string.IsNullOrWhiteSpace(DownloadDirectory))
        {
            DownloadDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "ClipDock");
        }

        if (!FilenameTemplate.Contains("%(title)s", StringComparison.Ordinal) ||
            FilenameTemplate.Contains("..", StringComparison.Ordinal) ||
            FilenameTemplate.IndexOfAny(Path.GetInvalidPathChars()) >= 0 ||
            FilenameTemplate.Contains('/') || FilenameTemplate.Contains('\\'))
        {
            FilenameTemplate = "%(title)s.%(ext)s";
        }

        if (string.IsNullOrWhiteSpace(WallpaperPath)) WallpaperPath = null;
    }
}

public static class SupportedQualities
{
    public static readonly IReadOnlyList<int> All = [2160, 1440, 1080, 720, 480, 360, 240, 144];

    public static string Label(int height) => height switch
    {
        2160 => "4K",
        1440 => "1440p",
        1080 => "1080p",
        720 => "720p",
        _ => $"{height}p"
    };
}
