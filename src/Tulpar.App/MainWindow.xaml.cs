using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using Tulpar.App.Controls;
using Tulpar.App.Services;
using Tulpar.App.ViewModels;
using Tulpar.MapHost;

namespace Tulpar.App;

/// <summary>
/// Primary Tulpar GCS cockpit shell hosting the top command bar, collapsible HUD/telemetry sidebar,
/// swappable Map/Camera stage with PiP HWND ordering, and bottom mission/geofence drawer.
/// </summary>
public partial class MainWindow : Window
{
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    private sealed record MapLayerDisplayItem(string Id, string Name);

    private readonly MainViewModel _viewModel;
    private TileCacheStore? _tileCache;
    private HttpClient? _mapHttpClient;
    private MapHttpServer? _mapServer;
    private OfflineRegionDownloader? _regionDownloader;
    private CameraPopupWindow? _cameraPopup;
    private SettingsPopupWindow? _settingsPopup;
    private readonly List<LogPopupWindow> _logPopups = [];
    private CancellationTokenSource? _loadCts;
    private double _savedSidebarWidth = 382.0;
    private double _savedDrawerHeight = 286.0;
    private bool _isShuttingDown;
    private bool _isShowingSafetyPopup;

    public MainWindow()
    {
        InitializeComponent();
        WindowChromeHelper.EnableDarkTitleBar(this);

        _viewModel = new MainViewModel();
        DataContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        VideoFeed.ExpandPopupRequested += OnVideoExpandPopupRequested;
        VideoFeed.PlaybackStateChanged += OnVideoPlaybackStateChanged;

        Loaded += OnWindowLoadedAsync;
        SizeChanged += OnWindowSizeChanged;
        PreviewKeyDown += OnMainWindowPreviewKeyDown;
        Closing += (_, _) => _loadCts?.Cancel();
        Closed += OnWindowClosedAsync;
    }

    private async void OnWindowLoadedAsync(object sender, RoutedEventArgs e)
    {
        UpdateResponsiveLayout(ActualWidth, ActualHeight);
        _loadCts = new CancellationTokenSource();
        CancellationToken loadToken = _loadCts.Token;

        try
        {
            string appDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Tulpar");
            Directory.CreateDirectory(appDataDir);

            string tileDbPath = Path.Combine(appDataDir, "tiles.db");
            _tileCache = new TileCacheStore(tileDbPath);
            _mapHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            _mapServer = new MapHttpServer(_tileCache, _mapHttpClient);
            _regionDownloader = new OfflineRegionDownloader(_tileCache, _mapHttpClient);

            await _mapServer.StartAsync(loadToken);
            if (_isShuttingDown || loadToken.IsCancellationRequested)
            {
                return;
            }

            await RefreshMapLayerComboAsync(selectLayerId: _viewModel.SelectedMapLayerId);
            if (_isShuttingDown || loadToken.IsCancellationRequested)
            {
                return;
            }

            await MapHost.InitializeAsync(_mapServer.BaseUri);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _viewModel.AppendSystemLog(Core.Mavlink.MavSeverity.Error, "HARİTA", $"Harita sunucusu başlatılamadı: {ex.Message}");
        }

        BringPipHwndToTop();
    }

    private async void ShowPendingSafetyPopup()
    {
        if (_isShuttingDown || _isShowingSafetyPopup || _viewModel.PendingSafetyAction is not { } request)
        {
            return;
        }

        _isShowingSafetyPopup = true;
        try
        {
            var popup = new SafetyConfirmPopupWindow(
                request,
                _viewModel.Snapshot.ModeName,
                _viewModel.IsArmed)
            {
                Owner = this
            };

            bool confirmed = popup.ShowDialog() == true;
            if (confirmed)
            {
                await _viewModel.ConfirmSafetyActionCommand.ExecuteAsync();
            }
            else
            {
                _viewModel.CancelSafetyAction();
            }
        }
        finally
        {
            _isShowingSafetyPopup = false;
        }
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateResponsiveLayout(e.NewSize.Width, e.NewSize.Height);
        BringPipHwndToTop();
    }

