using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipDock.App.ViewModels;

namespace ClipDock.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        viewModel.PropertyChanged += ViewModelOnPropertyChanged;
        StateChanged += (_, _) => UpdateMaximizeIcon();
        Closed += (_, _) => viewModel.PropertyChanged -= ViewModelOnPropertyChanged;
        UpdateWallpaper();
        UpdateMaximizeIcon();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.ChangedButton != MouseButton.Left) return;
        if (eventArgs.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        try { DragMove(); }
        catch (InvalidOperationException) { }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs eventArgs) => WindowState = WindowState.Minimized;
    private void MaximizeButton_Click(object sender, RoutedEventArgs eventArgs) => ToggleMaximize();
    private void CloseButton_Click(object sender, RoutedEventArgs eventArgs) => Close();

    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void UpdateMaximizeIcon()
    {
        var maximized = WindowState == WindowState.Maximized;
        MaximizeBox.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
        RestoreBoxes.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(MainViewModel.WallpaperPath)) UpdateWallpaper();
    }

    private void UpdateWallpaper()
    {
        var path = _viewModel.WallpaperPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            WallpaperImage.Source = null;
            WallpaperImage.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 1920;
            using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                bitmap.StreamSource = stream;
                bitmap.EndInit();
            }
            bitmap.Freeze();
            WallpaperImage.Source = bitmap;
            WallpaperImage.Visibility = Visibility.Visible;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            WallpaperImage.Source = null;
            WallpaperImage.Visibility = Visibility.Collapsed;
        }
    }

    private void ForwardMouseWheel(object sender, MouseWheelEventArgs eventArgs)
    {
        if (sender is not DependencyObject source) return;
        var parent = FindParentScrollViewer(source);
        if (parent is null) return;

        eventArgs.Handled = true;
        parent.RaiseEvent(new MouseWheelEventArgs(eventArgs.MouseDevice, eventArgs.Timestamp, eventArgs.Delta)
        {
            RoutedEvent = MouseWheelEvent,
            Source = parent
        });
    }

    private static System.Windows.Controls.ScrollViewer? FindParentScrollViewer(DependencyObject child)
    {
        var current = VisualTreeHelper.GetParent(child);
        while (current is not null)
        {
            if (current is System.Windows.Controls.ScrollViewer scrollViewer) return scrollViewer;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}
