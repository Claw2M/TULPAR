using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using LibVLCSharp.Shared;
using Tulpar.App.Services;
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

    private static readonly SolidColorBrush FooterIdleBrush = CreateFrozenBrush(0xD4, 0xD4, 0xDC);
    private static readonly SolidColorBrush FooterLiveBrush = CreateFrozenBrush(0x4A, 0xDE, 0x80);
    private static readonly SolidColorBrush FooterConnectingBrush = CreateFrozenBrush(0xFA, 0xCC, 0x15);
    private static readonly SolidColorBrush FooterErrorBrush = CreateFrozenBrush(0xF8, 0x71, 0x71);

    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private Media? _currentMedia;
    private MainViewModel? _boundViewModel;
    private bool _libVlcAvailable;
    private bool _isAttachedToExternalView;
    private bool _disposed;
    private string _overlayMessage = DefaultIdleOverlayMessage;
    private string _overlaySubtitle = DefaultIdleSubtitle;
    private string? _activeMrl;
    private IReadOnlyList<string>? _activeMediaOptions;
    private DateTimeOffset? _lastStreamTimestamp;
    private uint _videoWidth;
    private uint _videoHeight;
    private string? _lastErrorReason;
    private bool _isConnecting;

    public VideoFeedControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
        UpdateTelemetryFooter();
    }

    public event Action<VideoFeedControl>? ExpandPopupRequested;
    public event Action<bool, string, string>? PlaybackStateChanged;

    public string OverlayMessage => _overlayMessage;

    public bool IsStreamPlaying => _mediaPlayer?.IsPlaying ?? false;

    internal bool AttachToExternalView(LibVLCSharp.WPF.VideoView targetView, out string statusText, out string subtitle)
    {
        statusText = OverlayStatusText.Text;
        subtitle = _overlaySubtitle;
        bool playing = IsStreamPlaying && _mediaPlayer is not null;
        if (!playing || _mediaPlayer is null)
        {
            return false;
        }

        try
        {
            VideoViewControl.MediaPlayer = null;
            targetView.MediaPlayer = _mediaPlayer;
            _isAttachedToExternalView = true;
            VideoViewControl.Visibility = Visibility.Collapsed;
            WpfIdleOverlay.Visibility = Visibility.Visible;
            IdleOverlayTitleText.Text = "BÜYÜK PENCEREDE AÇIK";
            IdleOverlaySubtitleText.Text = "Video akışı popup pencerede gösteriliyor.";
            return true;
        }
        catch
        {
            _isAttachedToExternalView = false;
            return false;
        }
    }

    internal void RestoreFromExternalView(LibVLCSharp.WPF.VideoView externalView)
    {
        _isAttachedToExternalView = false;

        try
        {
            externalView.MediaPlayer = null;
            if (_mediaPlayer is not null && !_disposed)
            {
                VideoViewControl.MediaPlayer = _mediaPlayer;
            }
        }
        catch
        {
        }

        bool playing = IsStreamPlaying && Window.GetWindow(this) is not null;
        if (playing)
        {
            RefreshVideoDimensionsSafe();
        }

        string statusText = playing ? BuildLiveHeaderStatus() : " · BEKLİYOR";
        SetOverlayState(
            DefaultIdleOverlayMessage,
            playing ? string.Empty : DefaultIdleSubtitle,
            showIdleOverlay: !playing,
            statusText: statusText);
    }

    private void OnExpandPopupClick(object sender, RoutedEventArgs e) =>
        ExpandPopupRequested?.Invoke(this);

    private void OnIdleOverlayClick(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        ExpandPopupRequested?.Invoke(this);

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

        _activeMrl = mrl.Trim();
        _activeMediaOptions = mediaOptions;
        _videoWidth = 0;
        _videoHeight = 0;
        _lastErrorReason = null;

        if (!EnsureLibVlcInitialized() || _libVlc is null || _mediaPlayer is null)
        {
            _isConnecting = false;
            _lastErrorReason = "VLC KULLANILAMIYOR";
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

            var media = new Media(_libVlc, _activeMrl, FromType.FromLocation);
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
            _isConnecting = true;
            SetOverlayState(
                DefaultIdleOverlayMessage,
                $"AKIM: {_activeMrl}",
                showIdleOverlay: true,
                statusText: " · BAĞLANIYOR");

            if (!_mediaPlayer.Play(media))
            {
                _isConnecting = false;
                _lastErrorReason = "AKIŞ BAŞLATILAMADI";
                SetOverlayState(
                    DefaultIdleOverlayMessage,
                    $"AKIŞ BAŞLATILAMADI ({_activeMrl})",
                    showIdleOverlay: true,
                    statusText: " · HATA");
                _boundViewModel?.UpdateVideoPlaybackStatus(false, "HATA");
            }
        }
        catch (Exception ex)
        {
            _isConnecting = false;
            _lastErrorReason = $"HATA: {TruncateText(ex.Message, 32)}";
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

        _isConnecting = false;
        _videoWidth = 0;
        _videoHeight = 0;
        _lastErrorReason = null;
        _activeMrl = null;
        _activeMediaOptions = null;

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
                _mediaPlayer.Vout -= OnMediaVout;
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
        bool showVideo = !_isAttachedToExternalView && IsStreamPlaying && Window.GetWindow(this) is not null;
        VideoViewControl.Visibility = showVideo ? Visibility.Visible : Visibility.Collapsed;
        WpfIdleOverlay.Visibility = showVideo ? Visibility.Collapsed : Visibility.Visible;
        UpdateTelemetryFooter();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Only stop stream on actual control disposal/window close, NOT during layout updates.
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UnhookViewModel();
        if (e.NewValue is MainViewModel vm)
        {
            _boundViewModel = vm;
            vm.VideoStartRequested += OnViewModelVideoStartRequested;
            vm.VideoStopRequested += OnViewModelVideoStopRequested;
            vm.PropertyChanged += OnViewModelPropertyChanged;
        }

        UpdateTelemetryFooter();
    }

    private void UnhookViewModel()
    {
        if (_boundViewModel is not null)
        {
            _boundViewModel.VideoStartRequested -= OnViewModelVideoStartRequested;
            _boundViewModel.VideoStopRequested -= OnViewModelVideoStopRequested;
            _boundViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _boundViewModel = null;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(MainViewModel.SelectedVideoSourceKind)
            or nameof(MainViewModel.VideoStreamInput)
            or nameof(MainViewModel.ActiveVideoMrl)))
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (!_disposed)
                {
                    UpdateTelemetryFooter();
                }
            });
            return;
        }

        UpdateTelemetryFooter();
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
        if (_libVlcAvailable && _libVlc is not null && _mediaPlayer is not null)
        {
            return true;
        }

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
            _mediaPlayer.Vout += OnMediaVout;
            _mediaPlayer.Stopped += OnMediaStopped;
            _mediaPlayer.EncounteredError += OnMediaError;

            if (!_isAttachedToExternalView)
            {
                VideoViewControl.MediaPlayer = _mediaPlayer;
            }
            _libVlcAvailable = true;
        }
        catch (Exception ex)
        {
            _libVlcAvailable = false;
            _overlaySubtitle = $"LIBVLC BAŞLATILAMADI ({ex.GetType().Name})";
            if (_mediaPlayer is not null)
            {
                _mediaPlayer.Playing -= OnMediaPlaying;
                _mediaPlayer.Vout -= OnMediaVout;
                _mediaPlayer.Stopped -= OnMediaStopped;
                _mediaPlayer.EncounteredError -= OnMediaError;
                _mediaPlayer.Dispose();
                _mediaPlayer = null;
            }
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

            _isConnecting = false;
            _lastErrorReason = null;
            _lastStreamTimestamp = DateTimeOffset.Now;
            RefreshVideoDimensionsSafe();

            string statusText = BuildLiveHeaderStatus();
            SetOverlayState(
                DefaultIdleOverlayMessage,
                string.Empty,
                showIdleOverlay: false,
                statusText: statusText);
            _boundViewModel?.UpdateVideoPlaybackStatus(true, "CANLI");
        });
    }

    private void OnMediaVout(object? sender, MediaPlayerVoutEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_disposed || !IsStreamPlaying)
            {
                return;
            }

            _lastStreamTimestamp = DateTimeOffset.Now;
            if (RefreshVideoDimensionsSafe())
            {
                OverlayStatusText.Text = BuildLiveHeaderStatus();
            }
            UpdateTelemetryFooter();
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

            _isConnecting = false;
            _videoWidth = 0;
            _videoHeight = 0;

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

            _isConnecting = false;
            _videoWidth = 0;
            _videoHeight = 0;
            _lastErrorReason = "AKIŞ BAĞLANTISI KURULAMADI";

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

        if (_isAttachedToExternalView)
        {
            IdleOverlayTitleText.Text = "BÜYÜK PENCEREDE AÇIK";
            IdleOverlaySubtitleText.Text = "Video akışı popup pencerede gösteriliyor.";
            OverlayStatusText.Text = statusText;
            VideoViewControl.Visibility = Visibility.Collapsed;
            WpfIdleOverlay.Visibility = Visibility.Visible;
            UpdateTelemetryFooter();
            PlaybackStateChanged?.Invoke(IsStreamPlaying, statusText, subtitle);
            return;
        }

        IdleOverlayTitleText.Text = title;
        IdleOverlaySubtitleText.Text = subtitle;
        OverlayStatusText.Text = statusText;

        bool canShowNativeVideo = !showIdleOverlay && Window.GetWindow(this) is not null;
        VideoViewControl.Visibility = canShowNativeVideo ? Visibility.Visible : Visibility.Collapsed;
        WpfIdleOverlay.Visibility = canShowNativeVideo ? Visibility.Collapsed : Visibility.Visible;
        UpdateTelemetryFooter();
        PlaybackStateChanged?.Invoke(IsStreamPlaying, statusText, subtitle);
    }

    private string BuildLiveHeaderStatus() =>
        _videoWidth > 0 && _videoHeight > 0
            ? $" · CANLI · {_videoWidth}×{_videoHeight}"
            : " · CANLI";

    private bool RefreshVideoDimensionsSafe()
    {
        uint width = 0;
        uint height = 0;

        try
        {
            if (_mediaPlayer is null)
            {
                return false;
            }

            if (_mediaPlayer.Size(0, ref width, ref height) && width > 0 && height > 0)
            {
                _videoWidth = width;
                _videoHeight = height;
                return true;
            }

            // Touch VideoTrackDescription safely to see if track metadata is available.
            _ = _mediaPlayer.VideoTrackDescription;

            if (_currentMedia is not null)
            {
                foreach (MediaTrack track in _currentMedia.Tracks)
                {
                    if (track.TrackType == TrackType.Video &&
                        track.Data.Video.Width > 0 &&
                        track.Data.Video.Height > 0)
                    {
                        _videoWidth = track.Data.Video.Width;
                        _videoHeight = track.Data.Video.Height;
                        return true;
                    }
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private void UpdateTelemetryFooter()
    {
        if (FooterSourceText is null || FooterStatusText is null)
        {
            return;
        }

        FooterSourceText.Text = ResolveFooterSourceText();

        if (IsStreamPlaying)
        {
            RefreshVideoDimensionsSafe();
            FooterStatusText.Text = _videoWidth > 0 && _videoHeight > 0
                ? $"CANLI · {_videoWidth}×{_videoHeight}"
                : (_lastStreamTimestamp.HasValue
                    ? $"CANLI · {_lastStreamTimestamp.Value:HH:mm:ss}"
                    : "CANLI");
            FooterStatusText.Foreground = FooterLiveBrush;
        }
        else if (_isConnecting)
        {
            FooterStatusText.Text = "BAĞLANIYOR...";
            FooterStatusText.Foreground = FooterConnectingBrush;
        }
        else if (!string.IsNullOrWhiteSpace(_lastErrorReason))
        {
            FooterStatusText.Text = _lastErrorReason;
            FooterStatusText.Foreground = FooterErrorBrush;
        }
        else if (_lastStreamTimestamp.HasValue)
        {
            FooterStatusText.Text = $"SON AKIM: {_lastStreamTimestamp.Value:HH:mm:ss}";
            FooterStatusText.Foreground = FooterIdleBrush;
        }
        else
        {
            FooterStatusText.Text = "BEKLİYOR";
            FooterStatusText.Foreground = FooterIdleBrush;
        }
    }

    private string ResolveFooterSourceText()
    {
        if (!string.IsNullOrWhiteSpace(_activeMrl))
        {
            return FormatSourceSummary(_boundViewModel?.SelectedVideoSourceKind, _activeMrl, _activeMediaOptions, _boundViewModel?.VideoStreamInput);
        }

        if (_boundViewModel is not null)
        {
            return FormatSourceSummary(_boundViewModel.SelectedVideoSourceKind, _boundViewModel.VideoStreamInput, null, _boundViewModel.VideoStreamInput);
        }

        return "UDP/RTP · :5600";
    }

    internal static string FormatSourceSummary(
        VideoSourceKind? kind,
        string? mrlOrInput,
        IReadOnlyList<string>? mediaOptions = null,
        string? fallbackInput = null)
    {
        string raw = mrlOrInput?.Trim() ?? string.Empty;
        VideoSourceKind effectiveKind = kind ?? InferSourceKind(raw);

        switch (effectiveKind)
        {
            case VideoSourceKind.Rtsp:
            {
                string target = string.IsNullOrWhiteSpace(raw)
                    ? "127.0.0.1:8554/stream"
                    : StripPrefixIgnoreCase(raw, "rtsp://");
                return $"RTSP · {TruncateText(target, 40)}";
            }
            case VideoSourceKind.UsbDirectShow:
            {
                string? device = ExtractDirectShowDevice(mediaOptions);
                if (string.IsNullOrWhiteSpace(device) &&
                    !string.IsNullOrWhiteSpace(raw) &&
                    !raw.Equals(VideoMrlBuilder.DefaultDirectShowMrl, StringComparison.OrdinalIgnoreCase))
                {
                    device = StripPrefixIgnoreCase(StripPrefixIgnoreCase(raw, "video="), ":dshow-vdev=");
                }

                if (string.IsNullOrWhiteSpace(device) &&
                    !string.IsNullOrWhiteSpace(fallbackInput) &&
                    !fallbackInput.Trim().Equals(VideoMrlBuilder.DefaultDirectShowMrl, StringComparison.OrdinalIgnoreCase))
                {
                    device = StripPrefixIgnoreCase(StripPrefixIgnoreCase(fallbackInput.Trim(), "video="), ":dshow-vdev=");
                }

                return string.IsNullOrWhiteSpace(device)
                    ? "USB · VARSAYILAN"
                    : $"USB · {TruncateText(device, 36)}";
            }
            default:
            {
                string endpoint = FormatUdpEndpoint(raw);
                return $"UDP/RTP · {TruncateText(endpoint, 36)}";
            }
        }
    }

    private static VideoSourceKind InferSourceKind(string mrl)
    {
        if (mrl.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
        {
            return VideoSourceKind.Rtsp;
        }

        if (mrl.StartsWith("dshow://", StringComparison.OrdinalIgnoreCase))
        {
            return VideoSourceKind.UsbDirectShow;
        }

        return VideoSourceKind.UdpRtp;
    }

    private static string FormatUdpEndpoint(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return ":5600";
        }

        string clean = StripPrefixIgnoreCase(StripPrefixIgnoreCase(raw, "udp://"), "rtp://");
        if (clean.StartsWith("@", StringComparison.Ordinal))
        {
            clean = clean[1..];
        }

        return string.IsNullOrWhiteSpace(clean) ? ":5600" : clean;
    }

    private static string? ExtractDirectShowDevice(IReadOnlyList<string>? mediaOptions)
    {
        if (mediaOptions is null)
        {
            return null;
        }

        const string prefix = ":dshow-vdev=";
        foreach (string opt in mediaOptions)
        {
            if (opt.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                string value = opt[prefix.Length..].Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static string StripPrefixIgnoreCase(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? value[prefix.Length..].Trim()
            : value;

    private static string TruncateText(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value[..(maxLength - 1)] + "…";
    }

    private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
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