    private void UpdateResponsiveLayout(double width, double height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        _viewModel.ApplyViewportSize(width, height);

        RightDockColumn.Width = new GridLength(_viewModel.RightDockWidth);
        StageCameraRow.Height = new GridLength(_viewModel.RightCameraHeight);
        if (_viewModel.IsMissionDrawerExpanded)
        {
            MissionDrawerRow.Height = new GridLength(_viewModel.ResolveExpandedMissionDrawerHeight(_savedDrawerHeight));
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.PendingSafetyAction))
        {
            if (_viewModel.PendingSafetyAction is not null && !_isShuttingDown)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(ShowPendingSafetyPopup));
            }
        }
        else if (e.PropertyName == nameof(MainViewModel.IsCameraPrimary))
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(BringPipHwndToTop));
        }
        else if (e.PropertyName == nameof(MainViewModel.IsLeftPanelVisible))
        {
            if (_viewModel.IsLeftPanelVisible)
            {
                LeftSidebarColumn.Width = new GridLength(Math.Max(330.0, _savedSidebarWidth));
            }
            else
            {
                if (LeftSidebarColumn.ActualWidth >= 300.0)
                {
                    _savedSidebarWidth = LeftSidebarColumn.ActualWidth;
                }
                LeftSidebarColumn.Width = GridLength.Auto;
            }
        }
        else if (e.PropertyName == nameof(MainViewModel.IsMissionDrawerExpanded))
        {
            if (_viewModel.IsMissionDrawerExpanded)
            {
                MissionDrawerRow.Height = new GridLength(_viewModel.ResolveExpandedMissionDrawerHeight(_savedDrawerHeight));
            }
            else
            {
                if (MissionDrawerRow.ActualHeight >= 150.0 && _viewModel.IsDesktopLayout)
                {
                    _savedDrawerHeight = MissionDrawerRow.ActualHeight;
                }
                MissionDrawerRow.Height = GridLength.Auto;
            }
        }
    }

    private void OnMainWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_viewModel.ActiveMapEditMode != MapEditMode.None)
            {
                _viewModel.ActiveMapEditMode = MapEditMode.None;
                e.Handled = true;
            }
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z)
        {
            if (Keyboard.FocusedElement is not TextBox && _viewModel.UndoMissionCommand.CanExecute(null))
            {
                _viewModel.UndoMissionCommand.Execute(null);
                e.Handled = true;
            }
        }
    }

    private void BringPipHwndToTop()
    {
        if (_isShuttingDown)
        {
            return;
        }

        HwndHost? pipHost = _viewModel.IsCameraPrimary
            ? MapHost.GetNativeHost()
            : VideoFeed.GetNativeHost();

        if (pipHost is not null && pipHost.Handle != IntPtr.Zero)
        {
            SetWindowPos(pipHost.Handle, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        }
    }

    private async Task RefreshMapLayerComboAsync(string? selectLayerId = null)
    {
        if (_mapServer is null)
        {
            return;
        }

        var items = new List<MapLayerDisplayItem>();
        try
        {
            using var client = new HttpClient();
            string json = await client.GetStringAsync(new Uri(_mapServer.BaseUri, "api/layers"));
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement el in doc.RootElement.EnumerateArray())
                {
                    string? id = el.TryGetProperty("id", out JsonElement idEl) ? idEl.GetString() : null;
                    string? name = el.TryGetProperty("name", out JsonElement nameEl) ? nameEl.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        items.Add(new MapLayerDisplayItem(id, string.IsNullOrWhiteSpace(name) ? id.ToUpperInvariant() : name));
                    }
                }
            }
        }
        catch
        {
            foreach (TileProviderConfig p in _mapServer.GetProviders())
            {
                items.Add(new MapLayerDisplayItem(p.Id, p.Name));
            }
        }

        MapLayerCombo.ItemsSource = items;
        if (items.Count > 0)
        {
            string targetId = selectLayerId ?? _viewModel.SelectedMapLayerId;
            MapLayerDisplayItem? match = items.FirstOrDefault(x => string.Equals(x.Id, targetId, StringComparison.OrdinalIgnoreCase));
            MapLayerCombo.SelectedItem = match ?? items[0];
        }
    }

    private void OnMapLayerSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MapLayerCombo.SelectedItem is MapLayerDisplayItem item)
        {
            _viewModel.SelectedMapLayerId = item.Id;
            MapHost.SetLayer(item.Id);
        }
    }

    private void OnSerialPortsDropDownOpened(object? sender, EventArgs e)
    {
        _viewModel.RefreshSerialPorts();
    }

    private void OnFlightModeComboSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox combo &&
            (combo.IsDropDownOpen || combo.IsKeyboardFocusWithin) &&
            e.AddedItems.Count > 0 &&
            _viewModel.ApplySelectedModeCommand.CanExecute(null))
        {
            _viewModel.ApplySelectedModeCommand.Execute(null);
        }
    }

    private void OnOpenSettingsPopupClick(object sender, RoutedEventArgs e)
    {
        if (_settingsPopup is not null)
        {
            _settingsPopup.Activate();
            return;
        }

        _settingsPopup = new SettingsPopupWindow(_viewModel, OpenOfflineMapDialog)
        {
            Owner = this
        };
        _settingsPopup.Closed += (_, _) => _settingsPopup = null;
        _settingsPopup.Show();
    }

    private void OpenOfflineMapDialog()
    {
        if (_mapServer is null || _regionDownloader is null)
        {
            _viewModel.AppendSystemLog(Core.Mavlink.MavSeverity.Warning, "HARİTA", "Harita sunucusu henüz hazır değil.");
            return;
        }

        var dialog = new OfflineMapDialog(
            _mapServer,
            _regionDownloader,
            _viewModel.SelectedOfflineBounds,
            onLayersUpdated: newLayerId =>
            {
                MapHost.RefreshLayers();
                _ = RefreshMapLayerComboAsync(newLayerId);
            },
            onRequestBoundsPickFromMap: () =>
            {
                _viewModel.ActiveMapEditMode = MapEditMode.Bounds;
                _viewModel.AppendSystemLog(Core.Mavlink.MavSeverity.Info, "HARİTA", "Haritada sol tuşla sürükleyerek çevrimdışı indirme dikdörtgenini seçin.");
            })
        {
            Owner = (Window?)_settingsPopup ?? this
        };

        dialog.ShowDialog();
    }

    private void OnVideoExpandPopupRequested(VideoFeedControl feed)
    {
        if (_cameraPopup is not null)
        {
            _cameraPopup.Activate();
            return;
        }

        _cameraPopup = new CameraPopupWindow(feed)
        {
            Owner = this
        };
        _cameraPopup.Closed += (_, _) => _cameraPopup = null;
        _cameraPopup.Show();
    }

    private void OnVideoPlaybackStateChanged(bool isPlaying, string statusText, string subtitle)
    {
        _cameraPopup?.RefreshVideoState(isPlaying, statusText, subtitle);
    }

    private void OnOpenLogPopupClick(object sender, RoutedEventArgs e)
    {
        var popup = new LogPopupWindow(_viewModel)
        {
            Owner = this
        };
        _logPopups.Add(popup);
        popup.Closed += (_, _) => _logPopups.Remove(popup);
        popup.Show();
    }

    private async void OnOpenReplayFileClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Telemetri Kaydı (.tlog) Aç",
            Filter = "MAVLink Telemetri Kaydı (*.tlog)|*.tlog|Tüm Dosyalar (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await _viewModel.LoadReplayFileAsync(dialog.FileName);
    }

    private async void OnReplaySliderDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (sender is Slider slider)
        {
            await _viewModel.SeekReplayAsync(slider.Value);
        }
    }

    private async void OnWindowClosedAsync(object? sender, EventArgs e)
    {
        if (_isShuttingDown)
        {
            return;
        }

        _isShuttingDown = true;
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        PreviewKeyDown -= OnMainWindowPreviewKeyDown;
        VideoFeed.ExpandPopupRequested -= OnVideoExpandPopupRequested;
        VideoFeed.PlaybackStateChanged -= OnVideoPlaybackStateChanged;
        _cameraPopup?.Close();
        foreach (LogPopupWindow logPopup in _logPopups.ToArray())
        {
            logPopup.Close();
        }
        _logPopups.Clear();

        try
        {
            VideoFeed.Dispose();
            MapHost.Dispose();

            try
            {
                await _viewModel.DisposeAsync();
            }
            catch
            {
            }

            if (_mapServer is not null)
            {
                try
                {
                    await _mapServer.DisposeAsync();
                }
                catch
                {
                }
            }

            _tileCache?.Dispose();
            _mapHttpClient?.Dispose();
        }
        finally
        {
            Application.Current?.Shutdown();
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);
}
