using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using LibVLCSharp.Shared;
using Tulpar.App.ViewModels;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace Tulpar.App.Controls;

/// <summary>
/// Safe wrapper around LibVLCSharp.WPF <c>VideoView</c> supporting RTSP, UDP/RTP, and USB DirectShow capture
/// with graceful fallback to "SİNYAL BEKLENİYOR / KAMERA SEÇ" in headless or LibVLC-unavailable environments.
/// </summary>
public partial class VideoFeedControl : UserControl, IDisposable
{
    private const string DefaultIdleOverlayMessage = "SİNYAL BEKLENİYOR / KAMERA SEÇ";
    private const string DefaultIdleSubtitle = "RTSP · UDP/RTP · USB DIRECTSHOW";

    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private Media? _currentMedia;
    private MainViewModel? _boundViewModel;
    private bool _libVlcInitAttempted;
    private bool _libVlcAvailable;
    private bool _disposed;
    private string _overlayMessage = DefaultIdleOverlayMessage;
    private string _overlaySubtitle = DefaultIdleSubtitle;

    public VideoFeedControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
    }

    public string OverlayMessage => _overlayMessage;

    public bool IsStreamPlaying => _mediaPlayer?.IsPlaying ?? false;

    public static string BuildEffectiveMrl(VideoSourceKind kind, string? input) =>
        VideoMrlBuilder.BuildEffectiveMrl(kind, input);

    public static IReadOnlyList<string> BuildMediaOptions(VideoSourceKind kind, string? input) =>
        VideoMrlBuilder.BuildMediaOptions(kind, input);

    public HwndHost? GetNativeHost() => FindVisualChild<HwndHost>(VideoViewControl);

    public void StartStream(string mrl, IReadOnlyList<string>? mediaOptions = null)
    {
        if (_disposed || string.IsNullOrWhiteSpace(mrl))
        {
            return;
        }

        if (!EnsureLibVlcInitialized() || _libVlc is null || _mediaPlayer is null)
        {
            SetOverlayState(
                DefaultIdleOverlayMessage,
                _overlaySubtitle,
                showIdleOverlay: true,
                statusText: " · VLC YOK");
            _boundViewModel?.UpdateVideoPlaybackStatus(false, "VLC KULLANILAMIYOR");
            return;
        }

        try
        {
            StopCurrentMediaOnly();

            var media = new Media(_libVlc, mrl.Trim(), FromType.FromLocation);
            if (mediaOptions is not null)
            {
                foreach (string option in mediaOptions)
                {
                    if (!string.IsNullOrWhiteSpace(option))
                    {
                        media.AddOption(option);
                    }
                }
            }

            _currentMedia = media;
            SetOverlayState(
                DefaultIdleOverlayMessage,
                $"AKIM: {mrl.Trim()}",
                showIdleOverlay: true,
                statusText: " · BAĞLANIYOR");

            if (!_mediaPlayer.Play(media))
            {
                SetOverlayState(
                    DefaultIdleOverlayMessage,
                    $"AKIŞ BAŞLATILAMADI ({mrl.Trim()})",
                    showIdleOverlay: true,
                    statusText: " · HATA");
                _boundViewModel?.UpdateVideoPlaybackStatus(false, "HATA");
            }
        }
        catch (Exception ex)
        {
            SetOverlayState(
                DefaultIdleOverlayMessage,
                $"VİDEO HATASI: {ex.Message}",
                showIdleOverlay: true,
                statusText: " · HATA");
            _boundViewModel?.UpdateVideoPlaybackStatus(false, "HATA");
        }
    }

    public void StopStream()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _mediaPlayer?.Stop();
        }
        catch
        {
        }

        StopCurrentMediaOnly();
        SetOverlayState(
            DefaultIdleOverlayMessage,
            DefaultIdleSubtitle,
            showIdleOverlay: true,
            statusText: " · BEKLİYOR");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        DataContextChanged -= OnDataContextChanged;
        UnhookViewModel();

        try
        {
            if (_mediaPlayer is not null)
            {
                _mediaPlayer.Playing -= OnMediaPlaying;
                _mediaPlayer.Stopped -= OnMediaStopped;
                _mediaPlayer.EncounteredError -= OnMediaError;
                _mediaPlayer.Stop();
            }
        }
        catch
        {
        }

        StopCurrentMediaOnly();

        try
        {
            VideoViewControl.MediaPlayer = null;
            VideoViewControl.Dispose();
        }
        catch
        {
        }

        try
        {
            _mediaPlayer?.Dispose();
        }
        catch
        {
        }
        _mediaPlayer = null;

        try
        {
            _libVlc?.Dispose();
        }
        catch
        {
        }
        _libVlc = null;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        bool hasWindow = Window.GetWindow(this) is not null;
        if (hasWindow)
        {
            VideoViewControl.Visibility = Visibility.Visible;
            WpfFallbackOverlay.Visibility = Visibility.Collapsed;
            ForegroundRoot.DataContext = DataContext;
        }
        else
        {
            VideoViewControl.Visibility = Visibility.Collapsed;
            WpfFallbackOverlay.Visibility = Visibility.Visible;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Only stop stream on actual control disposal/window close, NOT during layout updates.
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UnhookViewModel();
        ForegroundRoot.DataContext = e.NewValue;
        if (e.NewValue is MainViewModel vm)
        {
            _boundViewModel = vm;
            vm.VideoStartRequested += OnViewModelVideoStartRequested;
            vm.VideoStopRequested += OnViewModelVideoStopRequested;
        }
    }

    private void UnhookViewModel()
    {
        if (_boundViewModel is not null)
        {
            _boundViewModel.VideoStartRequested -= OnViewModelVideoStartRequested;
            _boundViewModel.VideoStopRequested -= OnViewModelVideoStopRequested;
            _boundViewModel = null;
        }
    }

    private void OnViewModelVideoStartRequested(string mrl, IReadOnlyList<string> options)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => StartStream(mrl, options));
            return;
        }

        StartStream(mrl, options);
    }

    private void OnViewModelVideoStopRequested()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(StopStream);
            return;
        }

        StopStream();
    }

    private bool EnsureLibVlcInitialized()
    {
        if (_libVlcInitAttempted)
        {
            return _libVlcAvailable;
        }

        _libVlcInitAttempted = true;
        try
        {
            string archFolder = Environment.Is64BitProcess ? "win-x64" : "win-x86";
            string explicitVlcDir = Path.Combine(AppContext.BaseDirectory, "libvlc", archFolder);
            if (Directory.Exists(explicitVlcDir))
            {
                LibVLCSharp.Shared.Core.Initialize(explicitVlcDir);
            }
            else
            {
                LibVLCSharp.Shared.Core.Initialize();
            }

            _libVlc = new LibVLC("--no-video-title-show", "--quiet");
            _mediaPlayer = new MediaPlayer(_libVlc)
            {
                EnableHardwareDecoding = true
            };
            _mediaPlayer.Playing += OnMediaPlaying;
            _mediaPlayer.Stopped += OnMediaStopped;
            _mediaPlayer.EncounteredError += OnMediaError;

            VideoViewControl.MediaPlayer = _mediaPlayer;
            _libVlcAvailable = true;
        }
        catch (Exception ex)
        {
            _libVlcAvailable = false;
            _overlaySubtitle = $"LIBVLC BAŞLATILAMADI ({ex.GetType().Name})";
            _mediaPlayer?.Dispose();
            _mediaPlayer = null;
            _libVlc?.Dispose();
            _libVlc = null;
        }

        return _libVlcAvailable;
    }

    private void OnMediaPlaying(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_disposed)
            {
                return;
            }

            SetOverlayState(
                DefaultIdleOverlayMessage,
                string.Empty,
                showIdleOverlay: false,
                statusText: " · CANLI");
            _boundViewModel?.UpdateVideoPlaybackStatus(true, "CANLI");
        });
    }

    private void OnMediaStopped(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_disposed)
            {
                return;
            }

            SetOverlayState(
                DefaultIdleOverlayMessage,
                DefaultIdleSubtitle,
                showIdleOverlay: true,
                statusText: " · BEKLİYOR");
            _boundViewModel?.UpdateVideoPlaybackStatus(false, "BEKLİYOR");
        });
    }

    private void OnMediaError(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_disposed)
            {
                return;
            }

            SetOverlayState(
                DefaultIdleOverlayMessage,
                "AKIŞ BAĞLANTISI KURULAMADI",
                showIdleOverlay: true,
                statusText: " · HATA");
            _boundViewModel?.UpdateVideoPlaybackStatus(false, "HATA");
        });
    }

    private void SetOverlayState(string title, string subtitle, bool showIdleOverlay, string statusText)
    {
        _overlayMessage = title;
        _overlaySubtitle = subtitle;

        NativeOverlayTitleText.Text = title;
        NativeOverlaySubtitleText.Text = subtitle;
        FallbackOverlayTitleText.Text = title;
        FallbackOverlaySubtitleText.Text = subtitle;
        NativeStatusText.Text = statusText;

        Visibility idleVisibility = showIdleOverlay ? Visibility.Visible : Visibility.Collapsed;
        NativeIdleOverlay.Visibility = idleVisibility;
    }

    private void StopCurrentMediaOnly()
    {
        try
        {
            _currentMedia?.Dispose();
        }
        catch
        {
        }
        _currentMedia = null;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                return match;
            }

            T? nested = FindVisualChild<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }
}
