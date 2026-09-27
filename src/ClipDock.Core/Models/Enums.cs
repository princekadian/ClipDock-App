namespace ClipDock.Core.Models;

public enum OutputContainer
{
    Mp4,
    Mkv,
    WebM
}

public enum DownloadState
{
    Waiting,
    Preparing,
    Downloading,
    Paused,
    Merging,
    Completed,
    Failed,
    Cancelled
}
