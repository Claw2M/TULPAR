using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Tulpar.App.Services;

namespace Tulpar.App.Controls;

/// <summary>
/// Large modal/floating popup window for inspecting the live LibVLC video stream
/// or starting playback cleanly without Win32 HWND airspace contention.
/// </summary>
public partial class CameraPopupWindow : Window
{
    private readonly VideoFeedControl _sourceFeed;
    private bool _restored;

    public CameraPopupWindow(VideoFeedControl sourceFeed)
    {
        _sourceFeed = sourceFeed ?? throw new ArgumentNullException(nameof(sourceFeed));
        InitializeComponent();
        WindowChromeHelper.EnableDarkTitleBar(this);

        DataContext = sourceFeed.DataContext;
        Loaded += OnLoaded;
        Closing += OnWindowClosing;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        bool active = _sourceFeed.AttachToExternalView(PopupVideoView, out string statusText, out string subtitle);
        StatusBadgeText.Text = statusText;
        PopupIdleSubtitle.Text = string.IsNullOrWhiteSpace(subtitle)
            ? "Üstteki 'OYNAT' butonunu veya ana ekrandaki kamera kaynağını kullanarak yayını başlatabilirsiniz."
            : subtitle;
        PopupVideoView.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        PopupIdleOverlay.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
    }

    public void RefreshVideoState(bool isPlaying, string statusText, string subtitle)
    {
        StatusBadgeText.Text = statusText;
        PopupIdleSubtitle.Text = string.IsNullOrWhiteSpace(subtitle)
            ? "Üstteki 'OYNAT' butonunu veya ana ekrandaki kamera kaynağını kullanarak yayını başlatabilirsiniz."
            : subtitle;
        PopupVideoView.Visibility = isPlaying ? Visibility.Visible : Visibility.Collapsed;
        PopupIdleOverlay.Visibility = isPlaying ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_restored)
        {
            return;
        }

        _restored = true;
        _sourceFeed.RestoreFromExternalView(PopupVideoView);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }
}
