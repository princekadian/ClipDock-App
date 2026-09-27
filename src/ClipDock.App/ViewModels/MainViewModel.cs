using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using ClipDock.Core.Models;
using ClipDock.Core.Services;
using ClipDock.Infrastructure;
using Microsoft.Win32;

namespace ClipDock.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly IVideoAnalyzer _analyzer;
    private readonly DownloadQueueManager _queue;
    private readonly IHistoryStore _historyStore;
    private readonly ISettingsStore _settingsStore;
    private readonly IEngineManager _engineManager;
    private readonly AppPaths _paths;
    private VideoMetadata? _metadata;
    private string _currentSection = "Downloads";
    private string _url = string.Empty;
    private string _statusMessage = "Paste a YouTube link to inspect available formats.";
    private string? _errorMessage;
    private bool _isAnalyzing;
    private QualityItemViewModel? _selectedQuality;
    private OutputContainer _selectedContainer;
    private string? _ytDlpVersion;
    private string? _ffmpegVersion;
    private bool _isYtDlpBusy;
    private bool _isFfmpegBusy;

    public MainViewModel(
        IVideoAnalyzer analyzer,
        DownloadQueueManager queue,
        IHistoryStore historyStore,
        ISettingsStore settingsStore,
        IEngineManager engineManager,
        AppPaths paths,
        AppSettings settings)
    {
        _analyzer = analyzer;
        _queue = queue;
        _historyStore = historyStore;
        _settingsStore = settingsStore;
        _engineManager = engineManager;
        _paths = paths;
        Settings = settings;
        _selectedContainer = settings.DefaultContainer;
        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync, () => !IsAnalyzing && !string.IsNullOrWhiteSpace(Url));
        DownloadCommand = new AsyncRelayCommand(DownloadAsync, () => SelectedQuality?.IsAvailable == true && _metadata is not null);
        NavigateCommand = new RelayCommand<string>(section => CurrentSection = section);
        PauseCommand = new AsyncRelayCommand<DownloadItemViewModel>(item => _queue.PauseAsync(item.Id), item => item.CanPause);
        ResumeCommand = new AsyncRelayCommand<DownloadItemViewModel>(item => _queue.ResumeAsync(item.Id), item => item.CanResume);
        CancelCommand = new AsyncRelayCommand<DownloadItemViewModel>(CancelAsync, item => item.CanCancel);
        OpenQueueFolderCommand = new RelayCommand<DownloadItemViewModel>(item => OpenFolder(item.OutputPath, item.OutputDirectory), item => item.CanOpenFolder);
        OpenFileCommand = new RelayCommand<HistoryItemViewModel>(item => OpenPath(item.Entry.FilePath));
        OpenFolderCommand = new RelayCommand<HistoryItemViewModel>(item => OpenFolder(item.Entry.FilePath));
        CopyPathCommand = new RelayCommand<HistoryItemViewModel>(item => Clipboard.SetText(item.Entry.FilePath));
        RemoveHistoryCommand = new AsyncRelayCommand<HistoryItemViewModel>(RemoveHistoryAsync);
        BrowseCommand = new RelayCommand(Browse);
        ChooseWallpaperCommand = new AsyncRelayCommand(ChooseWallpaperAsync);
        RemoveWallpaperCommand = new AsyncRelayCommand(RemoveWallpaperAsync);
        SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync);
        CopyTokenCommand = new RelayCommand(() => Clipboard.SetText(Settings.BridgeToken));
        OpenLogsCommand = new RelayCommand(() => OpenPath(_paths.Logs));
        UpdateEngineCommand = new AsyncRelayCommand(ManageYtDlpAsync);
        InstallFfmpegCommand = new AsyncRelayCommand(InstallFfmpegAsync);
        _queue.JobChanged += QueueOnJobChanged;
    }

    public AppSettings Settings { get; }
    public ObservableCollection<QualityItemViewModel> Qualities { get; } = [];
    public ObservableCollection<DownloadItemViewModel> QueueItems { get; } = [];
    public ObservableCollection<HistoryItemViewModel> HistoryItems { get; } = [];
    public IReadOnlyList<OutputContainer> Containers { get; } = Enum.GetValues<OutputContainer>();
    public IReadOnlyList<int> ConcurrentOptions { get; } = [1, 2, 3];
    public IReadOnlyList<int> DefaultQualityOptions { get; } = SupportedQualities.All;
    public AsyncRelayCommand AnalyzeCommand { get; }
    public AsyncRelayCommand DownloadCommand { get; }
    public RelayCommand<string> NavigateCommand { get; }
    public AsyncRelayCommand<DownloadItemViewModel> PauseCommand { get; }
    public AsyncRelayCommand<DownloadItemViewModel> ResumeCommand { get; }
    public AsyncRelayCommand<DownloadItemViewModel> CancelCommand { get; }
    public RelayCommand<DownloadItemViewModel> OpenQueueFolderCommand { get; }
    public RelayCommand<HistoryItemViewModel> OpenFileCommand { get; }
    public RelayCommand<HistoryItemViewModel> OpenFolderCommand { get; }
    public RelayCommand<HistoryItemViewModel> CopyPathCommand { get; }
    public AsyncRelayCommand<HistoryItemViewModel> RemoveHistoryCommand { get; }
    public RelayCommand BrowseCommand { get; }
    public AsyncRelayCommand ChooseWallpaperCommand { get; }
    public AsyncRelayCommand RemoveWallpaperCommand { get; }
    public AsyncRelayCommand SaveSettingsCommand { get; }
    public RelayCommand CopyTokenCommand { get; }
    public RelayCommand OpenLogsCommand { get; }
    public AsyncRelayCommand UpdateEngineCommand { get; }
    public AsyncRelayCommand InstallFfmpegCommand { get; }

    public string CurrentSection { get => _currentSection; set => SetProperty(ref _currentSection, value); }
    public string Url
    {
        get => _url;
        set { if (SetProperty(ref _url, value)) AnalyzeCommand.RaiseCanExecuteChanged(); }
    }
    public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }
    public string? ErrorMessage { get => _errorMessage; set => SetProperty(ref _errorMessage, value); }
    public bool IsAnalyzing { get => _isAnalyzing; set { if (SetProperty(ref _isAnalyzing, value)) AnalyzeCommand.RaiseCanExecuteChanged(); } }
    public bool HasVideo => _metadata is not null;
    public string VideoTitle => _metadata?.Title ?? string.Empty;
    public string VideoChannel => _metadata?.Channel ?? string.Empty;
    public string VideoDuration => _metadata is null ? string.Empty : FormatDuration(_metadata.Duration);
    public string? ThumbnailUrl => _metadata?.ThumbnailUrl;
    public QualityItemViewModel? SelectedQuality
    {
        get => _selectedQuality;
        set { if (SetProperty(ref _selectedQuality, value)) DownloadCommand.RaiseCanExecuteChanged(); }
    }
    public OutputContainer SelectedContainer
    {
        get => _selectedContainer;
        set
        {
            if (SetProperty(ref _selectedContainer, value) && _metadata is not null) PopulateQualities(_metadata);
        }
    }
    public string EngineStatus => $"yt-dlp: {YtDlpVersion ?? "Installed on first use"}\nFFmpeg: {FfmpegVersion ?? "Installed on first download"}";
    public string? YtDlpVersion
    {
        get => _ytDlpVersion;
        private set
        {
            if (!SetProperty(ref _ytDlpVersion, value)) return;
            OnPropertyChanged(nameof(EngineStatus));
            OnPropertyChanged(nameof(IsYtDlpInstalled));
            OnPropertyChanged(nameof(YtDlpStatus));
            OnPropertyChanged(nameof(YtDlpActionLabel));
        }
    }
    public string? FfmpegVersion
    {
        get => _ffmpegVersion;
        private set
        {
            if (!SetProperty(ref _ffmpegVersion, value)) return;
            OnPropertyChanged(nameof(EngineStatus));
            OnPropertyChanged(nameof(IsFfmpegInstalled));
            OnPropertyChanged(nameof(FfmpegStatus));
        }
    }
    public bool IsYtDlpInstalled => !string.IsNullOrWhiteSpace(YtDlpVersion);
    public bool IsFfmpegInstalled => !string.IsNullOrWhiteSpace(FfmpegVersion);
    public string YtDlpStatus => _isYtDlpBusy
        ? (IsYtDlpInstalled ? "Checking for updates..." : "Downloading and installing...")
        : IsYtDlpInstalled ? $"Installed - {YtDlpVersion}" : "Not installed";
    public string FfmpegStatus => _isFfmpegBusy
        ? "Downloading and installing..."
        : IsFfmpegInstalled ? $"Installed - {ShortFfmpegVersion(FfmpegVersion!)}" : "Not installed";
    public string YtDlpActionLabel => IsYtDlpInstalled ? "Check for updates" : "Download and install";
    public string? WallpaperPath => Settings.WallpaperPath;
    public bool HasWallpaper => !string.IsNullOrWhiteSpace(Settings.WallpaperPath);
    public string WallpaperLabel => HasWallpaper
        ? Path.GetFileName(Settings.WallpaperPath) ?? Settings.WallpaperPath ?? "Selected wallpaper"
        : "No wallpaper selected";

    public async Task InitializeAsync()
    {
        await _queue.InitializeAsync();
        foreach (var job in _queue.Jobs) QueueItems.Add(new DownloadItemViewModel(job));
        var history = await _historyStore.LoadAsync();
        foreach (var entry in history) HistoryItems.Add(new HistoryItemViewModel(entry));
        YtDlpVersion = await _engineManager.GetYtDlpVersionAsync(CancellationToken.None);
        FfmpegVersion = await _engineManager.GetFfmpegVersionAsync(CancellationToken.None);
    }

    private async Task AnalyzeAsync()
    {
        ErrorMessage = null;
        if (!YouTubeUrlValidator.TryNormalize(Url, out var normalized))
        {
            ErrorMessage = "Enter a YouTube watch, Shorts, or youtu.be link.";
            return;
        }

        IsAnalyzing = true;
        StatusMessage = "Analyzing video and available formats...";
        try
        {
            _metadata = await _analyzer.AnalyzeAsync(normalized, CancellationToken.None);
            Url = normalized;
            PopulateQualities(_metadata);
            StatusMessage = "Choose a quality and start the download.";
            OnPropertyChanged(nameof(HasVideo));
            OnPropertyChanged(nameof(VideoTitle));
            OnPropertyChanged(nameof(VideoChannel));
            OnPropertyChanged(nameof(VideoDuration));
            OnPropertyChanged(nameof(ThumbnailUrl));
            DownloadCommand.RaiseCanExecuteChanged();
        }
        catch (Exception exception) when (exception is VideoAnalysisException or HttpRequestException or IOException)
        {
            _metadata = null;
            Qualities.Clear();
            ErrorMessage = exception.Message;
            StatusMessage = "The video could not be analyzed.";
            OnPropertyChanged(nameof(HasVideo));
        }
        finally { IsAnalyzing = false; }
    }

    private void PopulateQualities(VideoMetadata metadata)
    {
        Qualities.Clear();
        foreach (var option in FormatPlanner.BuildOptions(metadata, SelectedContainer))
            Qualities.Add(new QualityItemViewModel(option));
        SelectedQuality = Qualities.FirstOrDefault(item => item.Height == Settings.DefaultHeight && item.IsAvailable)
            ?? Qualities.FirstOrDefault(item => item.IsAvailable);
    }

    private async Task DownloadAsync()
    {
        if (_metadata is null || SelectedQuality?.Option.FormatExpression is not { } expression) return;
        var job = new DownloadJob
        {
            Url = _metadata.Url,
            VideoId = _metadata.Id,
            Title = _metadata.Title,
            Channel = _metadata.Channel,
            ThumbnailUrl = _metadata.ThumbnailUrl,
            Height = SelectedQuality.Height,
            Container = SelectedContainer,
            FormatExpression = expression,
            OutputDirectory = Settings.DownloadDirectory,
            FilenameTemplate = Settings.FilenameTemplate,
            EstimatedBytes = SelectedQuality.Option.EstimatedBytes
        };
        await _queue.EnqueueAsync(job);
        CurrentSection = "Queue";
    }

    private async Task CancelAsync(DownloadItemViewModel item)
    {
        if (Settings.ConfirmBeforeCancelling)
        {
            var answer = MessageBox.Show("Cancel this download? Partial data will remain available if you download it again.",
                "Cancel download", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
        }
        await _queue.CancelAsync(item.Id);
    }

    private async Task RemoveHistoryAsync(HistoryItemViewModel item)
    {
        await _historyStore.RemoveAsync(item.Id);
        HistoryItems.Remove(item);
    }

    private void QueueOnJobChanged(object? sender, DownloadJob job)
    {
        Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            var item = QueueItems.FirstOrDefault(value => value.Id == job.Id);
            if (item is null) QueueItems.Add(new DownloadItemViewModel(job));
            else item.Update(job);
            PauseCommand.RaiseCanExecuteChanged();
            ResumeCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
            OpenQueueFolderCommand.RaiseCanExecuteChanged();
            if (job.State == DownloadState.Completed)
            {
                var history = await _historyStore.LoadAsync();
                HistoryItems.Clear();
                foreach (var entry in history) HistoryItems.Add(new HistoryItemViewModel(entry));
            }
        });
    }

    private void Browse()
    {
        var dialog = new OpenFolderDialog { Title = "Choose download location", InitialDirectory = Settings.DownloadDirectory };
        if (dialog.ShowDialog() == true) Settings.DownloadDirectory = dialog.FolderName;
        OnPropertyChanged(nameof(Settings));
    }

    private async Task ChooseWallpaperAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose background wallpaper",
            Filter = "Image files|*.jpg;*.jpeg;*.png;*.bmp|JPEG images|*.jpg;*.jpeg|PNG images|*.png|Bitmap images|*.bmp",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() != true) return;
        Settings.WallpaperPath = dialog.FileName;
        await _settingsStore.SaveAsync(Settings);
        NotifyWallpaperChanged();
    }

    private async Task RemoveWallpaperAsync()
    {
        Settings.WallpaperPath = null;
        await _settingsStore.SaveAsync(Settings);
        NotifyWallpaperChanged();
    }

    private void NotifyWallpaperChanged()
    {
        OnPropertyChanged(nameof(WallpaperPath));
        OnPropertyChanged(nameof(HasWallpaper));
        OnPropertyChanged(nameof(WallpaperLabel));
    }

    private async Task SaveSettingsAsync()
    {
        Settings.Normalize();
        await _settingsStore.SaveAsync(Settings);
        StatusMessage = "Settings saved.";
    }

    private async Task ManageYtDlpAsync()
    {
        _isYtDlpBusy = true;
        OnPropertyChanged(nameof(YtDlpStatus));
        try
        {
            if (IsYtDlpInstalled) await _engineManager.UpdateYtDlpAsync(CancellationToken.None);
            else await _engineManager.EnsureYtDlpAsync(CancellationToken.None);
            YtDlpVersion = await _engineManager.GetYtDlpVersionAsync(CancellationToken.None);
            StatusMessage = "yt-dlp is up to date.";
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Engine update failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _isYtDlpBusy = false;
            OnPropertyChanged(nameof(YtDlpStatus));
        }
    }

    private async Task InstallFfmpegAsync()
    {
        _isFfmpegBusy = true;
        OnPropertyChanged(nameof(FfmpegStatus));
        try
        {
            await _engineManager.EnsureFfmpegAsync(CancellationToken.None);
            FfmpegVersion = await _engineManager.GetFfmpegVersionAsync(CancellationToken.None);
            if (!IsFfmpegInstalled) throw new InvalidOperationException("FFmpeg was installed but its version could not be verified.");
            StatusMessage = "FFmpeg is installed and ready.";
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "FFmpeg installation failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _isFfmpegBusy = false;
            OnPropertyChanged(nameof(FfmpegStatus));
        }
    }

    private static void OpenPath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private static void OpenFolder(string? filePath, string? preferredDirectory = null)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = true });
                return;
            }

            var directory = !string.IsNullOrWhiteSpace(preferredDirectory)
                ? preferredDirectory
                : !string.IsNullOrWhiteSpace(filePath) ? Path.GetDirectoryName(filePath) : null;
            if (string.IsNullOrWhiteSpace(directory))
                throw new DirectoryNotFoundException("The download location is unavailable.");

            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo("explorer.exe", directory) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show("The download folder could not be opened.\n\n" + exception.Message,
                "Open folder", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1 ? duration.ToString("h\\:mm\\:ss") : duration.ToString("m\\:ss");

    private static string ShortFfmpegVersion(string value)
    {
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 && parts[0].Equals("ffmpeg", StringComparison.OrdinalIgnoreCase) &&
               parts[1].Equals("version", StringComparison.OrdinalIgnoreCase)
            ? parts[2]
            : value;
    }
}
