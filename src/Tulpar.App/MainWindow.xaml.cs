using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Tulpar.App.Controls;
using Tulpar.App.ViewModels;
using Tulpar.MapHost;

namespace Tulpar.App;

/// <summary>
/// Primary Tulpar GCS cockpit shell hosting the top command bar, collapsible HUD/telemetry sidebar,
/// swappable Map/Camera stage with PiP HWND ordering, and bottom mission/geofence drawer.
/// </summary>
public partial class MainWindow : Window
{
    private const double ResponsiveCollapseBreakpointWidth = 1280.0;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    private sealed record MapLayerDisplayItem(string Id, string Name);

    private readonly MainViewModel _viewModel;
    private TileCacheStore? _tileCache;
    private HttpClient? _mapHttpClient;
    private MapHttpServer? _mapServer;
    private OfflineRegionDownloader? _regionDownloader;
    private bool _wasBelowBreakpoint;
    private bool _isShuttingDown;

    public MainWindow()
    {
        InitializeComponent();

        _viewModel = new MainViewModel();
        DataContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        Loaded += OnWindowLoadedAsync;
        SizeChanged += OnWindowSizeChanged;
        Closed += OnWindowClosedAsync;
    }

    private async void OnWindowLoadedAsync(object sender, RoutedEventArgs e)
    {
        UpdateResponsiveSidebar(ActualWidth);

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

            await _mapServer.StartAsync();
            await RefreshMapLayerComboAsync(selectLayerId: _viewModel.SelectedMapLayerId);
            await MapHost.InitializeAsync(_mapServer.BaseUri);
        }
        catch (Exception ex)
        {
            _viewModel.AppendSystemLog(Core.Mavlink.MavSeverity.Error, "HARİTA", $"Harita sunucusu başlatılamadı: {ex.Message}");
        }

        BringPipHwndToTop();
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateResponsiveSidebar(e.NewSize.Width);
        BringPipHwndToTop();
    }

    private void UpdateResponsiveSidebar(double width)
    {
        if (width <= 0)
        {
            return;
        }

        bool isBelow = width < ResponsiveCollapseBreakpointWidth;
        if (isBelow != _wasBelowBreakpoint)
        {
            _wasBelowBreakpoint = isBelow;
            _viewModel.IsLeftPanelVisible = !isBelow;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsCameraPrimary))
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(BringPipHwndToTop));
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

    private void OnCenterOnVehicleClick(object sender, RoutedEventArgs e)
    {
        MapHost.CenterOnVehicle();
    }

    private void OnOpenOfflineMapDialogClick(object sender, RoutedEventArgs e)
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
            Owner = this
        };

        dialog.ShowDialog();
    }

    private async void OnWindowClosedAsync(object? sender, EventArgs e)
    {
        _isShuttingDown = true;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

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
