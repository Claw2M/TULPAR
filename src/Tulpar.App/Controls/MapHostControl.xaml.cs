using System.Collections.Specialized;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Tulpar.App.ViewModels;
using Tulpar.Core.Session;

namespace Tulpar.App.Controls;

/// <summary>
/// WebView2 host embedding the local MapLibre GL JS tactical map server and bridging
/// telemetry, mission waypoints, geofence polygons, and operator interactions.
/// </summary>
public partial class MapHostControl : UserControl, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private MainViewModel? _viewModel;
    private Uri? _mapServerBaseUri;
    private bool _initStarted;
    private bool _isMapReady;
    private bool _disposed;
    private string _pendingLayerId = "satellite";
    private bool _lastConnectedState;

    public MapHostControl()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    public HwndHost? GetNativeHost() => MapWebView.Visibility == Visibility.Visible ? MapWebView : null;

    public async Task InitializeAsync(Uri mapServerBaseUri)
    {
        ArgumentNullException.ThrowIfNull(mapServerBaseUri);
        _mapServerBaseUri = mapServerBaseUri;

        if (_disposed || _initStarted)
        {
            return;
        }

        _initStarted = true;
        try
        {
            string userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Tulpar",
                "WebView2");
            Directory.CreateDirectory(userDataFolder);

            CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
            await MapWebView.EnsureCoreWebView2Async(env);

            if (_disposed || MapWebView.CoreWebView2 is null)
            {
                return;
            }

            MapWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            MapWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            MapWebView.CoreWebView2.Settings.IsZoomControlEnabled = false;
            MapWebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            MapWebView.NavigationCompleted += OnNavigationCompleted;

            MapWebView.Source = mapServerBaseUri;
        }
        catch (Exception ex)
        {
            MapWebView.Visibility = Visibility.Collapsed;
            MapFallbackOverlay.Visibility = Visibility.Visible;
            FallbackTitleText.Text = "HARİTA MOTORU KULLANILAMIYOR";
            FallbackSubtitleText.Text = $"WebView2 başlatılamadı: {ex.Message}";
            _viewModel?.AppendSystemLog(Core.Mavlink.MavSeverity.Warning, "HARİTA", $"WebView2 kullanılamıyor: {ex.Message}");
        }
    }

    public void SetLayer(string layerId)
    {
        if (string.IsNullOrWhiteSpace(layerId))
        {
            return;
        }

        _pendingLayerId = layerId;
        PostJsonMessage(new { type = "setLayer", layerId });
    }

    public void CenterOnVehicle()
    {
        PostJsonMessage(new { type = "centerOnVehicle" });
    }

    public void RefreshLayers()
    {
        PostJsonMessage(new { type = "refreshLayers" });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DataContextChanged -= OnDataContextChanged;
        UnhookViewModel();

        try
        {
            if (MapWebView.CoreWebView2 is not null)
            {
                MapWebView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
            }
            MapWebView.NavigationCompleted -= OnNavigationCompleted;
            MapWebView.Dispose();
        }
        catch
        {
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UnhookViewModel();
        if (e.NewValue is MainViewModel vm)
        {
            _viewModel = vm;
            _pendingLayerId = vm.SelectedMapLayerId;
            vm.VehicleMapStateUpdated += OnVehicleMapStateUpdated;
            vm.MissionPlanChanged += OnMissionPlanChanged;
            vm.FencePolygonChanged += OnFencePolygonChanged;
            vm.MapEditModeChanged += OnMapEditModeChanged;
            vm.MissionItems.CollectionChanged += OnCollectionChangedSyncMission;
            vm.FencePoints.CollectionChanged += OnCollectionChangedSyncFence;

            if (_isMapReady)
            {
                PushAllStateToMap();
            }
        }
    }

    private void UnhookViewModel()
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.VehicleMapStateUpdated -= OnVehicleMapStateUpdated;
        _viewModel.MissionPlanChanged -= OnMissionPlanChanged;
        _viewModel.FencePolygonChanged -= OnFencePolygonChanged;
        _viewModel.MapEditModeChanged -= OnMapEditModeChanged;
        _viewModel.MissionItems.CollectionChanged -= OnCollectionChangedSyncMission;
        _viewModel.FencePoints.CollectionChanged -= OnCollectionChangedSyncFence;
        _viewModel = null;
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (e.IsSuccess)
        {
            _isMapReady = true;
            MapFallbackOverlay.Visibility = Visibility.Collapsed;
            PushAllStateToMap();
        }
        else if (_mapServerBaseUri is not null)
        {
            FallbackTitleText.Text = "HARİTA YÜKLENEMEDİ";
            FallbackSubtitleText.Text = $"Durum: {e.WebErrorStatus}";
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_disposed || _viewModel is null)
        {
            return;
        }

        try
        {
            string json = e.WebMessageAsJson;
            _viewModel.HandleMapWebMessage(json);
        }
        catch
        {
        }
    }

    private void OnVehicleMapStateUpdated(TelemetrySnapshot snapshot)
    {
        bool resetTrail = _lastConnectedState && !snapshot.IsConnected;
        _lastConnectedState = snapshot.IsConnected;

        if (resetTrail)
        {
            PostJsonMessage(new
            {
                type = "updateVehicle",
                payload = new
                {
                    lat = 0.0,
                    lon = 0.0,
                    headingDeg = 0.0,
                    category = "Unknown",
                    hasHome = false,
                    homeLat = 0.0,
                    homeLon = 0.0,
                    trail = Array.Empty<object>()
                }
            });
            return;
        }

        PostJsonMessage(new
        {
            type = "updateVehicle",
            payload = new
            {
                lat = snapshot.Latitude,
                lon = snapshot.Longitude,
                headingDeg = snapshot.HeadingDeg,
                category = snapshot.VehicleCategory.ToString(),
                hasHome = snapshot.HasHome,
                homeLat = snapshot.HomeLatitude,
                homeLon = snapshot.HomeLongitude
            }
        });
    }

    private void OnCollectionChangedSyncMission(object? sender, NotifyCollectionChangedEventArgs e) =>
        OnMissionPlanChanged();

    private void OnCollectionChangedSyncFence(object? sender, NotifyCollectionChangedEventArgs e) =>
        OnFencePolygonChanged();

    private void OnMissionPlanChanged()
    {
        if (_viewModel is null)
        {
            return;
        }

        var items = _viewModel.MissionItems
            .Select(m => new { seq = m.Seq, lat = m.Latitude, lon = m.Longitude })
            .ToArray();
        PostJsonMessage(new { type = "setMission", items });
    }

    private void OnFencePolygonChanged()
    {
        if (_viewModel is null)
        {
            return;
        }

        var points = _viewModel.FencePoints
            .Select(p => new { lat = p.Latitude, lon = p.Longitude })
            .ToArray();
        PostJsonMessage(new { type = "setFence", points });
    }

    private void OnMapEditModeChanged(MapEditMode mode)
    {
        string modeKey = mode switch
        {
            MapEditMode.Mission => "mission",
            MapEditMode.Fence => "fence",
            MapEditMode.Bounds => "bounds",
            _ => "none"
        };
        PostJsonMessage(new { type = "setEditMode", mode = modeKey });
    }

    private void PushAllStateToMap()
    {
        if (_viewModel is null)
        {
            return;
        }

        RefreshLayers();
        SetLayer(_pendingLayerId);
        OnMapEditModeChanged(_viewModel.ActiveMapEditMode);
        OnMissionPlanChanged();
        OnFencePolygonChanged();
        OnVehicleMapStateUpdated(_viewModel.Snapshot);
    }

    private void PostJsonMessage(object payload)
    {
        if (_disposed || !_isMapReady || MapWebView.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            string json = JsonSerializer.Serialize(payload, JsonOptions);
            MapWebView.CoreWebView2.PostWebMessageAsJson(json);
        }
        catch
        {
        }
    }
}
