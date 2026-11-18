using System.Collections.Specialized;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Tulpar.App.Services;
using Tulpar.App.ViewModels;
using Tulpar.Core.Session;
using Tulpar.Core.Vehicles;

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
    private bool _isMapVehicleReset = true;
    private bool _hasSentVehicleState;
    private double _lastSentLat;
    private double _lastSentLon;
    private double _lastSentHeadingDeg;
    private bool _lastSentHasHome;
    private double _lastSentHomeLat;
    private double _lastSentHomeLon;
    private VehicleCategory _lastSentCategory;
    private int _lastSentActiveWaypointSeq = -1;

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

    public void SetFollowVehicle(bool enabled)
    {
        PostJsonMessage(new { type = "setFollowVehicle", enabled });
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
            vm.SelectedMissionSeqChanged += OnSelectedMissionSeqChanged;
            vm.FencePolygonChanged += OnFencePolygonChanged;
            vm.RallyPointsChanged += OnRallyPointsChanged;
            vm.MapEditModeChanged += OnMapEditModeChanged;
            vm.FollowVehicleChanged += SetFollowVehicle;
            vm.MissionItems.CollectionChanged += OnCollectionChangedSyncMission;
            vm.FencePoints.CollectionChanged += OnCollectionChangedSyncFence;
            vm.RallyPoints.CollectionChanged += OnCollectionChangedSyncRally;

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
        _viewModel.SelectedMissionSeqChanged -= OnSelectedMissionSeqChanged;
        _viewModel.FencePolygonChanged -= OnFencePolygonChanged;
        _viewModel.RallyPointsChanged -= OnRallyPointsChanged;
        _viewModel.MapEditModeChanged -= OnMapEditModeChanged;
        _viewModel.FollowVehicleChanged -= SetFollowVehicle;
        _viewModel.MissionItems.CollectionChanged -= OnCollectionChangedSyncMission;
        _viewModel.FencePoints.CollectionChanged -= OnCollectionChangedSyncFence;
        _viewModel.RallyPoints.CollectionChanged -= OnCollectionChangedSyncRally;
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
        const double CoordEpsilonDeg = 1e-7;
        const double HeadingEpsilonDeg = 0.25;

        bool resetTrail = _lastConnectedState && !snapshot.IsConnected;
        _lastConnectedState = snapshot.IsConnected;

        if (resetTrail)
        {
            _isMapVehicleReset = true;
            _hasSentVehicleState = false;
            _lastSentActiveWaypointSeq = -1;
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
                    activeWaypointSeq = -1,
                    trail = Array.Empty<object>()
                }
            });
            return;
        }

        if (!snapshot.IsConnected)
        {
            if (_isMapVehicleReset)
            {
                return;
            }

            _isMapVehicleReset = true;
            _hasSentVehicleState = false;
            _lastSentActiveWaypointSeq = -1;
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
                    activeWaypointSeq = -1,
                    trail = Array.Empty<object>()
                }
            });
            return;
        }

        _isMapVehicleReset = false;

        if (_hasSentVehicleState)
        {
            double headingDelta = Math.Abs(((snapshot.HeadingDeg - _lastSentHeadingDeg + 540.0) % 360.0) - 180.0);
            bool samePosition =
                Math.Abs(snapshot.Latitude - _lastSentLat) <= CoordEpsilonDeg &&
                Math.Abs(snapshot.Longitude - _lastSentLon) <= CoordEpsilonDeg &&
                headingDelta <= HeadingEpsilonDeg;
            bool sameHome =
                snapshot.HasHome == _lastSentHasHome &&
                (!snapshot.HasHome ||
                 (Math.Abs(snapshot.HomeLatitude - _lastSentHomeLat) <= CoordEpsilonDeg &&
                  Math.Abs(snapshot.HomeLongitude - _lastSentHomeLon) <= CoordEpsilonDeg));
            bool sameMeta =
                snapshot.VehicleCategory == _lastSentCategory &&
                snapshot.ActiveWaypointSeq == _lastSentActiveWaypointSeq;

            if (samePosition && sameHome && sameMeta)
            {
                return;
            }
        }

        _hasSentVehicleState = true;
        _lastSentLat = snapshot.Latitude;
        _lastSentLon = snapshot.Longitude;
        _lastSentHeadingDeg = snapshot.HeadingDeg;
        _lastSentHasHome = snapshot.HasHome;
        _lastSentHomeLat = snapshot.HomeLatitude;
        _lastSentHomeLon = snapshot.HomeLongitude;
        _lastSentCategory = snapshot.VehicleCategory;
        _lastSentActiveWaypointSeq = snapshot.ActiveWaypointSeq;

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
                homeLon = snapshot.HomeLongitude,
                activeWaypointSeq = snapshot.ActiveWaypointSeq
            }
        });
    }

    private void OnCollectionChangedSyncMission(object? sender, NotifyCollectionChangedEventArgs e) =>
        OnMissionPlanChanged();

    private void OnCollectionChangedSyncFence(object? sender, NotifyCollectionChangedEventArgs e) =>
        OnFencePolygonChanged();

    private void OnCollectionChangedSyncRally(object? sender, NotifyCollectionChangedEventArgs e) =>
        OnRallyPointsChanged();

    private void OnRallyPointsChanged()
    {
        if (_viewModel is null)
        {
            return;
        }

        var points = _viewModel.RallyPoints
            .Select(p => new { lat = p.Latitude, lon = p.Longitude, alt = p.AltitudeMeters })
            .ToArray();
        PostJsonMessage(new { type = "setRally", points });
    }

    private void OnMissionPlanChanged()
    {
        if (_viewModel is null)
        {
            return;
        }

        var items = _viewModel.MissionItems
            .Select(m => new
            {
                seq = m.Seq,
                lat = m.Latitude,
                lon = m.Longitude,
                alt = m.Altitude,
                command = (int)m.Command,
                param1 = m.Param1
            })
            .ToArray();
        PostJsonMessage(new { type = "setMission", items });
    }

    private void OnSelectedMissionSeqChanged(int seq)
    {
        PostJsonMessage(new { type = "setSelectedWaypoint", payload = new { seq } });
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
            MapEditMode.Rally => "rally",
            MapEditMode.Bounds => "bounds",
            MapEditMode.Measure => "measure",
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

        _hasSentVehicleState = false;
        RefreshLayers();
        SetLayer(_pendingLayerId);
        SetFollowVehicle(_viewModel.IsFollowVehicleEnabled);
        OnMapEditModeChanged(_viewModel.ActiveMapEditMode);
        OnMissionPlanChanged();
        OnSelectedMissionSeqChanged(_viewModel.SelectedMissionItem?.Seq ?? -1);
        OnFencePolygonChanged();
        OnRallyPointsChanged();
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
