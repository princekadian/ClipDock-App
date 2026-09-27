namespace ClipDock.Infrastructure;

public sealed class AppPaths
{
    public AppPaths()
    {
        Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipDock");
        Tools = Path.Combine(Root, "Tools");
        Logs = Path.Combine(Root, "Logs");
        Data = Path.Combine(Root, "Data");
        FfmpegDirectory = Tools;
    }

    public string Root { get; }
    public string Tools { get; }
    public string Logs { get; }
    public string Data { get; }
    public string YtDlp => Path.Combine(Tools, "yt-dlp.exe");
    public string FfmpegDirectory { get; private set; }
    public string Ffmpeg => Path.Combine(FfmpegDirectory, "ffmpeg.exe");
    public string Ffprobe => Path.Combine(FfmpegDirectory, "ffprobe.exe");

    public void UseFfmpegDirectory(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        if (!File.Exists(Path.Combine(fullPath, "ffmpeg.exe")) || !File.Exists(Path.Combine(fullPath, "ffprobe.exe")))
            throw new DirectoryNotFoundException("The selected FFmpeg directory is incomplete.");
        FfmpegDirectory = fullPath;
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Tools);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Data);
    }
}
