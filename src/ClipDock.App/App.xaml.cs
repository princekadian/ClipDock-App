using System.Threading;
using System.Windows;
using ClipDock.App.ViewModels;
using ClipDock.Core.Services;
using ClipDock.Infrastructure;
using ClipDock.Infrastructure.BrowserBridge;
using Microsoft.Win32;

namespace ClipDock.App;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private DownloadQueueManager? _queue;
    private LocalBridgeServer? _bridge;
    private EngineManager? _engineManager;
    private FileLogger? _logger;

    protected override async void OnStartup(StartupEventArgs eventArgs)
    {
        base.OnStartup(eventArgs);
        _singleInstance = new Mutex(true, "Local\\ClipDock.SingleInstance", out var created);
        if (!created)
        {
            MessageBox.Show("ClipDock is already running.", "ClipDock", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        ApplySystemTheme();
        var paths = new AppPaths();
        paths.EnsureDirectories();
        _logger = new FileLogger(paths);
        DispatcherUnhandledException += (_, args) =>
        {
            _logger.Error("Unhandled UI error", args.Exception);
            MessageBox.Show("ClipDock encountered an unexpected problem. Details were written to the log.",
                "ClipDock", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        try
        {
            var settingsStore = new SettingsStore(paths);
            var settings = await settingsStore.LoadAsync();
            var historyStore = new HistoryStore(paths);
            var queueStore = new QueueStore(paths);
            var runner = new ProcessRunner();
            _engineManager = new EngineManager(paths, runner, _logger);
            var analyzer = new YtDlpVideoAnalyzer(paths, _engineManager, runner, _logger);
            var downloadEngine = new YtDlpDownloadEngine(paths, _engineManager, runner, _logger);
            _queue = new DownloadQueueManager(downloadEngine, historyStore, queueStore, _logger, settings.MaximumConcurrentDownloads);
            var viewModel = new MainViewModel(analyzer, _queue, historyStore, settingsStore, _engineManager, paths, settings);
            var window = new MainWindow(viewModel);
            MainWindow = window;
            window.Show();
            await viewModel.InitializeAsync();
            _bridge = new LocalBridgeServer(analyzer, _queue, settings, _logger);
            _bridge.Start();
        }
        catch (Exception exception)
        {
            _logger.Error("Application startup failed", exception);
            MessageBox.Show("ClipDock could not start. Open the logs folder for technical details.",
                "Startup failed", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override async void OnExit(ExitEventArgs eventArgs)
    {
        if (_bridge is not null) await _bridge.DisposeAsync();
        if (_queue is not null) await _queue.DisposeAsync();
        _engineManager?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(eventArgs);
    }

    private static void ApplySystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var useLightTheme = key?.GetValue("AppsUseLightTheme") as int? != 0;
            if (!useLightTheme)
            {
                Current.Resources.MergedDictionaries[0] = new ResourceDictionary
                {
                    Source = new Uri("Themes/Dark.xaml", UriKind.Relative)
                };
            }
        }
        catch
        {
            // The light palette remains the safe fallback when the theme setting is unavailable.
        }
    }
}
