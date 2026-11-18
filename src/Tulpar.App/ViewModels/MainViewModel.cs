using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Tulpar.App.Services;
using Tulpar.Core.Mavlink;
using Tulpar.Core.Missions;
using Tulpar.Core.Session;
using Tulpar.Core.Transport;
using Tulpar.Core.Vehicles;

namespace Tulpar.App.ViewModels;

/// <summary>
/// Central cockpit ViewModel orchestrating MAVLink transport, vehicle profile adaptation,
/// 2-step safety guards, local polygon geofence monitoring, Map/Camera swap, and mission transfers.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private const byte GcsSystemId = 255;
    private const byte GcsComponentId = (byte)MavComponent.MissionPlanner;
    private const int MaxLogEntries = 500;
    private const int MaxMissionUndoHistory = 25;

    private static readonly IReadOnlyList<MavCmd> DefaultCommands =
    [
        MavCmd.NavWaypoint,
        MavCmd.NavLoiterUnlim,
        MavCmd.NavTakeoff,
        MavCmd.NavLand,
        MavCmd.NavReturnToLaunch
    ];

    private readonly ITransportFactory _transportFactory;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly MainViewModelOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _sessionSync = new();
    private readonly Lock _recorderSync = new();
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly SemaphoreSlim _replayLock = new(1, 1);
    private readonly Dictionary<string, VehicleParameterRow> _allParameterRows = new(StringComparer.Ordinal);

    private ITransport? _transport;
    private TelemetryRecorder? _recorder;
    private MavlinkUdpForwarder? _udpForwarder;
    private CancellationTokenSource? _backgroundCts;
    private Task? _backgroundLoopTask;
    private long _connectionGeneration;

    private TelemetrySnapshot _snapshot = TelemetrySnapshot.Empty;
    private TelemetrySnapshot _hudSnapshot = TelemetrySnapshot.Empty;
    private IVehicleProfile? _activeProfile;
    private (VehicleCategory Category, MavAutopilot Autopilot, Type? ProfileType) _lastProfileKey;
    private string? _lastSyncedVehicleMode;
    private (bool IsTransportConnected, byte SystemId, bool IsArmed, bool HasPendingSafetyAction, FlightModeOption? SelectedMode) _lastCommandRaiseState;
    private byte _dataStreamRequestedSysId;
    private MavType _dataStreamRequestedVehicleType;
    private int _txSequence;
    private bool _disposed;
    private int _disposedFlag;

    private long _lastHudPushTick;
    private long _lastPanelPushTick;
    private PendingTelemetryUpdate? _pendingTelemetryUpdate;

    private FlightMetricsSummary _currentFlightMetrics = new(0.0, TimeSpan.Zero, Array.Empty<MissionLegMetric>());
    private double _cruiseSpeedMps = 3.0;
    private bool _suppressMetricsRecalc;
    private bool _suppressMissionPlanChanged;

    private bool _isPreFlightReady;
    private string _preFlightSummaryText = "HAZIR DEĞİL (0/5)";

    private string _parameterSearchText = string.Empty;
    private string _parameterStatusText = "HAZIR";
    private int _parameterProgressPercent;
    private volatile bool _isDownloadingParameters;

    private List<TlogEntry> _replayEntries = [];
    private int _replayIndex;
    private CancellationTokenSource? _replayCts;
    private Task? _replayLoopTask;
    private long _replayGeneration;
    private bool _isReplayMode;
    private bool _isReplayPlaying;
    private volatile bool _isSeekingReplay;
    private double _replaySpeedMultiplier = 1.0;
    private double _replayProgressPercent;
    private string _replayTimeDisplay = "00:00 / 00:00";
    private string _replayFileName = string.Empty;

    private ConnectionKind _selectedConnectionKind = ConnectionKind.Udp;
    private string? _selectedSerialPort;
    private int _selectedBaudRate = 57600;
    private int _udpListenPort = 14550;
    private string _tcpHost = "127.0.0.1";
    private int _tcpPort = 5760;
    private bool _isTransportConnected;
    private bool _isBusy;
    private FlightModeOption? _selectedMode;
    private MavCmd _selectedNewMissionCommand = MavCmd.NavWaypoint;
    private MavFrame _defaultMissionFrame = MavFrame.GlobalRelativeAltInt;
    private float _defaultWaypointAltitudeMeters = 50f;
    private EditableMissionItem? _selectedMissionItem;
    private EditableFencePoint? _selectedFencePoint;
    private bool _isGeofenceBreached;
    private string _geofenceAlertMessage = string.Empty;
    private SafetyActionRequest? _pendingSafetyAction;
    private bool _isCameraPrimary;
    private VideoSourceKind _selectedVideoSourceKind = VideoSourceKind.UdpRtp;
    private string _videoStreamInput = VideoMrlBuilder.DefaultUdpMrl;
    private string _activeVideoMrl = string.Empty;
    private bool _isVideoActive;
    private string _videoStatusText = "BEKLİYOR";
    private MapEditMode _activeMapEditMode = MapEditMode.None;
    private bool _isUdpForwarderEnabled = true;
    private bool _isMissionDrawerExpanded = true;
    private bool _isLeftPanelVisible = true;
    private double _takeoffAltitudeMeters = 15.0;
    private int _servoChannel = 9;
    private int _servoPwm = 1500;
    private double _guidedDefaultAltitudeMeters = 50.0;
    private string _selectedMapLayerId = "google-satellite-hd";
    private string _missionTransferStatusText = "HAZIR";
    private int _missionTransferProgressPercent;
    private readonly List<MissionUndoSnapshot> _missionUndoStack = [];
    private int _missionUndoSuppressDepth;
    private bool _isMissionDirty;
    private string _dataFreshnessText = "VERİ YOK";
    private string _dataFreshnessState = "Unknown";
    private string _systemHealthSummaryText = "BEKLENİYOR";
    private string _logSearchText = string.Empty;
    private string _selectedLogSeverityFilter = "TÜMÜ";
    private double _defaultRallyAltitudeMeters = 50.0;
    private EditableRallyPoint? _selectedRallyPoint;
    private bool _isFollowVehicleEnabled;
    private CockpitFormFactor _formFactor = CockpitFormFactor.Desktop;
    private double _viewportWidth = 1480.0;
    private double _viewportHeight = 900.0;

    public MainViewModel()
        : this(
            new DefaultTransportFactory(),
            Application.Current?.Dispatcher is { } dispatcher
                ? new WpfUiDispatcher(dispatcher)
                : new InlineUiDispatcher(),
            new MainViewModelOptions())
    {
    }

    public MainViewModel(
        ITransportFactory transportFactory,
        IUiDispatcher? uiDispatcher = null,
        MainViewModelOptions? options = null)
    {
        _transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
        _uiDispatcher = uiDispatcher ?? new InlineUiDispatcher();
        _options = options ?? new MainViewModelOptions();
        _timeProvider = _options.TimeProvider ?? TimeProvider.System;
        _isUdpForwarderEnabled = _options.EnableUdpForwarder;

        Session = new VehicleSession(_timeProvider);
        MissionProtocol = new MissionProtocolManager(GcsSystemId, GcsComponentId);
        ParameterProtocol = new ParameterProtocolManager(GcsSystemId, GcsComponentId);

        if (_options.EnableUdpForwarder)
        {
            try
            {
                _udpForwarder = new MavlinkUdpForwarder(_options.UdpForwarderHost, _options.UdpForwarderPort)
                {
                    IsEnabled = _isUdpForwarderEnabled
                };
            }
            catch (Exception ex)
            {
                AppendLog(MavSeverity.Warning, "FORWARDER", $"UDP yansıtıcı başlatılamadı: {ex.Message}");
            }
        }

        Session.Bus.RawFrameReceived += OnRawFrameReceived;
        Session.Bus.SnapshotUpdated += OnSessionSnapshotUpdated;
        Session.Bus.StatusTextReceived += OnStatusTextReceived;
        Session.Bus.CommandAckReceived += OnCommandAckReceived;
        ParameterProtocol.ParameterUpdated += OnParameterUpdated;
        MissionItems.CollectionChanged += OnMissionItemsCollectionChanged;

        foreach (MavCmd cmd in DefaultCommands)
        {
            AvailableMissionCommands.Add(cmd);
        }

        RefreshSerialPorts();
        RefreshVideoDevices();
        InitializeCommands();
        RefreshPreFlightChecks();
        RecalculateMissionMetrics();
        RefreshDataFreshness();
        RefreshSystemHealth();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<TelemetrySnapshot>? VehicleMapStateUpdated;
    public event Action? MissionPlanChanged;
    public event Action<int>? SelectedMissionSeqChanged;
    public event Action<EditableMissionItem>? MissionWaypointInspectRequested;
    public event Action? FencePolygonChanged;
    public event Action? RallyPointsChanged;
    public event Action<MapEditMode>? MapEditModeChanged;
    public event Action<bool>? FollowVehicleChanged;
    public event Action<string, IReadOnlyList<string>>? VideoStartRequested;
    public event Action? VideoStopRequested;

    public VehicleSession Session { get; }
    public MissionProtocolManager MissionProtocol { get; }
    public ParameterProtocolManager ParameterProtocol { get; }

    public IReadOnlyList<ConnectionKind> ConnectionKinds { get; } =
        [ConnectionKind.Udp, ConnectionKind.Serial, ConnectionKind.Tcp];

    public IReadOnlyList<int> AvailableBaudRates { get; } =
        [57600, 115200, 230400, 460800, 921600];

    public IReadOnlyList<VideoSourceKind> VideoSourceKinds { get; } =
        [VideoSourceKind.UdpRtp, VideoSourceKind.Rtsp, VideoSourceKind.UsbDirectShow];

    public IReadOnlyList<string> LogSeverityFilters { get; } =
        ["TÜMÜ", "BİLGİ", "UYARI", "HATA"];

    public ObservableCollection<string> AvailableSerialPorts { get; } = [];
    public ObservableCollection<string> AvailableVideoDevices { get; } = [];
    public ObservableCollection<FlightModeOption> AvailableModes { get; } = [];
    public ObservableCollection<MavCmd> AvailableMissionCommands { get; } = [];
    public ObservableCollection<EditableMissionItem> MissionItems { get; } = [];
    public ObservableCollection<EditableFencePoint> FencePoints { get; } = [];
    public ObservableCollection<EditableRallyPoint> RallyPoints { get; } = [];
    public ObservableCollection<PreFlightCheckItem> PreFlightChecks { get; } = [];
    public ObservableCollection<SystemHealthItem> SystemHealthItems { get; } = [];
    public ObservableCollection<VehicleParameterRow> FilteredParameters { get; } = [];
    public ObservableCollection<LogEntry> Logs { get; } = [];
    public ObservableCollection<LogEntry> FilteredLogs { get; } = [];

    public TelemetrySnapshot Snapshot
    {
        get => _snapshot;
        private set
        {
            if (SetField(ref _snapshot, value))
            {
                OnPropertyChanged(nameof(VehicleBadgeText));
                OnPropertyChanged(nameof(IsArmed));
                OnPropertyChanged(nameof(ArmedBadgeText));
                OnPropertyChanged(nameof(LinkLost));
                OnPropertyChanged(nameof(BatteryDisplayText));
                OnPropertyChanged(nameof(IsBatteryLow));
                OnPropertyChanged(nameof(IsBatteryCritical));
                OnPropertyChanged(nameof(GpsDisplayText));
                OnPropertyChanged(nameof(RssiDisplayText));
                OnPropertyChanged(nameof(HomeDistanceMeters));
                OnPropertyChanged(nameof(HomeDistanceDisplayText));
                NotifyTelemetryDisplayProperties();
            }
        }
    }

    public TelemetrySnapshot HudSnapshot
    {
        get => _hudSnapshot;
        private set => SetField(ref _hudSnapshot, value);
    }

    public IVehicleProfile? ActiveProfile
    {
        get => _activeProfile;
        private set
        {
            if (SetField(ref _activeProfile, value))
            {
                OnPropertyChanged(nameof(VehicleBadgeText));
            }
        }
    }

    public string VehicleBadgeText => TelemetryDisplayFormatter.FormatVehicleBadge(_snapshot, _activeProfile);

    public ConnectionKind SelectedConnectionKind
    {
        get => _selectedConnectionKind;
        set
        {
            if (SetField(ref _selectedConnectionKind, value))
            {
                if (value == ConnectionKind.Serial)
                {
                    RefreshSerialPorts();
                }

                OnPropertyChanged(nameof(IsSerialConnection));
                OnPropertyChanged(nameof(IsUdpConnection));
                OnPropertyChanged(nameof(IsTcpConnection));
            }
        }
    }

    public bool IsSerialConnection => SelectedConnectionKind == ConnectionKind.Serial;
    public bool IsUdpConnection => SelectedConnectionKind == ConnectionKind.Udp;
    public bool IsTcpConnection => SelectedConnectionKind == ConnectionKind.Tcp;

    public string? SelectedSerialPort
    {
        get => _selectedSerialPort;
        set => SetField(ref _selectedSerialPort, value);
    }

    public int SelectedBaudRate
    {
        get => _selectedBaudRate;
        set => SetField(ref _selectedBaudRate, value);
    }

    public int UdpListenPort
    {
        get => _udpListenPort;
        set => SetField(ref _udpListenPort, value);
    }

    public string TcpHost
    {
        get => _tcpHost;
        set => SetField(ref _tcpHost, value);
    }

    public int TcpPort
    {
        get => _tcpPort;
        set => SetField(ref _tcpPort, value);
    }

    public bool IsTransportConnected
    {
        get => _isTransportConnected;
        private set
        {
            if (SetField(ref _isTransportConnected, value))
            {
                OnPropertyChanged(nameof(ConnectionButtonText));
                NotifyTelemetryDisplayProperties();
                RefreshDataFreshness();
                RefreshSystemHealth();
                RaiseCommandStates();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public string ConnectionButtonText => IsTransportConnected ? "KES" : "BAĞLAN";
    public bool IsArmed => _snapshot.IsArmed;
    public string ArmedBadgeText => _snapshot.IsArmed ? "ARMED" : "DISARMED";
    public bool LinkLost => _snapshot.LinkLost;

    public string BatteryDisplayText => TelemetryDisplayFormatter.FormatBattery(_snapshot);

    public bool IsBatteryCritical =>
        _snapshot.IsConnected &&
        _snapshot.BatteryRemainingPercent is >= 0 and <= 15;

    public bool IsBatteryLow =>
        _snapshot.IsConnected &&
        _snapshot.BatteryRemainingPercent is > 15 and <= 25;

    public string GpsDisplayText => TelemetryDisplayFormatter.FormatGpsDisplay(_snapshot);
    public string RssiDisplayText => TelemetryDisplayFormatter.FormatRssiDisplay(_snapshot);

    public double HomeDistanceMeters =>
        _snapshot.HasHome &&
        TelemetryDisplayFormatter.IsValidGeoCoordinate(_snapshot.Latitude, _snapshot.Longitude) &&
        TelemetryDisplayFormatter.IsValidGeoCoordinate(_snapshot.HomeLatitude, _snapshot.HomeLongitude)
            ? TelemetryDisplayFormatter.ComputeHaversineMeters(
                _snapshot.HomeLatitude,
                _snapshot.HomeLongitude,
                _snapshot.Latitude,
                _snapshot.Longitude)
            : 0.0;

    public string HomeDistanceDisplayText => TelemetryDisplayFormatter.FormatHomeDistance(_snapshot, HomeDistanceMeters);

    public bool HasLiveTelemetry => (IsTransportConnected || IsReplayMode) && _snapshot.IsConnected;

    public string AltitudeRelativeDisplay => TelemetryDisplayFormatter.FormatAltitude(HasLiveTelemetry, _snapshot.AltitudeRelativeMeters);
    public string AltitudeAmslDisplay => TelemetryDisplayFormatter.FormatAltitude(HasLiveTelemetry, _snapshot.AltitudeAmslMeters);
    public string AirspeedDisplay => TelemetryDisplayFormatter.FormatSpeed(HasLiveTelemetry, _snapshot.AirspeedMps);
    public string GroundspeedDisplay => TelemetryDisplayFormatter.FormatSpeed(HasLiveTelemetry, _snapshot.GroundspeedMps);
    public string VerticalSpeedDisplay => TelemetryDisplayFormatter.FormatVerticalSpeed(HasLiveTelemetry, _snapshot.VerticalSpeedMps);
    public string CurrentThrottleDisplay => TelemetryDisplayFormatter.FormatCurrentThrottle(HasLiveTelemetry, _snapshot.BatteryCurrentAmps, _snapshot.ThrottlePercent);
    public string HeadingDisplay => TelemetryDisplayFormatter.FormatHeading(HasLiveTelemetry, _snapshot.HeadingDeg);
    public string RollPitchDisplay => TelemetryDisplayFormatter.FormatRollPitch(HasLiveTelemetry, _snapshot.RollDeg, _snapshot.PitchDeg);
    public string LatitudeDisplay => TelemetryDisplayFormatter.FormatCoordinate(HasLiveTelemetry, _snapshot.Latitude, _snapshot.Longitude, isLatitude: true);
    public string LongitudeDisplay => TelemetryDisplayFormatter.FormatCoordinate(HasLiveTelemetry, _snapshot.Latitude, _snapshot.Longitude, isLatitude: false);

    public string DataFreshnessText
    {
        get => _dataFreshnessText;
        private set => SetField(ref _dataFreshnessText, value);
    }

    public string DataFreshnessState
    {
        get => _dataFreshnessState;
        private set => SetField(ref _dataFreshnessState, value);
    }

    public string SystemHealthSummaryText
    {
        get => _systemHealthSummaryText;
        private set => SetField(ref _systemHealthSummaryText, value);
    }

    public string LogSearchText
    {
        get => _logSearchText;
        set
        {
            string normalized = value ?? string.Empty;
            if (SetField(ref _logSearchText, normalized))
            {
                RebuildFilteredLogs();
            }
        }
    }

    public string SelectedLogSeverityFilter
    {
        get => _selectedLogSeverityFilter;
        set
        {
            string normalized = string.IsNullOrWhiteSpace(value) ? "TÜMÜ" : value;
            if (SetField(ref _selectedLogSeverityFilter, normalized))
            {
                RebuildFilteredLogs();
            }
        }
    }

    public FlightModeOption? SelectedMode
    {
        get => _selectedMode;
        set
        {
            if (SetField(ref _selectedMode, value))
            {
                ApplySelectedModeCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public MavCmd SelectedNewMissionCommand
    {
        get => _selectedNewMissionCommand;
        set => SetField(ref _selectedNewMissionCommand, value);
    }

    public MavFrame DefaultMissionFrame
    {
        get => _defaultMissionFrame;
        set
        {
            if (SetField(ref _defaultMissionFrame, value))
            {
                OnPropertyChanged(nameof(DefaultMissionFrameLabel));
            }
        }
    }

    public string DefaultMissionFrameLabel
    {
        get => _defaultMissionFrame is MavFrame.GlobalInt or MavFrame.Global ? "AMSL" : "REL";
        set => DefaultMissionFrame = string.Equals(value?.Trim(), "AMSL", StringComparison.OrdinalIgnoreCase)
            ? MavFrame.GlobalInt
            : MavFrame.GlobalRelativeAltInt;
    }

    public float DefaultWaypointAltitudeMeters
    {
        get => _defaultWaypointAltitudeMeters;
        set => SetField(ref _defaultWaypointAltitudeMeters, value);
    }

    public EditableMissionItem? SelectedMissionItem
    {
        get => _selectedMissionItem;
        set
        {
            if (SetField(ref _selectedMissionItem, value))
            {
                RemoveSelectedMissionWaypointCommand.RaiseCanExecuteChanged();
                MoveWaypointUpCommand.RaiseCanExecuteChanged();
                MoveWaypointDownCommand.RaiseCanExecuteChanged();
                SelectedMissionSeqChanged?.Invoke(value?.Seq ?? -1);
            }
        }
    }

    public EditableFencePoint? SelectedFencePoint
    {
        get => _selectedFencePoint;
        set
        {
            if (SetField(ref _selectedFencePoint, value))
            {
                RemoveSelectedFencePointCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public EditableRallyPoint? SelectedRallyPoint
    {
        get => _selectedRallyPoint;
        set
        {
            if (SetField(ref _selectedRallyPoint, value))
            {
                RemoveSelectedRallyPointCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public double DefaultRallyAltitudeMeters
    {
        get => _defaultRallyAltitudeMeters;
        set => SetField(ref _defaultRallyAltitudeMeters, value);
    }

    public bool IsGeofenceBreached
    {
        get => _isGeofenceBreached;
        private set
        {
            if (SetField(ref _isGeofenceBreached, value))
            {
                OnPropertyChanged(nameof(GeofenceStatusSummary));
            }
        }
    }

    public string GeofenceAlertMessage
    {
        get => _geofenceAlertMessage;
        private set => SetField(ref _geofenceAlertMessage, value);
    }

    public string GeofenceStatusSummary =>
        FencePoints.Count < 3
            ? $"GEOFENCE PASİF ({FencePoints.Count}/3 NOKTA)"
            : IsGeofenceBreached
                ? "GEOFENCE İHLALİ!"
                : $"GEOFENCE AKTİF ({FencePoints.Count} KÖŞE)";

    public SafetyActionRequest? PendingSafetyAction
    {
        get => _pendingSafetyAction;
        private set
        {
            if (SetField(ref _pendingSafetyAction, value))
            {
                OnPropertyChanged(nameof(HasPendingSafetyAction));
                ConfirmSafetyActionCommand.RaiseCanExecuteChanged();
                CancelSafetyActionCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasPendingSafetyAction => _pendingSafetyAction is not null;

    public bool IsCameraPrimary
    {
        get => _isCameraPrimary;
        private set
        {
            if (SetField(ref _isCameraPrimary, value))
            {
                OnPropertyChanged(nameof(IsMapPrimary));
                OnPropertyChanged(nameof(SwapButtonText));
            }
        }
    }

    public bool IsMapPrimary => !_isCameraPrimary;
    public string SwapButtonText => IsCameraPrimary ? "HARİTAYI BÜYÜT" : "YER DEĞİŞTİR";

    public VideoSourceKind SelectedVideoSourceKind
    {
        get => _selectedVideoSourceKind;
        set
        {
            if (SetField(ref _selectedVideoSourceKind, value))
            {
                VideoStreamInput = value switch
                {
                    VideoSourceKind.UdpRtp => VideoMrlBuilder.DefaultUdpMrl,
                    VideoSourceKind.Rtsp => VideoMrlBuilder.DefaultRtspMrl,
                    VideoSourceKind.UsbDirectShow => AvailableVideoDevices.FirstOrDefault() ?? string.Empty,
                    _ => VideoStreamInput
                };
                OnPropertyChanged(nameof(IsUsbVideoSelected));
            }
        }
    }

    public bool IsUsbVideoSelected => SelectedVideoSourceKind == VideoSourceKind.UsbDirectShow;

    public string VideoStreamInput
    {
        get => _videoStreamInput;
        set => SetField(ref _videoStreamInput, value);
    }

    public string ActiveVideoMrl
    {
        get => _activeVideoMrl;
        private set => SetField(ref _activeVideoMrl, value);
    }

    public bool IsVideoActive
    {
        get => _isVideoActive;
        private set => SetField(ref _isVideoActive, value);
    }

    public string VideoStatusText
    {
        get => _videoStatusText;
        private set => SetField(ref _videoStatusText, value);
    }

    public MapEditMode ActiveMapEditMode
    {
        get => _activeMapEditMode;
        set
        {
            if (SetField(ref _activeMapEditMode, value))
            {
                OnPropertyChanged(nameof(IsMissionMapPickActive));
                OnPropertyChanged(nameof(IsFenceMapPickActive));
                OnPropertyChanged(nameof(IsRallyMapPickActive));
                OnPropertyChanged(nameof(IsMeasureMapPickActive));
                MapEditModeChanged?.Invoke(value);
            }
        }
    }

    public bool IsMissionMapPickActive => ActiveMapEditMode == MapEditMode.Mission;
    public bool IsFenceMapPickActive => ActiveMapEditMode == MapEditMode.Fence;
    public bool IsRallyMapPickActive => ActiveMapEditMode == MapEditMode.Rally;
    public bool IsMeasureMapPickActive => ActiveMapEditMode == MapEditMode.Measure;

    public bool IsFollowVehicleEnabled
    {
        get => _isFollowVehicleEnabled;
        set
        {
            if (SetField(ref _isFollowVehicleEnabled, value))
            {
                FollowVehicleChanged?.Invoke(value);
            }
        }
    }

    public bool IsUdpForwarderEnabled
    {
        get => _isUdpForwarderEnabled;
        set
        {
            if (SetField(ref _isUdpForwarderEnabled, value))
            {
                if (_udpForwarder is not null)
                {
                    _udpForwarder.IsEnabled = value;
                }

                AppendLog(MavSeverity.Info, "FORWARDER",
                    value
                        ? $"UDP MAVLink yansıtıcı aktif ({UdpForwarderEndpointText})."
                        : "UDP MAVLink yansıtıcı durduruldu.");
            }
        }
    }

    public string UdpForwarderEndpointText => $"{_options.UdpForwarderHost}:{_options.UdpForwarderPort}";
    public string UdpForwarderLabel => $"UDP YANSITICI ({UdpForwarderEndpointText})";

    public bool IsMissionDrawerExpanded
    {
        get => _isMissionDrawerExpanded;
        set => SetField(ref _isMissionDrawerExpanded, value);
    }

    public bool IsLeftPanelVisible
    {
        get => _isLeftPanelVisible;
        set => SetField(ref _isLeftPanelVisible, value);
    }

    public CockpitFormFactor FormFactor
    {
        get => _formFactor;
        private set
        {
            if (SetField(ref _formFactor, value))
            {
                OnPropertyChanged(nameof(IsDesktopLayout));
                OnPropertyChanged(nameof(IsTabletLayout));
                OnPropertyChanged(nameof(IsPhoneLandscapeLayout));
                OnPropertyChanged(nameof(RightDockWidth));
                OnPropertyChanged(nameof(RightCameraHeight));
                OnPropertyChanged(nameof(CompactHudHeight));
                OnPropertyChanged(nameof(IsRightLogPanelVisible));
            }
        }
    }

    public bool IsDesktopLayout => _formFactor == CockpitFormFactor.Desktop;
    public bool IsTabletLayout => _formFactor == CockpitFormFactor.Tablet;
    public bool IsPhoneLandscapeLayout => _formFactor == CockpitFormFactor.PhoneLandscape;

    public double RightDockWidth => _formFactor switch
    {
        CockpitFormFactor.PhoneLandscape => 224.0,
        CockpitFormFactor.Tablet => 278.0,
        _ => 328.0
    };

    public double RightCameraHeight => _formFactor switch
    {
        CockpitFormFactor.PhoneLandscape => 116.0,
        CockpitFormFactor.Tablet => 168.0,
        _ => 224.0
    };

    public double CompactHudHeight => _formFactor switch
    {
        CockpitFormFactor.PhoneLandscape => 112.0,
        CockpitFormFactor.Tablet => 146.0,
        _ => 178.0
    };

    public bool IsRightLogPanelVisible => _formFactor != CockpitFormFactor.PhoneLandscape || _viewportHeight >= 460.0;

    public void ApplyViewportSize(double width, double height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        _viewportWidth = width;
        _viewportHeight = height;

        CockpitFormFactor next = (width < 920.0 || height < 520.0)
            ? CockpitFormFactor.PhoneLandscape
            : (width < 1240.0 || height < 660.0)
                ? CockpitFormFactor.Tablet
                : CockpitFormFactor.Desktop;

        bool changed = next != _formFactor;
        FormFactor = next;
        OnPropertyChanged(nameof(IsRightLogPanelVisible));

        if (!changed)
        {
            return;
        }

        switch (next)
        {
            case CockpitFormFactor.Desktop:
                IsLeftPanelVisible = true;
                IsMissionDrawerExpanded = true;
                break;
            case CockpitFormFactor.Tablet:
                IsLeftPanelVisible = false;
                IsMissionDrawerExpanded = true;
                break;
            case CockpitFormFactor.PhoneLandscape:
                IsLeftPanelVisible = false;
                IsMissionDrawerExpanded = false;
                break;
        }
    }

    public double ResolveExpandedMissionDrawerHeight(double fallbackDesktopHeight = 286.0)
    {
        return _formFactor switch
        {
            CockpitFormFactor.PhoneLandscape => Math.Clamp(_viewportHeight * 0.40, 138.0, 196.0),
            CockpitFormFactor.Tablet => Math.Clamp(_viewportHeight * 0.29, 165.0, 240.0),
            _ => Math.Max(200.0, fallbackDesktopHeight)
        };
    }

    public double TakeoffAltitudeMeters
    {
        get => _takeoffAltitudeMeters;
        set => SetField(ref _takeoffAltitudeMeters, value);
    }

    public int ServoChannel
    {
        get => _servoChannel;
        set => SetField(ref _servoChannel, value);
    }

    public int ServoPwm
    {
        get => _servoPwm;
        set => SetField(ref _servoPwm, value);
    }

    public double GuidedDefaultAltitudeMeters
    {
        get => _guidedDefaultAltitudeMeters;
        set => SetField(ref _guidedDefaultAltitudeMeters, value);
    }

    public string SelectedMapLayerId
    {
        get => _selectedMapLayerId;
        set => SetField(ref _selectedMapLayerId, value);
    }

    public MapBounds? SelectedOfflineBounds { get; private set; }

    public string MissionTransferStatusText
    {
        get => _missionTransferStatusText;
        private set => SetField(ref _missionTransferStatusText, value);
    }

    public int MissionTransferProgressPercent
    {
        get => _missionTransferProgressPercent;
        private set => SetField(ref _missionTransferProgressPercent, value);
    }

    public bool IsMissionDirty
    {
        get => _isMissionDirty;
        private set
        {
            if (SetField(ref _isMissionDirty, value))
            {
                OnPropertyChanged(nameof(MissionSyncStateText));
            }
        }
    }

    public string MissionSyncStateText =>
        MissionItems.Count == 0
            ? "GÖREV YOK"
            : IsMissionDirty
                ? "YEREL TASLAK (YÜKLENMEDİ)"
                : "ARACA YÜKLÜ · EŞİTLİ";

    public bool CanUndoMission => _missionUndoStack.Count > 0;

    public FlightMetricsSummary CurrentFlightMetrics
    {
        get => _currentFlightMetrics;
        private set
        {
            if (SetField(ref _currentFlightMetrics, value))
            {
                OnPropertyChanged(nameof(TotalMissionDistanceDisplay));
                OnPropertyChanged(nameof(EstimatedFlightTimeDisplay));
                OnPropertyChanged(nameof(MissionMetricsSummaryText));
                OnPropertyChanged(nameof(MissionLegs));
            }
        }
    }

    public IReadOnlyList<MissionLegMetric> MissionLegs => _currentFlightMetrics.Legs;

    public double CruiseSpeedMps
    {
        get => _cruiseSpeedMps;
        set
        {
            if (double.IsFinite(value) && value > 0.0 && SetField(ref _cruiseSpeedMps, value))
            {
                RecalculateMissionMetrics();
            }
        }
    }

    public string TotalMissionDistanceDisplay => FormatDistanceDisplay(_currentFlightMetrics.TotalDistanceMeters);

    public string EstimatedFlightTimeDisplay => FormatDurationDisplay(_currentFlightMetrics.EstimatedFlightDuration, compact: false);

    public string MissionMetricsSummaryText =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{TotalMissionDistanceDisplay} · {FormatDurationDisplay(_currentFlightMetrics.EstimatedFlightDuration, compact: true)} · MAX {_currentFlightMetrics.MaxAltitudeMeters:0}M");

    public bool IsPreFlightReady
    {
        get => _isPreFlightReady;
        private set => SetField(ref _isPreFlightReady, value);
    }

    public string PreFlightSummaryText
    {
        get => _preFlightSummaryText;
        private set => SetField(ref _preFlightSummaryText, value);
    }

    public string ParameterSearchText
    {
        get => _parameterSearchText;
        set
        {
            string normalized = value ?? string.Empty;
            if (SetField(ref _parameterSearchText, normalized))
            {
                RebuildFilteredParameters();
            }
        }
    }

    public string ParameterStatusText
    {
        get => _parameterStatusText;
        private set => SetField(ref _parameterStatusText, value);
    }

    public int ParameterProgressPercent
    {
        get => _parameterProgressPercent;
        private set => SetField(ref _parameterProgressPercent, value);
    }

    public bool IsReplayMode
    {
        get => _isReplayMode;
        private set
        {
            if (SetField(ref _isReplayMode, value))
            {
                NotifyTelemetryDisplayProperties();
                RefreshDataFreshness();
                RefreshSystemHealth();
                ToggleReplayPlayPauseCommand?.RaiseCanExecuteChanged();
                StopReplayCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsReplayPlaying
    {
        get => _isReplayPlaying;
        private set => SetField(ref _isReplayPlaying, value);
    }

    public double ReplaySpeedMultiplier
    {
        get => _replaySpeedMultiplier;
        private set
        {
            if (SetField(ref _replaySpeedMultiplier, value))
            {
                OnPropertyChanged(nameof(ReplaySpeedDisplay));
            }
        }
    }

    public string ReplaySpeedDisplay => string.Create(CultureInfo.InvariantCulture, $"{_replaySpeedMultiplier:0}X");

    public double ReplayProgressPercent
    {
        get => _replayProgressPercent;
        private set => SetField(ref _replayProgressPercent, value);
    }

    public string ReplayTimeDisplay
    {
        get => _replayTimeDisplay;
        private set => SetField(ref _replayTimeDisplay, value);
    }

    public string ReplayFileName
    {
        get => _replayFileName;
        private set => SetField(ref _replayFileName, value);
    }

    public AsyncRelayCommand ConnectOrDisconnectCommand { get; private set; } = null!;
    public AsyncRelayCommand QuickConnectSitlCommand { get; private set; } = null!;
    public RelayCommand RefreshSerialPortsCommand { get; private set; } = null!;
    public RelayCommand RefreshVideoDevicesCommand { get; private set; } = null!;
    public RelayCommand SwapMapAndCameraCommand { get; private set; } = null!;
    public RelayCommand RequestArmOrDisarmCommand { get; private set; } = null!;
    public RelayCommand RequestRtlCommand { get; private set; } = null!;
    public RelayCommand RequestHoldCommand { get; private set; } = null!;
    public RelayCommand RequestTakeoffCommand { get; private set; } = null!;
    public RelayCommand RequestLandCommand { get; private set; } = null!;
    public RelayCommand RequestStartMissionCommand { get; private set; } = null!;
    public AsyncRelayCommand ConfirmSafetyActionCommand { get; private set; } = null!;
    public RelayCommand CancelSafetyActionCommand { get; private set; } = null!;
    public AsyncRelayCommand ApplySelectedModeCommand { get; private set; } = null!;
    public AsyncRelayCommand TriggerServoCommand { get; private set; } = null!;
    public AsyncRelayCommand SetHomeCurrentCommand { get; private set; } = null!;
    public AsyncRelayCommand ApplyCruiseSpeedCommand { get; private set; } = null!;
    public RelayCommand StartVideoCommand { get; private set; } = null!;
    public RelayCommand StopVideoCommand { get; private set; } = null!;
    public RelayCommand ToggleMissionMapEditCommand { get; private set; } = null!;
    public RelayCommand ToggleFenceMapEditCommand { get; private set; } = null!;
    public RelayCommand ToggleRallyMapEditCommand { get; private set; } = null!;
    public RelayCommand ToggleMeasureMapEditCommand { get; private set; } = null!;
    public RelayCommand AddManualWaypointCommand { get; private set; } = null!;
    public RelayCommand RemoveSelectedMissionWaypointCommand { get; private set; } = null!;
    public RelayCommand MoveWaypointUpCommand { get; private set; } = null!;
    public RelayCommand MoveWaypointDownCommand { get; private set; } = null!;
    public RelayCommand UndoMissionCommand { get; private set; } = null!;
    public RelayCommand RemoveSelectedFencePointCommand { get; private set; } = null!;
    public RelayCommand ClearFencePointsCommand { get; private set; } = null!;
    public RelayCommand RemoveSelectedRallyPointCommand { get; private set; } = null!;
    public RelayCommand ClearRallyPointsCommand { get; private set; } = null!;
    public AsyncRelayCommand UploadMissionCommand { get; private set; } = null!;
    public AsyncRelayCommand DownloadMissionCommand { get; private set; } = null!;
    public AsyncRelayCommand ClearMissionCommand { get; private set; } = null!;
    public AsyncRelayCommand UploadFenceCommand { get; private set; } = null!;
    public AsyncRelayCommand DownloadFenceCommand { get; private set; } = null!;
    public AsyncRelayCommand ClearFenceCommand { get; private set; } = null!;
    public AsyncRelayCommand UploadRallyCommand { get; private set; } = null!;
    public AsyncRelayCommand DownloadRallyCommand { get; private set; } = null!;
    public AsyncRelayCommand ClearRallyCommand { get; private set; } = null!;
    public AsyncRelayCommand RefreshParametersCommand { get; private set; } = null!;
    public AsyncParameterCommand<VehicleParameterRow> ApplyParameterCommand { get; private set; } = null!;
    public AsyncRelayCommand ToggleReplayPlayPauseCommand { get; private set; } = null!;
    public RelayCommand CycleReplaySpeedCommand { get; private set; } = null!;
    public AsyncRelayCommand StopReplayCommand { get; private set; } = null!;
    public RelayCommand ToggleLeftPanelCommand { get; private set; } = null!;
    public RelayCommand ToggleMissionDrawerCommand { get; private set; } = null!;
    public RelayCommand ToggleFollowVehicleCommand { get; private set; } = null!;

    private void InitializeCommands()
    {
        ConnectOrDisconnectCommand = new AsyncRelayCommand(ToggleConnectionAsync, () => !IsBusy, LogCommandError);
        QuickConnectSitlCommand = new AsyncRelayCommand(QuickConnectSitlAsync, () => !IsBusy, LogCommandError);
        RefreshSerialPortsCommand = new RelayCommand(RefreshSerialPorts);
        RefreshVideoDevicesCommand = new RelayCommand(RefreshVideoDevices);
        SwapMapAndCameraCommand = new RelayCommand(SwapMapAndCamera);
        RequestArmOrDisarmCommand = new RelayCommand(RequestArmOrDisarm, CanIssueVehicleCommand);
        RequestRtlCommand = new RelayCommand(RequestRtl, CanIssueVehicleCommand);
        RequestHoldCommand = new RelayCommand(RequestHold, CanIssueVehicleCommand);
        RequestTakeoffCommand = new RelayCommand(RequestTakeoff, CanIssueVehicleCommand);
        RequestLandCommand = new RelayCommand(RequestLand, CanIssueVehicleCommand);
        RequestStartMissionCommand = new RelayCommand(RequestStartMission, CanIssueVehicleCommand);
        ConfirmSafetyActionCommand = new AsyncRelayCommand(ConfirmSafetyActionAsync, () => HasPendingSafetyAction && CanIssueVehicleCommand(), LogCommandError);
        CancelSafetyActionCommand = new RelayCommand(CancelSafetyAction, () => HasPendingSafetyAction);
        ApplySelectedModeCommand = new AsyncRelayCommand(() => ApplyFlightModeAsync(SelectedMode), () => CanIssueVehicleCommand() && SelectedMode is not null, LogCommandError);
        TriggerServoCommand = new AsyncRelayCommand(() => TriggerServoAsync(ServoChannel, ServoPwm), CanIssueVehicleCommand, LogCommandError);
        SetHomeCurrentCommand = new AsyncRelayCommand(SetHomeCurrentAsync, CanIssueVehicleCommand, LogCommandError);
        ApplyCruiseSpeedCommand = new AsyncRelayCommand(ApplyCruiseSpeedAsync, CanIssueVehicleCommand, LogCommandError);
        StartVideoCommand = new RelayCommand(() => StartVideo(null));
        StopVideoCommand = new RelayCommand(StopVideo);
        ToggleMissionMapEditCommand = new RelayCommand(() => ActiveMapEditMode = ActiveMapEditMode == MapEditMode.Mission ? MapEditMode.None : MapEditMode.Mission);
        ToggleFenceMapEditCommand = new RelayCommand(() => ActiveMapEditMode = ActiveMapEditMode == MapEditMode.Fence ? MapEditMode.None : MapEditMode.Fence);
        ToggleRallyMapEditCommand = new RelayCommand(() => ActiveMapEditMode = ActiveMapEditMode == MapEditMode.Rally ? MapEditMode.None : MapEditMode.Rally);
        ToggleMeasureMapEditCommand = new RelayCommand(() => ActiveMapEditMode = ActiveMapEditMode == MapEditMode.Measure ? MapEditMode.None : MapEditMode.Measure);
        AddManualWaypointCommand = new RelayCommand(AddManualWaypoint);
        RemoveSelectedMissionWaypointCommand = new RelayCommand(RemoveSelectedMissionWaypoint, () => SelectedMissionItem is not null);
        MoveWaypointUpCommand = new RelayCommand(() => MoveSelectedWaypoint(-1), () => SelectedMissionItem is not null);
        MoveWaypointDownCommand = new RelayCommand(() => MoveSelectedWaypoint(1), () => SelectedMissionItem is not null);
        UndoMissionCommand = new RelayCommand(UndoMission, () => CanUndoMission);
        RemoveSelectedFencePointCommand = new RelayCommand(RemoveSelectedFencePoint, () => SelectedFencePoint is not null);
        ClearFencePointsCommand = new RelayCommand(ClearFencePoints);
        RemoveSelectedRallyPointCommand = new RelayCommand(RemoveSelectedRallyPoint, () => SelectedRallyPoint is not null);
        ClearRallyPointsCommand = new RelayCommand(ClearRallyPoints);
        UploadMissionCommand = new AsyncRelayCommand(UploadMissionAsync, CanIssueVehicleCommand, LogCommandError);
        DownloadMissionCommand = new AsyncRelayCommand(DownloadMissionAsync, CanIssueVehicleCommand, LogCommandError);
        ClearMissionCommand = new AsyncRelayCommand(ClearMissionAsync, CanIssueVehicleCommand, LogCommandError);
        UploadFenceCommand = new AsyncRelayCommand(UploadFenceAsync, CanIssueVehicleCommand, LogCommandError);
        DownloadFenceCommand = new AsyncRelayCommand(DownloadFenceAsync, CanIssueVehicleCommand, LogCommandError);
        ClearFenceCommand = new AsyncRelayCommand(ClearFenceAsync, CanIssueVehicleCommand, LogCommandError);
        UploadRallyCommand = new AsyncRelayCommand(UploadRallyAsync, CanIssueVehicleCommand, LogCommandError);
        DownloadRallyCommand = new AsyncRelayCommand(DownloadRallyAsync, CanIssueVehicleCommand, LogCommandError);
        ClearRallyCommand = new AsyncRelayCommand(ClearRallyAsync, CanIssueVehicleCommand, LogCommandError);
        RefreshParametersCommand = new AsyncRelayCommand(RefreshParametersAsync, CanIssueVehicleCommand, LogCommandError);
        ApplyParameterCommand = new AsyncParameterCommand<VehicleParameterRow>(ApplyParameterAsync, row => row is not null && CanIssueVehicleCommand(), LogCommandError);
        ToggleReplayPlayPauseCommand = new AsyncRelayCommand(ToggleReplayPlayPauseAsync, () => IsReplayMode && _replayEntries.Count > 0, LogCommandError);
        CycleReplaySpeedCommand = new RelayCommand(CycleReplaySpeed);
        StopReplayCommand = new AsyncRelayCommand(StopReplayAsync, () => IsReplayMode, LogCommandError);
        ToggleLeftPanelCommand = new RelayCommand(() => IsLeftPanelVisible = !IsLeftPanelVisible);
        ToggleMissionDrawerCommand = new RelayCommand(() => IsMissionDrawerExpanded = !IsMissionDrawerExpanded);
        ToggleFollowVehicleCommand = new RelayCommand(() => IsFollowVehicleEnabled = !IsFollowVehicleEnabled);
    }

    public void RefreshSerialPorts()
    {
        IReadOnlyList<string> ports = _options.SerialPortEnumerator is not null
            ? _options.SerialPortEnumerator()
            : SerialTransport.GetAvailablePorts();

        AvailableSerialPorts.Clear();
        foreach (string port in ports)
        {
            AvailableSerialPorts.Add(port);
        }

        if (string.IsNullOrWhiteSpace(SelectedSerialPort) || !AvailableSerialPorts.Contains(SelectedSerialPort))
        {
            SelectedSerialPort = AvailableSerialPorts.FirstOrDefault();
        }
    }

    public void RefreshVideoDevices()
    {
        IReadOnlyList<string> devices = _options.VideoDeviceEnumerator is not null
            ? _options.VideoDeviceEnumerator()
            : DirectShowDeviceEnumerator.GetVideoCaptureDevices();

        AvailableVideoDevices.Clear();
        foreach (string dev in devices)
        {
            AvailableVideoDevices.Add(dev);
        }

        if (SelectedVideoSourceKind == VideoSourceKind.UsbDirectShow && string.IsNullOrWhiteSpace(VideoStreamInput))
        {
            VideoStreamInput = AvailableVideoDevices.FirstOrDefault() ?? string.Empty;
        }
    }

    public async Task ToggleConnectionAsync()
    {
        if (IsTransportConnected)
        {
            await DisconnectAsync().ConfigureAwait(false);
        }
        else
        {
            await ConnectAsync().ConfigureAwait(false);
        }
    }

    public async Task QuickConnectSitlAsync()
    {
        if (Volatile.Read(ref _disposed))
        {
            return;
        }

        SelectedConnectionKind = ConnectionKind.Tcp;
        TcpHost = "127.0.0.1";
        TcpPort = 5760;

        if (IsReplayMode)
        {
            await StopReplayAsync().ConfigureAwait(false);
        }

        if (IsTransportConnected)
        {
            await DisconnectAsync().ConfigureAwait(false);
        }

        await ConnectAsync().ConfigureAwait(false);
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposed))
        {
            return;
        }

        await _lifecycleLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed))
            {
                return;
            }

            lock (_sessionSync)
            {
                if (_transport is not null || IsTransportConnected)
                {
                    return;
                }
            }

            if (IsReplayMode)
            {
                await StopReplayCoreAsync().ConfigureAwait(false);
            }

            _uiDispatcher.InvokeOrBeginInvoke(() => IsBusy = true);
            try
            {
                ITransport transport = CreateConfiguredTransport();
                TelemetryRecorder? recorder = !string.IsNullOrWhiteSpace(_options.TelemetryLogDirectory)
                    ? new TelemetryRecorder(_options.TelemetryLogDirectory, _timeProvider)
                    : null;

                transport.DataReceived += OnTransportDataReceived;
                transport.Disconnected += OnTransportDisconnected;

                try
                {
                    await transport.ConnectAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                    transport.DataReceived -= OnTransportDataReceived;
                    transport.Disconnected -= OnTransportDisconnected;
                    await transport.DisposeAsync().ConfigureAwait(false);
                    recorder?.Dispose();
                    throw;
                }

                long generation = Interlocked.Increment(ref _connectionGeneration);
                Interlocked.Exchange(ref _pendingTelemetryUpdate, null);

                lock (_recorderSync)
                {
                    _recorder = recorder;
                }

                lock (_sessionSync)
                {
                    _transport = transport;
                    _dataStreamRequestedSysId = 0;
                    _dataStreamRequestedVehicleType = MavType.Generic;
                    _backgroundCts = new CancellationTokenSource();
                    _backgroundLoopTask = RunBackgroundMonitorAsync(transport, generation, _backgroundCts.Token);
                }

                _uiDispatcher.InvokeOrBeginInvoke(() =>
                {
                    if (Volatile.Read(ref _disposed) || Volatile.Read(ref _connectionGeneration) != generation)
                    {
                        return;
                    }

                    IsTransportConnected = true;
                    RefreshPreFlightChecks();
                    AppendLog(MavSeverity.Info, "BAĞLANTI", $"Bağlandı: {transport.EndpointDescription}");
                });
            }
            catch (Exception ex)
            {
                _uiDispatcher.InvokeOrBeginInvoke(() =>
                    AppendLog(MavSeverity.Error, "BAĞLANTI", $"Bağlantı hatası: {ex.Message}"));
                throw;
            }
            finally
            {
                _uiDispatcher.InvokeOrBeginInvoke(() => IsBusy = false);
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisconnectCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task DisconnectCoreAsync()
    {
        ITransport? transport;
        TelemetryRecorder? recorder;
        CancellationTokenSource? cts;
        Task? monitorTask;
        bool hadSession;

        long generation = Interlocked.Increment(ref _connectionGeneration);
        Interlocked.Exchange(ref _pendingTelemetryUpdate, null);

        lock (_sessionSync)
        {
            transport = _transport;
            cts = _backgroundCts;
            monitorTask = _backgroundLoopTask;
            hadSession = transport is not null || cts is not null || monitorTask is not null || IsTransportConnected;

            _transport = null;
            _backgroundCts = null;
            _backgroundLoopTask = null;
            _dataStreamRequestedSysId = 0;
            _dataStreamRequestedVehicleType = MavType.Generic;
        }

        lock (_recorderSync)
        {
            recorder = _recorder;
            _recorder = null;
        }

        if (cts is not null)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        if (transport is not null)
        {
            transport.DataReceived -= OnTransportDataReceived;
            transport.Disconnected -= OnTransportDisconnected;
            await transport.DisposeAsync().ConfigureAwait(false);
        }

        if (monitorTask is not null)
        {
            try
            {
                await monitorTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        cts?.Dispose();
        if (recorder is not null)
        {
            try
            {
                recorder.Flush();
            }
            catch
            {
            }

            recorder.Dispose();
        }

        ParameterProtocol.Clear();
        if (!IsReplayMode)
        {
            Session.ResetOnDisconnect();
        }

        if (!hadSession && Volatile.Read(ref _disposed))
        {
            return;
        }

        _uiDispatcher.InvokeOrBeginInvoke(() =>
        {
            if (Volatile.Read(ref _connectionGeneration) != generation && !Volatile.Read(ref _disposed))
            {
                return;
            }

            PendingSafetyAction = null;
            IsTransportConnected = false;
            IsGeofenceBreached = false;
            GeofenceAlertMessage = string.Empty;
            ActiveProfile = null;
            _lastProfileKey = default;
            _lastSyncedVehicleMode = null;
            AvailableModes.Clear();
            SelectedMode = null;
            ResetMissionCommandsToDefault();
            _allParameterRows.Clear();
            FilteredParameters.Clear();
            ParameterProgressPercent = 0;
            ParameterStatusText = "HAZIR";
            RefreshPreFlightChecks();
            RefreshDataFreshness();
            RefreshSystemHealth();
            if (hadSession)
            {
                AppendLog(MavSeverity.Notice, "BAĞLANTI", "Bağlantı kesildi.");
            }
        });
    }

    public void SwapMapAndCamera() => IsCameraPrimary = !IsCameraPrimary;

    public void StartVideo(string? mrlOverride = null)
    {
        string input = string.IsNullOrWhiteSpace(mrlOverride) ? VideoStreamInput : mrlOverride;
        string mrl = VideoMrlBuilder.BuildEffectiveMrl(SelectedVideoSourceKind, input);
        IReadOnlyList<string> options = VideoMrlBuilder.BuildMediaOptions(SelectedVideoSourceKind, input);

        ActiveVideoMrl = mrl;
        IsVideoActive = true;
        VideoStatusText = "BAĞLANIYOR";
        AppendLog(MavSeverity.Info, "KAMERA", $"Video akışı başlatılıyor: {mrl}");
        VideoStartRequested?.Invoke(mrl, options);
    }

    public void StopVideo()
    {
        IsVideoActive = false;
        ActiveVideoMrl = string.Empty;
        VideoStatusText = "BEKLİYOR";
        VideoStopRequested?.Invoke();
    }

    public void UpdateVideoPlaybackStatus(bool isPlaying, string statusText)
    {
        VideoStatusText = statusText;
        if (!isPlaying && !string.IsNullOrEmpty(ActiveVideoMrl))
        {
            IsVideoActive = false;
        }
        else if (isPlaying)
        {
            IsVideoActive = true;
        }
    }

    public void RequestArmOrDisarm()
    {
        if (!CanIssueVehicleCommand())
        {
            AppendLog(MavSeverity.Warning, "GÜVENLİK", "Arm/Disarm için bağlı ve tanımlı bir araç gereklidir.");
            return;
        }

        bool currentlyArmed = Snapshot.IsArmed;
        SafetyActionKind kind = currentlyArmed ? SafetyActionKind.Disarm : SafetyActionKind.Arm;
        string title = currentlyArmed ? "DISARM ONAYI GEREKLİ" : "ARM ONAYI GEREKLİ";
        string desc = currentlyArmed
            ? "Motorları durdurmak (DISARM) için onaylayın. Havada DISARM tehlikelidir!"
            : "Pervane/motorlara güç vermek (ARM) için onaylayın. Çevrenin güvenli olduğundan emin olun.";

        PendingSafetyAction = new SafetyActionRequest(kind, title, desc);
    }

    public void RequestRtl()
    {
        if (!CanIssueVehicleCommand())
        {
            AppendLog(MavSeverity.Warning, "GÜVENLİK", "RTL komutu için bağlı ve tanımlı bir araç gereklidir.");
            return;
        }

        PendingSafetyAction = new SafetyActionRequest(
            SafetyActionKind.Rtl,
            "ACİL RTL ONAYI",
            "Araç kalkış noktasına dönüş (RETURN TO LAUNCH) moduna geçirilecek. Onaylıyor musunuz?");
    }

    public void RequestHold()
    {
        if (!CanIssueVehicleCommand())
        {
            AppendLog(MavSeverity.Warning, "GÜVENLİK", "HOLD komutu için bağlı ve tanımlı bir araç gereklidir.");
            return;
        }

        PendingSafetyAction = new SafetyActionRequest(
            SafetyActionKind.Hold,
            "GÖREVİ DURDUR / HOLD ONAYI",
            "Aktif görev durdurulacak ve araç mevcut konumunda bekleme (HOLD / LOITER) moduna geçirilecek. Onaylıyor musunuz?");
    }

    public void RequestTakeoff()
    {
        if (!CanIssueVehicleCommand())
        {
            AppendLog(MavSeverity.Warning, "GÜVENLİK", "Kalkış komutu için bağlı ve tanımlı bir araç gereklidir.");
            return;
        }

        float alt = (float)Math.Clamp(TakeoffAltitudeMeters, 1.0, 2000.0);
        string description = string.Create(
            CultureInfo.InvariantCulture,
            $"Araç {alt:0.0} M hedef irtifaya kalkış (TAKEOFF) yapacak. Onaylıyor musunuz?");

        IVehicleProfile profile = ActiveProfile ?? VehicleProfileFactory.Create(Snapshot.VehicleType, Snapshot.Autopilot);
        bool needsGuidedSwitch = !string.Equals(Snapshot.ModeName, "GUIDED", StringComparison.OrdinalIgnoreCase) &&
            profile.GetFlightModes(Snapshot.Autopilot).Any(m => string.Equals(m.Name, "GUIDED", StringComparison.OrdinalIgnoreCase));
        if (needsGuidedSwitch)
        {
            description += $"\nℹ Uçuş modu şu an {Snapshot.ModeName}; onaylandığında önce GUIDED moduna geçirilecektir.";
        }

        if (!Snapshot.IsArmed)
        {
            description += "\n⚠ DİKKAT: Araç şu an DISARMED durumda. Otonom kalkış için aracın ARM edilmiş olması gerekir.";
        }

        PendingSafetyAction = new SafetyActionRequest(
            SafetyActionKind.Takeoff,
            "OTONOM KALKIŞ ONAYI",
            description,
            AltitudeMeters: alt);
    }

    public void RequestLand()
    {
        if (!CanIssueVehicleCommand())
        {
            AppendLog(MavSeverity.Warning, "GÜVENLİK", "İniş komutu için bağlı ve tanımlı bir araç gereklidir.");
            return;
        }

        PendingSafetyAction = new SafetyActionRequest(
            SafetyActionKind.Land,
            "OTONOM İNİŞ ONAYI",
            "Araç bulunduğu konuma iniş (LAND) başlatacak. Onaylıyor musunuz?");
    }

    public void RequestStartMission()
    {
        if (!CanIssueVehicleCommand())
        {
            AppendLog(MavSeverity.Warning, "GÜVENLİK", "Görevi başlatmak için bağlı ve tanımlı bir araç gereklidir.");
            return;
        }

        string description = MissionItems.Count > 0
            ? $"Araç AUTO moduna alınacak ve {MissionItems.Count} noktalı görev (MISSION_START) başlatılacak. Onaylıyor musunuz?"
            : "Araç AUTO moduna alınacak ve araç hafızasındaki mevcut görev (MISSION_START) başlatılacak. Onaylıyor musunuz?";

        if (IsMissionDirty)
        {
            description += "\n⚠ UYARI: Görev listesinde henüz 'ARACA YÜKLE' yapılmamış değişiklikler var!";
        }

        if (!Snapshot.IsArmed)
        {
            description += "\n⚠ DİKKAT: Araç şu an DISARMED durumda. Görevin uçuşa geçmesi için aracın ARM edilmiş olması gerekir.";
        }

        PendingSafetyAction = new SafetyActionRequest(
            SafetyActionKind.StartMission,
            "OTONOM GÖREVİ BAŞLAT ONAYI",
            description);
    }

    public void RequestGuidedFlyHere(double lat, double lon, float? altMeters = null, float? loiterRadius = null)
    {
        if (!CanIssueVehicleCommand())
        {
            AppendLog(MavSeverity.Warning, "GÜVENLİK", "Guided yönlendirme için bağlı ve tanımlı bir araç gereklidir.");
            return;
        }

        if (!double.IsFinite(lat) || lat is < -90.0 or > 90.0 ||
            !double.IsFinite(lon) || lon is < -180.0 or > 180.0)
        {
            AppendLog(MavSeverity.Warning, "GÜVENLİK", "Geçersiz Guided koordinatı.");
            return;
        }

        IVehicleProfile profile = ActiveProfile ?? VehicleProfileFactory.Create(Snapshot.VehicleType, Snapshot.Autopilot);
        float effectiveAlt = SafetyCommandBuilder.ResolveSafeGuidedAltitude(profile, Snapshot, altMeters, GuidedDefaultAltitudeMeters);
        PendingSafetyAction = new SafetyActionRequest(
            SafetyActionKind.GuidedFlyHere,
            "GUIDED YÖNLENDİRME ONAYI",
            string.Create(
                CultureInfo.InvariantCulture,
                $"Araç {lat:0.000000}, {lon:0.000000} ({effectiveAlt:0.0} M) hedefine yönlendirilecek. Onaylıyor musunuz?"),
            Latitude: lat,
            Longitude: lon,
            AltitudeMeters: effectiveAlt,
            LoiterRadius: loiterRadius);
    }

    public void CancelSafetyAction() => PendingSafetyAction = null;

    public async Task ConfirmSafetyActionAsync()
    {
        SafetyActionRequest? action = PendingSafetyAction;
        PendingSafetyAction = null;
        if (action is null || !CanIssueVehicleCommand())
        {
            return;
        }

        byte targetSys = Snapshot.SystemId;
        byte targetComp = Snapshot.ComponentId == 0 ? (byte)MavComponent.Autopilot1 : Snapshot.ComponentId;
        IVehicleProfile profile = ActiveProfile ?? VehicleProfileFactory.Create(Snapshot.VehicleType, Snapshot.Autopilot);

        IMavlinkMessage? commandMessage = null;
        MavlinkFrame? explicitFrame = null;

        switch (action.Kind)
        {
            case SafetyActionKind.Arm:
                commandMessage = new CommandLongMessage(targetSys, targetComp, MavCmd.ComponentArmDisarm, 0, Param1: 1f);
                break;
            case SafetyActionKind.Disarm:
                commandMessage = new CommandLongMessage(targetSys, targetComp, MavCmd.ComponentArmDisarm, 0, Param1: 0f);
                break;
            case SafetyActionKind.Rtl:
                commandMessage = new CommandLongMessage(targetSys, targetComp, MavCmd.NavReturnToLaunch, 0);
                break;
            case SafetyActionKind.Hold:
                commandMessage = SafetyCommandBuilder.BuildHoldMessage(targetSys, targetComp, profile, Snapshot.Autopilot);
                break;
            case SafetyActionKind.Takeoff:
                if (!string.Equals(Snapshot.ModeName, "GUIDED", StringComparison.OrdinalIgnoreCase))
                {
                    FlightModeOption? guidedMode = profile.GetFlightModes(Snapshot.Autopilot)
                        .FirstOrDefault(m => string.Equals(m.Name, "GUIDED", StringComparison.OrdinalIgnoreCase));
                    if (guidedMode is not null)
                    {
                        await SendMessageAsync(
                            new SetModeMessage(targetSys, (MavModeFlag)guidedMode.BaseMode, guidedMode.CustomMode),
                            CancellationToken.None).ConfigureAwait(false);
                        await Task.Delay(150).ConfigureAwait(false);
                    }
                }
                MavCmd takeoffCmd = profile.Category == VehicleCategory.Vtol ? MavCmd.NavVtolTakeoff : MavCmd.NavTakeoff;
                commandMessage = new CommandLongMessage(targetSys, targetComp, takeoffCmd, 0, Param7: action.AltitudeMeters);
                break;
            case SafetyActionKind.Land:
                MavCmd landCmd = profile.Category == VehicleCategory.Vtol ? MavCmd.NavVtolLand : MavCmd.NavLand;
                commandMessage = new CommandLongMessage(targetSys, targetComp, landCmd, 0);
                break;
            case SafetyActionKind.StartMission:
                FlightModeOption? autoMode = SafetyCommandBuilder.FindPreferredAutoMissionMode(profile, Snapshot.Autopilot);
                if (autoMode is not null &&
                    !string.Equals(Snapshot.ModeName, autoMode.Name, StringComparison.OrdinalIgnoreCase))
                {
                    await SendMessageAsync(
                        new SetModeMessage(targetSys, (MavModeFlag)autoMode.BaseMode, autoMode.CustomMode),
                        CancellationToken.None).ConfigureAwait(false);
                    await Task.Delay(150).ConfigureAwait(false);
                }
                commandMessage = new CommandLongMessage(targetSys, targetComp, MavCmd.MissionStart, 0);
                break;
            case SafetyActionKind.GuidedFlyHere:
                explicitFrame = profile.BuildGuidedTargetCommand(
                    targetSys,
                    targetComp,
                    GcsSystemId,
                    GcsComponentId,
                    NextTxSequence(),
                    Snapshot.Autopilot,
                    action.Latitude,
                    action.Longitude,
                    action.AltitudeMeters,
                    action.LoiterRadius);
                break;
        }

        if (commandMessage is not null)
        {
            await SendMessageAsync(commandMessage, CancellationToken.None).ConfigureAwait(false);
        }
        else if (explicitFrame is not null)
        {
            byte[] packet = MavlinkEncoder.EncodeV2(
                explicitFrame.SystemId,
                explicitFrame.ComponentId,
                explicitFrame.Sequence,
                explicitFrame.MessageId,
                explicitFrame.Payload.Span,
                MavlinkCrc.TryGetCrcExtra(explicitFrame.MessageId, out byte crcExtra) ? crcExtra : (byte)0);
            await SendRawPacketAsync(packet, CancellationToken.None).ConfigureAwait(false);
        }

        _uiDispatcher.InvokeOrBeginInvoke(() =>
            AppendLog(MavSeverity.Notice, "KOMUT", $"Onaylı komut gönderildi: {action.Kind}"));
    }

    public async Task ApplyFlightModeAsync(FlightModeOption? mode = null)
    {
        FlightModeOption? target = mode ?? SelectedMode;
        if (target is null || !CanIssueVehicleCommand())
        {
            return;
        }

        var setMode = new SetModeMessage(Snapshot.SystemId, (MavModeFlag)target.BaseMode, target.CustomMode);
        await SendMessageAsync(setMode, CancellationToken.None).ConfigureAwait(false);
        _uiDispatcher.InvokeOrBeginInvoke(() =>
            AppendLog(MavSeverity.Info, "MOD", $"Uçuş modu talebi: {target.Name}"));
    }

    public async Task TriggerServoAsync(int channel, int pwm)
    {
        if (!CanIssueVehicleCommand())
        {
            return;
        }

        int clampedChannel = Math.Clamp(channel, 1, 16);
        int clampedPwm = Math.Clamp(pwm, 500, 2500);
        var msg = new CommandLongMessage(
            Snapshot.SystemId,
            Snapshot.ComponentId == 0 ? (byte)MavComponent.Autopilot1 : Snapshot.ComponentId,
            MavCmd.DoSetServo,
            0,
            Param1: clampedChannel,
            Param2: clampedPwm);

        await SendMessageAsync(msg, CancellationToken.None).ConfigureAwait(false);
        _uiDispatcher.InvokeOrBeginInvoke(() =>
            AppendLog(MavSeverity.Info, "SERVO", $"DO_SET_SERVO CH{clampedChannel} -> {clampedPwm} us"));
    }

    public async Task ApplyCruiseSpeedAsync()
    {
        if (!CanIssueVehicleCommand())
        {
            AppendLog(MavSeverity.Warning, "HIZ", "Görev hızı göndermek için bağlı bir araç gereklidir.");
            return;
        }

        float speedMps = (float)Math.Clamp(CruiseSpeedMps, 0.5, 100.0);
        var msg = new CommandLongMessage(
            Snapshot.SystemId,
            Snapshot.ComponentId == 0 ? (byte)MavComponent.Autopilot1 : Snapshot.ComponentId,
            MavCmd.DoChangeSpeed,
            0,
            Param1: 1f,
            Param2: speedMps,
            Param3: -1f);

        await SendMessageAsync(msg, CancellationToken.None).ConfigureAwait(false);
        _uiDispatcher.InvokeOrBeginInvoke(() =>
            AppendLog(MavSeverity.Info, "HIZ", string.Create(CultureInfo.InvariantCulture, $"Görev / uçuş hızı gönderildi: {speedMps:0.0} m/s")));
    }

    public async Task SetHomeCurrentAsync()
    {
        if (!CanIssueVehicleCommand())
        {
            return;
        }

        var msg = new CommandLongMessage(
            Snapshot.SystemId,
            Snapshot.ComponentId == 0 ? (byte)MavComponent.Autopilot1 : Snapshot.ComponentId,
            MavCmd.DoSetHome,
            0,
            Param1: 1f);

        await SendMessageAsync(msg, CancellationToken.None).ConfigureAwait(false);
        _uiDispatcher.InvokeOrBeginInvoke(() =>
            AppendLog(MavSeverity.Info, "HOME", "Home noktası mevcut araç konumu olarak güncellendi."));
    }

    public void AddMissionWaypoint(double lat, double lon)
    {
        if (!TelemetryDisplayFormatter.IsValidGeoCoordinate(lat, lon))
        {
            return;
        }

        RecordMissionUndoSnapshot();

        float alt = ActiveProfile?.SurfaceOnlyMode == true ? 0f : DefaultWaypointAltitudeMeters;
        MavCmd cmd = AvailableMissionCommands.Contains(SelectedNewMissionCommand)
            ? SelectedNewMissionCommand
            : MavCmd.NavWaypoint;

        var item = new EditableMissionItem(
            seq: MissionItems.Count,
            frame: DefaultMissionFrame,
            command: cmd,
            param1: 0f,
            param2: 0f,
            param3: 0f,
            param4: 0f,
            latitude: lat,
            longitude: lon,
            altitude: alt,
            autocontinue: true);

        _suppressMetricsRecalc = true;
        try
        {
            AddMissionItemTracked(item);
            SelectedMissionItem = item;
            RenumberMissionItems();
        }
        finally
        {
            _suppressMetricsRecalc = false;
        }

        MarkMissionDirty();
        RecalculateMissionMetrics();
        MissionPlanChanged?.Invoke();
    }

    public void MoveMissionWaypoint(int seq, double lat, double lon)
    {
        if (!TelemetryDisplayFormatter.IsValidGeoCoordinate(lat, lon))
        {
            return;
        }

        EditableMissionItem? item = MissionItems.FirstOrDefault(m => m.Seq == seq);
        if (item is null)
        {
            return;
        }

        item.Latitude = lat;
        item.Longitude = lon;
        MarkMissionDirty();
        RecalculateMissionMetrics();
        MissionPlanChanged?.Invoke();
    }

    public void MoveMissionWaypointPreview(int seq, double lat, double lon)
    {
        if (!TelemetryDisplayFormatter.IsValidGeoCoordinate(lat, lon))
        {
            return;
        }

        EditableMissionItem? item = MissionItems.FirstOrDefault(m => m.Seq == seq);
        if (item is null)
        {
            return;
        }

        _suppressMissionPlanChanged = true;
        try
        {
            item.Latitude = lat;
            item.Longitude = lon;
        }
        finally
        {
            _suppressMissionPlanChanged = false;
        }
    }

    public void InsertMissionWaypointAfter(int afterSeq, double lat, double lon)
    {
        if (!TelemetryDisplayFormatter.IsValidGeoCoordinate(lat, lon))
        {
            return;
        }

        RecordMissionUndoSnapshot();

        float alt = ActiveProfile?.SurfaceOnlyMode == true ? 0f : DefaultWaypointAltitudeMeters;
        int afterIdx = -1;
        for (int i = 0; i < MissionItems.Count; i++)
        {
            if (MissionItems[i].Seq == afterSeq)
            {
                afterIdx = i;
                break;
            }
        }

        int insertIndex = afterIdx >= 0 ? afterIdx + 1 : MissionItems.Count;
        var item = new EditableMissionItem(
            seq: insertIndex,
            frame: DefaultMissionFrame,
            command: MavCmd.NavWaypoint,
            param1: 0f,
            param2: 0f,
            param3: 0f,
            param4: 0f,
            latitude: lat,
            longitude: lon,
            altitude: alt,
            autocontinue: true);

        _suppressMetricsRecalc = true;
        try
        {
            item.PropertyChanged += OnEditableMissionItemPropertyChanged;
            MissionItems.Insert(insertIndex, item);
            RenumberMissionItems();
            SelectedMissionItem = item;
        }
        finally
        {
            _suppressMetricsRecalc = false;
        }

        MarkMissionDirty();
        RecalculateMissionMetrics();
        MissionPlanChanged?.Invoke();
    }

    public void AddManualWaypoint()
    {
        using var undoBatch = BeginMissionUndoBatch();
        double lat = TelemetryDisplayFormatter.IsValidGeoCoordinate(Snapshot.Latitude, Snapshot.Longitude)
            ? Snapshot.Latitude
            : TelemetryDisplayFormatter.IsValidGeoCoordinate(Snapshot.HomeLatitude, Snapshot.HomeLongitude)
                ? Snapshot.HomeLatitude
                : 39.9334;
        double lon = TelemetryDisplayFormatter.IsValidGeoCoordinate(Snapshot.Latitude, Snapshot.Longitude)
            ? Snapshot.Longitude
            : TelemetryDisplayFormatter.IsValidGeoCoordinate(Snapshot.HomeLatitude, Snapshot.HomeLongitude)
                ? Snapshot.HomeLongitude
                : 32.8597;

        if (MissionItems.Count > 0)
        {
            EditableMissionItem last = MissionItems[^1];
            lat = last.Latitude + 0.001;
            lon = last.Longitude + 0.001;
        }

        AddMissionWaypoint(lat, lon);
    }

    public (double Lat, double Lon) ResolveDefaultPatternCenter()
    {
        if (TelemetryDisplayFormatter.IsValidGeoCoordinate(Snapshot.Latitude, Snapshot.Longitude))
        {
            return (Snapshot.Latitude, Snapshot.Longitude);
        }

        if (TelemetryDisplayFormatter.IsValidGeoCoordinate(Snapshot.HomeLatitude, Snapshot.HomeLongitude))
        {
            return (Snapshot.HomeLatitude, Snapshot.HomeLongitude);
        }

        EditableMissionItem? firstGeo = MissionItems.FirstOrDefault(m =>
            TelemetryDisplayFormatter.IsValidGeoCoordinate(m.Latitude, m.Longitude));
        if (firstGeo is not null)
        {
            return (firstGeo.Latitude, firstGeo.Longitude);
        }

        if (FencePoints.Count >= 3)
        {
            return (FencePoints.Average(f => f.Latitude), FencePoints.Average(f => f.Longitude));
        }

        return (39.9334, 32.8597);
    }

    public int ApplyMissionPattern(MissionPatternRequest request, bool replaceExisting = true)
    {
        ArgumentNullException.ThrowIfNull(request);

        IReadOnlyList<EditableMissionItem> generated = MissionPatternGenerator.Generate(request);
        if (generated.Count == 0)
        {
            AppendLog(MavSeverity.Warning, "ROTA", "Seçilen şablon parametreleriyle waypoint üretilemedi.");
            return 0;
        }

        using var undoBatch = BeginMissionUndoBatch();
        _suppressMetricsRecalc = true;
        try
        {
            if (replaceExisting)
            {
                ClearMissionItemsTracked();
            }

            foreach (EditableMissionItem item in generated)
            {
                AddMissionItemTracked(item);
            }

            RenumberMissionItems();
            SelectedMissionItem = MissionItems.FirstOrDefault();
        }
        finally
        {
            _suppressMetricsRecalc = false;
        }

        MarkMissionDirty();
        RecalculateMissionMetrics();
        MissionPlanChanged?.Invoke();
        AppendLog(MavSeverity.Info, "ROTA", $"Şablon uygulandı: {request.Kind} ({generated.Count} nokta).");
        return generated.Count;
    }

    public bool ConnectLastWaypointToTarget(int targetSeq, int repeatCount = -1, bool useDoJump = true)
    {
        if (MissionItems.Count == 0)
        {
            AppendLog(MavSeverity.Warning, "ROTA", "Döngüye bağlamak için en az 1 waypoint bulunmalıdır.");
            return false;
        }

        EditableMissionItem? target = MissionItems.FirstOrDefault(m => m.Seq == targetSeq);
        if (target is null)
        {
            AppendLog(MavSeverity.Warning, "ROTA", $"Hedef WP #{targetSeq} bulunamadı.");
            return false;
        }

        EditableMissionItem added;
        if (useDoJump)
        {
            added = new EditableMissionItem(
                seq: MissionItems.Count,
                frame: MavFrame.Mission,
                command: MavCmd.DoJump,
                param1: target.Seq,
                param2: repeatCount,
                param3: 0f,
                param4: 0f,
                latitude: 0.0,
                longitude: 0.0,
                altitude: 0f,
                autocontinue: true);
        }
        else
        {
            if (!TelemetryDisplayFormatter.IsValidGeoCoordinate(target.Latitude, target.Longitude))
            {
                AppendLog(MavSeverity.Warning, "ROTA", $"WP #{target.Seq} geçerli bir coğrafi konuma sahip değil.");
                return false;
            }

            added = new EditableMissionItem(
                seq: MissionItems.Count,
                frame: target.Frame,
                command: MavCmd.NavWaypoint,
                param1: 0f,
                param2: 0f,
                param3: 0f,
                param4: 0f,
                latitude: target.Latitude,
                longitude: target.Longitude,
                altitude: target.Altitude,
                autocontinue: true);
        }

        RecordMissionUndoSnapshot();

        _suppressMetricsRecalc = true;
        try
        {
            AddMissionItemTracked(added);
            RenumberMissionItems();
            SelectedMissionItem = added;
        }
        finally
        {
            _suppressMetricsRecalc = false;
        }

        MarkMissionDirty();
        RecalculateMissionMetrics();
        MissionPlanChanged?.Invoke();
        AppendLog(
            MavSeverity.Info,
            "ROTA",
            useDoJump
                ? $"Son waypoint WP #{target.Seq} noktasına DO_JUMP ile bağlandı (Tekrar: {(repeatCount < 0 ? "∞" : repeatCount)})."
                : $"Son waypoint WP #{target.Seq} konumuna dönüş noktası eklenerek kapatıldı.");
        return true;
    }

    public void RemoveSelectedMissionWaypoint()
    {
        if (SelectedMissionItem is null)
        {
            return;
        }

        RecordMissionUndoSnapshot();

        _suppressMetricsRecalc = true;
        try
        {
            SelectedMissionItem.PropertyChanged -= OnEditableMissionItemPropertyChanged;
            MissionItems.Remove(SelectedMissionItem);
            SelectedMissionItem = null;
            RenumberMissionItems();
        }
        finally
        {
            _suppressMetricsRecalc = false;
        }

        MarkMissionDirty();
        RecalculateMissionMetrics();
        MissionPlanChanged?.Invoke();
    }

    private void MoveSelectedWaypoint(int delta)
    {
        if (SelectedMissionItem is null)
        {
            return;
        }

        int currentIdx = MissionItems.IndexOf(SelectedMissionItem);
        int targetIdx = currentIdx + delta;
        if (currentIdx < 0 || targetIdx < 0 || targetIdx >= MissionItems.Count)
        {
            return;
        }

        RecordMissionUndoSnapshot();

        _suppressMetricsRecalc = true;
        try
        {
            MissionItems.Move(currentIdx, targetIdx);
            RenumberMissionItems();
        }
        finally
        {
            _suppressMetricsRecalc = false;
        }

        MarkMissionDirty();
        RecalculateMissionMetrics();
        MissionPlanChanged?.Invoke();
    }

    public void UndoMission()
    {
        if (_missionUndoStack.Count == 0)
        {
            return;
        }

        MissionUndoSnapshot snapshot = _missionUndoStack[^1];
        _missionUndoStack.RemoveAt(_missionUndoStack.Count - 1);

        _missionUndoSuppressDepth++;
        _suppressMetricsRecalc = true;
        try
        {
            ClearMissionItemsTracked();
            foreach (MissionItem item in snapshot.Items)
            {
                AddMissionItemTracked(EditableMissionItem.FromMissionItem(item));
            }

            RenumberMissionItems();
            SelectedMissionItem = snapshot.SelectedIndex >= 0 && snapshot.SelectedIndex < MissionItems.Count
                ? MissionItems[snapshot.SelectedIndex]
                : MissionItems.FirstOrDefault();
            IsMissionDirty = snapshot.WasDirty;
        }
        finally
        {
            _suppressMetricsRecalc = false;
            _missionUndoSuppressDepth--;
        }

        RecalculateMissionMetrics();
        MissionPlanChanged?.Invoke();
        OnPropertyChanged(nameof(MissionSyncStateText));
        OnPropertyChanged(nameof(CanUndoMission));
        UndoMissionCommand.RaiseCanExecuteChanged();
        AppendLog(MavSeverity.Info, "GÖREV", $"Son görev işlemi geri alındı ({MissionItems.Count} WP).");
    }

    public void AddFencePoint(double lat, double lon)
    {
        if (!TelemetryDisplayFormatter.IsValidGeoCoordinate(lat, lon))
        {
            return;
        }

        var pt = new EditableFencePoint(FencePoints.Count, lat, lon);
        AddFencePointTracked(pt);
        SelectedFencePoint = pt;
        RenumberFencePoints();
        EvaluateLocalGeofence(Snapshot);
        RefreshPreFlightChecks();
        OnPropertyChanged(nameof(GeofenceStatusSummary));
        FencePolygonChanged?.Invoke();
    }

    public void MoveFencePoint(int index, double lat, double lon)
    {
        if (!TelemetryDisplayFormatter.IsValidGeoCoordinate(lat, lon) || index < 0 || index >= FencePoints.Count)
        {
            return;
        }

        FencePoints[index].Latitude = lat;
        FencePoints[index].Longitude = lon;
        EvaluateLocalGeofence(Snapshot);
        RefreshPreFlightChecks();
        FencePolygonChanged?.Invoke();
    }

    public void RemoveSelectedFencePoint()
    {
        if (SelectedFencePoint is null)
        {
            return;
        }

        SelectedFencePoint.PropertyChanged -= OnEditableFencePointPropertyChanged;
        FencePoints.Remove(SelectedFencePoint);
        SelectedFencePoint = null;
        RenumberFencePoints();
        EvaluateLocalGeofence(Snapshot);
        RefreshPreFlightChecks();
        OnPropertyChanged(nameof(GeofenceStatusSummary));
        FencePolygonChanged?.Invoke();
    }

    public void ClearFencePoints()
    {
        ClearFencePointsTracked();
        SelectedFencePoint = null;
        EvaluateLocalGeofence(Snapshot);
        RefreshPreFlightChecks();
        OnPropertyChanged(nameof(GeofenceStatusSummary));
        FencePolygonChanged?.Invoke();
    }

    public async Task UploadMissionAsync()
    {
        if (!CanIssueVehicleCommand())
        {
            return;
        }

        RenumberMissionItems();
        List<MissionItem> items = MissionItems.Select(x => x.ToMissionItem()).ToList();
        byte compId = Snapshot.ComponentId == 0 ? (byte)MavComponent.Autopilot1 : Snapshot.ComponentId;

        try
        {
            await MissionProtocol.UploadAsync(
                Snapshot.SystemId,
                compId,
                MavMissionType.Mission,
                items,
                (bytes, ct) => SendRawPacketAsync(bytes, ct),
                CreateMissionProgressReporter(),
                CancellationToken.None).ConfigureAwait(false);

            _uiDispatcher.InvokeOrBeginInvoke(() =>
            {
                IsMissionDirty = false;
                OnPropertyChanged(nameof(MissionSyncStateText));
                MissionTransferStatusText = $"GÖREV YÜKLENDİ ({items.Count} WP)";
                MissionTransferProgressPercent = 100;
                AppendLog(MavSeverity.Info, "GÖREV", $"Araca {items.Count} görev noktası yüklendi.");
            });
        }
        catch (Exception ex)
        {
            _uiDispatcher.InvokeOrBeginInvoke(() =>
            {
                MissionTransferStatusText = "GÖREV YÜKLEME HATASI";
                AppendLog(MavSeverity.Error, "GÖREV", $"Görev yüklenemedi: {ex.Message}");
            });
        }
    }

    public async Task DownloadMissionAsync()
    {
        if (!CanIssueVehicleCommand())
        {
            return;
        }

        byte compId = Snapshot.ComponentId == 0 ? (byte)MavComponent.Autopilot1 : Snapshot.ComponentId;
        try
        {
            IReadOnlyList<MissionItem> downloaded = await MissionProtocol.DownloadAsync(
                Snapshot.SystemId,
                compId,
                MavMissionType.Mission,
                (bytes, ct) => SendRawPacketAsync(bytes, ct),
                CreateMissionProgressReporter(),
                CancellationToken.None).ConfigureAwait(false);

            _uiDispatcher.InvokeOrBeginInvoke(() =>
            {
                _suppressMetricsRecalc = true;
                try
                {
                    ClearMissionItemsTracked();
                    foreach (MissionItem item in downloaded)
                    {
                        AddMissionItemTracked(EditableMissionItem.FromMissionItem(item));
                    }

                    RenumberMissionItems();
                }
                finally
                {
                    _suppressMetricsRecalc = false;
                }

                RecalculateMissionMetrics();
                IsMissionDirty = false;
                OnPropertyChanged(nameof(MissionSyncStateText));
                MissionTransferStatusText = $"GÖREV OKUNDU ({MissionItems.Count} WP)";
                MissionTransferProgressPercent = 100;
                MissionPlanChanged?.Invoke();
                AppendLog(MavSeverity.Info, "GÖREV", $"Araçtan {MissionItems.Count} görev noktası okundu.");
            });
        }
        catch (Exception ex)
        {
            _uiDispatcher.InvokeOrBeginInvoke(() =>
            {
                MissionTransferStatusText = "GÖREV OKUMA HATASI";
                AppendLog(MavSeverity.Error, "GÖREV", $"Görev okunamadı: {ex.Message}");
            });
        }
    }

    public async Task ClearMissionAsync()
    {
        if (!CanIssueVehicleCommand())
        {
            return;
        }

        byte compId = Snapshot.ComponentId == 0 ? (byte)MavComponent.Autopilot1 : Snapshot.ComponentId;
        try
        {
            await MissionProtocol.ClearAllAsync(
                Snapshot.SystemId,
                compId,
                MavMissionType.Mission,
                (bytes, ct) => SendRawPacketAsync(bytes, ct),
                CancellationToken.None).ConfigureAwait(false);

            _uiDispatcher.InvokeOrBeginInvoke(() =>
            {
                ClearMissionItemsTracked();
                IsMissionDirty = false;
                OnPropertyChanged(nameof(MissionSyncStateText));
                RecalculateMissionMetrics();
                MissionTransferStatusText = "GÖREV TEMİZLENDİ";
                MissionTransferProgressPercent = 0;
                MissionPlanChanged?.Invoke();
                AppendLog(MavSeverity.Notice, "GÖREV", "Araç üzerindeki görev temizlendi.");
            });
        }
        catch (Exception ex)
        {
            _uiDispatcher.InvokeOrBeginInvoke(() =>
            {
                MissionTransferStatusText = "TEMİZLEME HATASI";
                AppendLog(MavSeverity.Error, "GÖREV", $"Görev temizlenemedi: {ex.Message}");
            });
        }
    }

    public async Task UploadFenceAsync()
    {
        if (!CanIssueVehicleCommand())
        {
            return;
        }

        int vertexCount = FencePoints.Count;
        var fenceItems = new List<MissionItem>(vertexCount);
        for (int i = 0; i < vertexCount; i++)
        {
            EditableFencePoint pt = FencePoints[i];
            fenceItems.Add(new MissionItem(
                i, MavFrame.GlobalInt, MavCmd.NavFencePolygonVertexInclusion,
                vertexCount, 0f, 0f, 0f, pt.Latitude, pt.Longitude, 0f, false));
        }

        byte compId = Snapshot.ComponentId == 0 ? (byte)MavComponent.Autopilot1 : Snapshot.ComponentId;
        try
        {
            await MissionProtocol.UploadAsync(
                Snapshot.SystemId,
                compId,
                MavMissionType.Fence,
                fenceItems,
                (bytes, ct) => SendRawPacketAsync(bytes, ct),
                CreateMissionProgressReporter(),
                CancellationToken.None).ConfigureAwait(false);

            _uiDispatcher.InvokeOrBeginInvoke(() =>
            {
                MissionTransferStatusText = $"GEOFENCE YÜKLENDİ ({vertexCount} KÖŞE)";
                MissionTransferProgressPercent = 100;
                AppendLog(MavSeverity.Info, "GEOFENCE", $"Araca {vertexCount} geofence köşesi yüklendi.");
            });
        }
        catch (Exception ex)
        {
            _uiDispatcher.InvokeOrBeginInvoke(() =>
            {
                MissionTransferStatusText = "GEOFENCE YÜKLEME HATASI";
                AppendLog(MavSeverity.Error, "GEOFENCE", $"Geofence yüklenemedi: {ex.Message}");
            });
        }
    }

    public async Task DownloadFenceAsync()
    {
        if (!CanIssueVehicleCommand())
        {
            return;
        }

        byte compId = Snapshot.ComponentId == 0 ? (byte)MavComponent.Autopilot1 : Snapshot.ComponentId;
        try
        {
            IReadOnlyList<MissionItem> downloaded = await MissionProtocol.DownloadAsync(
                Snapshot.SystemId,
                compId,
                MavMissionType.Fence,
                (bytes, ct) => SendRawPacketAsync(bytes, ct),
                CreateMissionProgressReporter(),
                CancellationToken.None).ConfigureAwait(false);

            _uiDispatcher.InvokeOrBeginInvoke(() =>
            {
                ClearFencePointsTracked();
                int idx = 0;
                foreach (MissionItem item in downloaded)
                {
                    if (item.Command == MavCmd.NavFencePolygonVertexInclusion ||
                        (item.Command != MavCmd.NavFenceReturnPoint &&
                         TelemetryDisplayFormatter.IsValidGeoCoordinate(item.Latitude, item.Longitude)))
                    {
                        AddFencePointTracked(new EditableFencePoint(idx++, item.Latitude, item.Longitude));
                    }
                }

                EvaluateLocalGeofence(Snapshot);
                RefreshPreFlightChecks();
                OnPropertyChanged(nameof(GeofenceStatusSummary));
                FencePolygonChanged?.Invoke();
                MissionTransferStatusText = $"GEOFENCE OKUNDU ({FencePoints.Count} KÖŞE)";
                MissionTransferProgressPercent = 100;
                AppendLog(MavSeverity.Info, "GEOFENCE", $"Araçtan {FencePoints.Count} geofence noktası okundu.");
            });
        }
        catch (Exception ex)
        {
            _uiDispatcher.InvokeOrBeginInvoke(() =>
            {
                MissionTransferStatusText = "GEOFENCE OKUMA HATASI";
                AppendLog(MavSeverity.Error, "GEOFENCE", $"Geofence okunamadı: {ex.Message}");
            });
        }
    }

    public async Task ClearFenceAsync()
    {
        if (!CanIssueVehicleCommand())
        {
            return;
        }

        byte compId = Snapshot.ComponentId == 0 ? (byte)MavComponent.Autopilot1 : Snapshot.ComponentId;
        try
        {
            await MissionProtocol.ClearAllAsync(
                Snapshot.SystemId,
                compId,
                MavMissionType.Fence,
                (bytes, ct) => SendRawPacketAsync(bytes, ct),
                CancellationToken.None).ConfigureAwait(false);

            _uiDispatcher.InvokeOrBeginInvoke(() =>
            {
                ClearFencePoints();
                MissionTransferStatusText = "GEOFENCE TEMİZLENDİ";
                MissionTransferProgressPercent = 0;
                AppendLog(MavSeverity.Notice, "GEOFENCE", "Araç üzerindeki geofence temizlendi.");
            });
        }
        catch (Exception ex)
        {
            _uiDispatcher.InvokeOrBeginInvoke(() =>
            {
                MissionTransferStatusText = "GEOFENCE TEMİZLEME HATASI";
                AppendLog(MavSeverity.Error, "GEOFENCE", $"Geofence temizlenemedi: {ex.Message}");
            });
        }
    }

    public void AddRallyPoint(double lat, double lon)
    {
        if (!TelemetryDisplayFormatter.IsValidGeoCoordinate(lat, lon))
        {
            return;
        }

        float alt = (float)Math.Clamp(DefaultRallyAltitudeMeters, 1.0, 5000.0);
        var pt = new EditableRallyPoint(RallyPoints.Count, lat, lon, alt);
        pt.PropertyChanged += OnEditableRallyPointPropertyChanged;
        RallyPoints.Add(pt);
        SelectedRallyPoint = pt;
        RallyPointsChanged?.Invoke();
    }

    public void MoveRallyPoint(int index, double lat, double lon)
    {
        if (index < 0 || index >= RallyPoints.Count || !TelemetryDisplayFormatter.IsValidGeoCoordinate(lat, lon))
        {
            return;
        }

        RallyPoints[index].Latitude = lat;
        RallyPoints[index].Longitude = lon;
        RallyPointsChanged?.Invoke();
    }

    public void RemoveSelectedRallyPoint()
    {
        if (SelectedRallyPoint is null)
        {
            return;
        }

        SelectedRallyPoint.PropertyChanged -= OnEditableRallyPointPropertyChanged;
        RallyPoints.Remove(SelectedRallyPoint);
        SelectedRallyPoint = null;
        for (int i = 0; i < RallyPoints.Count; i++)
        {
            RallyPoints[i].Index = i;
        }

        RallyPointsChanged?.Invoke();
    }

    public void ClearRallyPoints()
    {
        foreach (EditableRallyPoint pt in RallyPoints)
        {
            pt.PropertyChanged -= OnEditableRallyPointPropertyChanged;
        }
        RallyPoints.Clear();
        SelectedRallyPoint = null;
        RallyPointsChanged?.Invoke();
    }

    private void OnEditableRallyPointPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EditableRallyPoint.Latitude) or nameof(EditableRallyPoint.Longitude) or nameof(EditableRallyPoint.AltitudeMeters))
        {
            RallyPointsChanged?.Invoke();
        }
    }

    public async Task UploadRallyAsync()
    {
        if (!CanIssueVehicleCommand())
        {
            return;
        }

        List<MissionItem> rallyItems = RallyPoints.Select((r, idx) => r.ToMissionItem(idx)).ToList();
        byte compId = Snapshot.ComponentId == 0 ? (byte)MavComponent.Autopilot1 : Snapshot.ComponentId;
        try
        {
            await MissionProtocol.UploadAsync(
                Snapshot.SystemId,
                compId,
                MavMissionType.Rally,
                rallyItems,
                (bytes, ct) => SendRawPacketAsync(bytes, ct),
                CreateMissionProgressReporter(),
                CancellationToken.None).ConfigureAwait(false);

            _uiDispatcher.InvokeOrBeginInvoke(() =>
            {
                MissionTransferStatusText = $"RALLY YÜKLENDİ ({rallyItems.Count} NOKTA)";
                MissionTransferProgressPercent = 100;
                AppendLog(MavSeverity.Info, "RALLY", $"Araca {rallyItems.Count} rally acil iniş noktası yüklendi.");
            });
        }
        catch (Exception ex)
        {
            _uiDispatcher.InvokeOrBeginInvoke(() =>
            {
                MissionTransferStatusText = "RALLY YÜKLEME HATASI";
                AppendLog(MavSeverity.Error, "RALLY", $"Rally noktaları yüklenemedi: {ex.Message}");
            });
        }
    }

    public async Task DownloadRallyAsync()
    {
        if (!CanIssueVehicleCommand())
        {
            return;
        }

        byte compId = Snapshot.ComponentId == 0 ? (byte)MavComponent.Autopilot1 : Snapshot.ComponentId;
        try
        {
            IReadOnlyList<MissionItem> downloaded = await MissionProtocol.DownloadAsync(
                Snapshot.SystemId,
                compId,
                MavMissionType.Rally,
                (bytes, ct) => SendRawPacketAsync(bytes, ct),
                CreateMissionProgressReporter(),
                CancellationToken.None).ConfigureAwait(false);

            _uiDispatcher.InvokeOrBeginInvoke(() =>
            {
                foreach (EditableRallyPoint old in RallyPoints)
                {
                    old.PropertyChanged -= OnEditableRallyPointPropertyChanged;
                }
                RallyPoints.Clear();

                int idx = 0;
                foreach (MissionItem item in downloaded)
                {
                    if (TelemetryDisplayFormatter.IsValidGeoCoordinate(item.Latitude, item.Longitude))
                    {
                        var pt = new EditableRallyPoint(
                            idx++,
                            item.Latitude,
                            item.Longitude,
                            item.Altitude,
                            item.Param1,
                            (ushort)Math.Clamp(item.Param2, 0f, ushort.MaxValue),
                            (byte)Math.Clamp(item.Param3, 0f, byte.MaxValue));
                        pt.PropertyChanged += OnEditableRallyPointPropertyChanged;
                        RallyPoints.Add(pt);
                    }
                }

                RallyPointsChanged?.Invoke();
                MissionTransferStatusText = $"RALLY OKUNDU ({RallyPoints.Count} NOKTA)";
                MissionTransferProgressPercent = 100;
                AppendLog(MavSeverity.Info, "RALLY", $"Araçtan {RallyPoints.Count} rally noktası okundu.");
            });
        }
        catch (Exception ex)
        {
            _uiDispatcher.InvokeOrBeginInvoke(() =>
            {
                MissionTransferStatusText = "RALLY OKUMA HATASI";
                AppendLog(MavSeverity.Error, "RALLY", $"Rally noktaları okunamadı: {ex.Message}");
            });
        }
    }

    public async Task ClearRallyAsync()
    {
        if (!CanIssueVehicleCommand())
        {
            return;
        }

        byte compId = Snapshot.ComponentId == 0 ? (byte)MavComponent.Autopilot1 : Snapshot.ComponentId;
        try
        {
            await MissionProtocol.ClearAllAsync(
                Snapshot.SystemId,
                compId,
                MavMissionType.Rally,
                (bytes, ct) => SendRawPacketAsync(bytes, ct),
                CancellationToken.None).ConfigureAwait(false);

            _uiDispatcher.InvokeOrBeginInvoke(() =>
            {
                ClearRallyPoints();
                MissionTransferStatusText = "RALLY TEMİZLENDİ";
                MissionTransferProgressPercent = 0;
                AppendLog(MavSeverity.Notice, "RALLY", "Araç üzerindeki rally noktaları temizlendi.");
            });
        }
        catch (Exception ex)
        {
            _uiDispatcher.InvokeOrBeginInvoke(() =>
            {
                MissionTransferStatusText = "RALLY TEMİZLEME HATASI";
                AppendLog(MavSeverity.Error, "RALLY", $"Rally temizlenemedi: {ex.Message}");
            });
        }
    }

    public string ExportWpl110()
    {
        RenumberMissionItems();
        return MissionFileCodec.ExportQgcWpl110(MissionItems.Select(x => x.ToMissionItem()).ToList());
    }

    public void ImportWpl110(string content)
    {
        IReadOnlyList<MissionItem> imported = MissionFileCodec.ImportQgcWpl110(content);
        var nextMissionItems = new List<EditableMissionItem>(imported.Count);
        for (int i = 0; i < imported.Count; i++)
        {
            MissionItem item = imported[i];
            ValidateImportedMissionItem(item, i);
            EditableMissionItem editable = EditableMissionItem.FromMissionItem(item);
            editable.Seq = i;
            nextMissionItems.Add(editable);
        }

        using var undoBatch = BeginMissionUndoBatch();
        _suppressMetricsRecalc = true;
        try
        {
            ClearMissionItemsTracked();
            foreach (EditableMissionItem item in nextMissionItems)
            {
                AddMissionItemTracked(item);
            }

            SelectedMissionItem = MissionItems.FirstOrDefault();
        }
        finally
        {
            _suppressMetricsRecalc = false;
        }

        MarkMissionDirty();
        RecalculateMissionMetrics();
        MissionPlanChanged?.Invoke();
        AppendLog(MavSeverity.Info, "DOSYA", $"WPL 110 görev dosyası içe aktarıldı ({MissionItems.Count} WP).");
    }

    public string ExportMissionJson()
    {
        RenumberMissionItems();
        var fence = FencePoints.Select((p, i) => new MissionItem(
            i, MavFrame.GlobalInt, MavCmd.NavFencePolygonVertexInclusion, FencePoints.Count, 0f, 0f, 0f, p.Latitude, p.Longitude, 0f, false)).ToList();
        return MissionFileCodec.ExportJson(new MissionDocument(MavMissionType.Mission, MissionItems.Select(x => x.ToMissionItem()).ToList(), fence));
    }

    public void ImportMissionJson(string json)
    {
        MissionDocument doc = MissionFileCodec.ImportJson(json);

        var nextMissionItems = new List<EditableMissionItem>(doc.Items.Count);
        for (int i = 0; i < doc.Items.Count; i++)
        {
            MissionItem item = doc.Items[i];
            ValidateImportedMissionItem(item, i);
            EditableMissionItem editable = EditableMissionItem.FromMissionItem(item);
            editable.Seq = i;
            nextMissionItems.Add(editable);
        }

        var nextFencePoints = new List<EditableFencePoint>(doc.FenceItems.Count);
        for (int i = 0; i < doc.FenceItems.Count; i++)
        {
            MissionItem f = doc.FenceItems[i];
            ValidateImportedMissionItem(f, i);
            nextFencePoints.Add(new EditableFencePoint(i, f.Latitude, f.Longitude));
        }

        using var undoBatch = BeginMissionUndoBatch();
        _suppressMetricsRecalc = true;
        try
        {
            ClearMissionItemsTracked();
            foreach (EditableMissionItem item in nextMissionItems)
            {
                AddMissionItemTracked(item);
            }

            SelectedMissionItem = MissionItems.FirstOrDefault();
            ClearFencePointsTracked();
            foreach (EditableFencePoint fp in nextFencePoints)
            {
                AddFencePointTracked(fp);
            }

            SelectedFencePoint = null;
        }
        finally
        {
            _suppressMetricsRecalc = false;
        }

        MarkMissionDirty();
        RecalculateMissionMetrics();
        EvaluateLocalGeofence(Snapshot);
        RefreshPreFlightChecks();
        OnPropertyChanged(nameof(GeofenceStatusSummary));
        MissionPlanChanged?.Invoke();
        FencePolygonChanged?.Invoke();
        AppendLog(MavSeverity.Info, "DOSYA", $"Tulpar JSON planı içe aktarıldı ({MissionItems.Count} WP, {FencePoints.Count} Fence).");
    }

    public void HandleMapWebMessage(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            using JsonDocument initialDoc = JsonDocument.Parse(json);
            JsonElement root = initialDoc.RootElement;
            JsonDocument? nestedDoc = null;
            if (root.ValueKind == JsonValueKind.String)
            {
                string? rawJson = root.GetString();
                if (string.IsNullOrWhiteSpace(rawJson))
                {
                    return;
                }

                nestedDoc = JsonDocument.Parse(rawJson);
                root = nestedDoc.RootElement;
            }

            try
            {
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out JsonElement typeEl))
                {
                    return;
                }

                switch (typeEl.GetString())
                {
                    case "missionAdd" when TryGetLatLon(root, out double mLat, out double mLon):
                        AddMissionWaypoint(mLat, mLon);
                        break;

                    case "missionMove" when root.TryGetProperty("seq", out JsonElement seqEl) &&
                                            seqEl.TryGetInt32(out int seq) &&
                                            TryGetLatLon(root, out double mmLat, out double mmLon):
                        MoveMissionWaypoint(seq, mmLat, mmLon);
                        break;

                    case "missionMovePreview" when root.TryGetProperty("seq", out JsonElement previewSeqEl) &&
                                                   previewSeqEl.TryGetInt32(out int previewSeq) &&
                                                   TryGetLatLon(root, out double mpLat, out double mpLon):
                        MoveMissionWaypointPreview(previewSeq, mpLat, mpLon);
                        break;

                    case "missionInsertAfter" when root.TryGetProperty("afterSeq", out JsonElement afterSeqEl) &&
                                                   afterSeqEl.TryGetInt32(out int afterSeq) &&
                                                   TryGetLatLon(root, out double iaLat, out double iaLon):
                        InsertMissionWaypointAfter(afterSeq, iaLat, iaLon);
                        break;

                    case "missionSelect" when root.TryGetProperty("seq", out JsonElement selectSeqEl) &&
                                              selectSeqEl.TryGetInt32(out int selectSeq):
                        EditableMissionItem? matchedWaypoint = MissionItems.FirstOrDefault(m => m.Seq == selectSeq);
                        if (matchedWaypoint is not null)
                        {
                            SelectedMissionItem = matchedWaypoint;
                            MissionWaypointInspectRequested?.Invoke(matchedWaypoint);
                        }
                        break;

                    case "fenceAdd" when TryGetLatLon(root, out double fLat, out double fLon):
                        AddFencePoint(fLat, fLon);
                        break;

                    case "fenceMove" when root.TryGetProperty("index", out JsonElement idxEl) &&
                                          idxEl.TryGetInt32(out int idx) &&
                                          TryGetLatLon(root, out double fmLat, out double fmLon):
                        MoveFencePoint(idx, fmLat, fmLon);
                        break;

                    case "rallyAdd" when TryGetLatLon(root, out double rLat, out double rLon):
                        AddRallyPoint(rLat, rLon);
                        break;

                    case "rallyMove" when root.TryGetProperty("index", out JsonElement rIdxEl) &&
                                          rIdxEl.TryGetInt32(out int rIdx) &&
                                          TryGetLatLon(root, out double rmLat, out double rmLon):
                        MoveRallyPoint(rIdx, rmLat, rmLon);
                        break;

                    case "guidedTarget" when TryGetLatLon(root, out double gLat, out double gLon):
                        RequestGuidedFlyHere(gLat, gLon);
                        break;

                    case "boundsSelected" when root.TryGetProperty("bounds", out JsonElement boundsEl) &&
                                               boundsEl.TryGetProperty("minLat", out JsonElement minLatEl) &&
                                               boundsEl.TryGetProperty("minLon", out JsonElement minLonEl) &&
                                               boundsEl.TryGetProperty("maxLat", out JsonElement maxLatEl) &&
                                               boundsEl.TryGetProperty("maxLon", out JsonElement maxLonEl):
                        SelectedOfflineBounds = new MapBounds(
                            minLatEl.GetDouble(), minLonEl.GetDouble(), maxLatEl.GetDouble(), maxLonEl.GetDouble());
                        OnPropertyChanged(nameof(SelectedOfflineBounds));
                        if (ActiveMapEditMode == MapEditMode.Bounds)
                        {
                            ActiveMapEditMode = MapEditMode.None;
                        }
                        AppendLog(MavSeverity.Info, "HARİTA", "Çevrimdışı indirme dikdörtgeni seçildi.");
                        break;
                }
            }
            finally
            {
                nestedDoc?.Dispose();
            }
        }
        catch (Exception ex)
        {
            AppendLog(MavSeverity.Warning, "HARİTA", $"Harita mesajı işlenemedi: {ex.Message}");
        }
    }

    public async Task RefreshParametersAsync()
    {
        if (!CanIssueVehicleCommand())
        {
            return;
        }

        byte targetSys = Snapshot.SystemId;
        byte targetComp = Snapshot.ComponentId == 0 ? (byte)MavComponent.Autopilot1 : Snapshot.ComponentId;

        _isDownloadingParameters = true;
        _uiDispatcher.InvokeOrBeginInvoke(() =>
        {
            _allParameterRows.Clear();
            FilteredParameters.Clear();
            ParameterProgressPercent = 0;
            ParameterStatusText = "PARAMETRELER OKUNUYOR...";
        });

        var progress = new Progress<ParameterTransferProgress>(report =>
        {
            int pct = report.TotalCount > 0
                ? Math.Clamp((int)Math.Round(report.ReceivedCount * 100.0 / report.TotalCount), 0, 100)
                : 0;
            _uiDispatcher.InvokeOrBeginInvoke(() =>
            {
                ParameterProgressPercent = pct;
                ParameterStatusText = report.TotalCount > 0
                    ? $"PARAMETRELER: {report.ReceivedCount}/{report.TotalCount}"
                    : "PARAMETRE LİSTESİ İSTENİYOR...";
            });
        });

        try
        {
            await ParameterProtocol.RequestAllParametersAsync(
                targetSys,
                targetComp,
                (bytes, ct) => SendRawPacketAsync(bytes, ct),
                progress,
                CancellationToken.None).ConfigureAwait(false);

            _uiDispatcher.InvokeOrBeginInvoke(() =>
            {
                SyncAllParametersFromProtocol();
                ParameterProgressPercent = 100;
                ParameterStatusText = $"PARAMETRELER HAZIR ({_allParameterRows.Count})";
                AppendLog(MavSeverity.Info, "PARAM", $"Araçtan {_allParameterRows.Count} parametre okundu.");
            });
        }
        catch (Exception ex)
        {
            _uiDispatcher.InvokeOrBeginInvoke(() =>
            {
                SyncAllParametersFromProtocol();
                ParameterStatusText = "PARAMETRE OKUMA HATASI";
                AppendLog(MavSeverity.Error, "PARAM", $"Parametreler okunamadı: {ex.Message}");
            });
        }
        finally
        {
            _isDownloadingParameters = false;
        }
    }

    public async Task ApplyParameterAsync(VehicleParameterRow? row)
    {
        if (row is null || !CanIssueVehicleCommand())
        {
            return;
        }

        if (!float.TryParse(row.EditValue?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedValue) ||
            !float.IsFinite(parsedValue))
        {
            _uiDispatcher.InvokeOrBeginInvoke(() =>
            {
                ParameterStatusText = $"GEÇERSİZ DEĞER ({row.Name})";
                AppendLog(MavSeverity.Warning, "PARAM", $"Geçersiz parametre değeri ({row.Name}): '{row.EditValue}'");
            });
            return;
        }

        byte targetSys = Snapshot.SystemId;
        byte targetComp = Snapshot.ComponentId == 0 ? (byte)MavComponent.Autopilot1 : Snapshot.ComponentId;

        try
        {
            VehicleParameter updated = await ParameterProtocol.SetParameterAsync(
                targetSys,
                targetComp,
                row.Name,
                parsedValue,
                row.ParamType,
                (bytes, ct) => SendRawPacketAsync(bytes, ct),
                CancellationToken.None).ConfigureAwait(false);

            _uiDispatcher.InvokeOrBeginInvoke(() =>
            {
                row.UpdateFrom(updated, markApplied: true);
                ParameterStatusText = $"{updated.Name} = {row.DisplayValue} GÜNCELLENDİ";
                AppendLog(MavSeverity.Info, "PARAM", $"Parametre yazıldı: {updated.Name} = {row.DisplayValue}");
            });
        }
        catch (Exception ex)
        {
            _uiDispatcher.InvokeOrBeginInvoke(() =>
            {
                ParameterStatusText = $"{row.Name} YAZILAMADI";
                AppendLog(MavSeverity.Error, "PARAM", $"Parametre yazılamadı ({row.Name}): {ex.Message}");
            });
        }
    }

    public async Task LoadReplayFileAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Volatile.Read(ref _disposed))
        {
            return;
        }

        List<TlogEntry> entries = await Task.Run(() => TlogReplayReader.ReadFile(path).ToList(), ct).ConfigureAwait(false);
        if (entries.Count == 0)
        {
            _uiDispatcher.InvokeOrBeginInvoke(() =>
                AppendLog(MavSeverity.Warning, "TLOG", $"Dosyada geçerli MAVLink kaydı bulunamadı: {Path.GetFileName(path)}"));
            return;
        }

        if (IsTransportConnected || _transport is not null)
        {
            await DisconnectAsync().ConfigureAwait(false);
        }

        await _replayLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopReplayLoopCoreAsync().ConfigureAwait(false);
            long generation = Interlocked.Increment(ref _connectionGeneration);
            Interlocked.Exchange(ref _pendingTelemetryUpdate, null);

            _replayEntries = entries;
            _replayIndex = 0;

            _isSeekingReplay = true;
            try
            {
                Session.ResetOnDisconnect();
                Session.ProcessIncomingBytes(entries[0].RawPacketBytes.Span);
                _replayIndex = entries.Count > 1 ? 1 : entries.Count;
            }
            finally
            {
                _isSeekingReplay = false;
            }

            TelemetrySnapshot initialSnapshot = Session.Current;
            IVehicleProfile? initialProfile = Session.ActiveProfile;

            _uiDispatcher.InvokeOrBeginInvoke(() =>
            {
                ReplayFileName = Path.GetFileName(path);
                IsReplayMode = true;
                IsReplayPlaying = false;
                ReplayProgressPercent = 0.0;
                ReplayTimeDisplay = FormatReplayTimeDisplay(0);
                ApplyUiTelemetry(initialSnapshot, initialProfile, generation, updateHud: true, updatePanel: true);
                ToggleReplayPlayPauseCommand.RaiseCanExecuteChanged();
                StopReplayCommand.RaiseCanExecuteChanged();
                AppendLog(MavSeverity.Info, "TLOG", $"Kayıt yüklendi: {ReplayFileName} ({entries.Count} paket)");
            });
        }
        finally
        {
            _replayLock.Release();
        }
    }

    public async Task SeekReplayAsync(double percent)
    {
        double clampedPercent = Math.Clamp(percent, 0.0, 100.0);

        await _replayLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_isReplayMode || _replayEntries.Count == 0)
            {
                return;
            }

            bool wasPlaying = _isReplayPlaying;
            await StopReplayLoopCoreAsync().ConfigureAwait(false);

            int targetIndex = _replayEntries.Count <= 1
                ? 0
                : (int)Math.Round((clampedPercent / 100.0) * (_replayEntries.Count - 1));
            targetIndex = Math.Clamp(targetIndex, 0, _replayEntries.Count - 1);

            long generation = Volatile.Read(ref _connectionGeneration);
            Interlocked.Exchange(ref _pendingTelemetryUpdate, null);

            _isSeekingReplay = true;
            try
            {
                Session.ResetOnDisconnect();
                for (int i = 0; i <= targetIndex; i++)
                {
                    Session.ProcessIncomingBytes(_replayEntries[i].RawPacketBytes.Span);
                }
            }
            finally
            {
                _isSeekingReplay = false;
            }

            _replayIndex = Math.Min(targetIndex + 1, _replayEntries.Count);
            TelemetrySnapshot targetSnapshot = Session.Current;
            IVehicleProfile? targetProfile = Session.ActiveProfile;

            _uiDispatcher.InvokeOrBeginInvoke(() =>
            {
                ReplayProgressPercent = clampedPercent;
                ReplayTimeDisplay = FormatReplayTimeDisplay(targetIndex);
                ApplyUiTelemetry(targetSnapshot, targetProfile, generation, updateHud: true, updatePanel: true);
            });

            if (wasPlaying && _replayIndex < _replayEntries.Count)
            {
                StartReplayLoopLocked();
            }
            else if (_replayIndex >= _replayEntries.Count)
            {
                _uiDispatcher.InvokeOrBeginInvoke(() => IsReplayPlaying = false);
            }
        }
        finally
        {
            _replayLock.Release();
        }
    }

    public async Task ToggleReplayPlayPauseAsync()
    {
        await _replayLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_isReplayMode || _replayEntries.Count == 0)
            {
                return;
            }

            if (_isReplayPlaying)
            {
                await StopReplayLoopCoreAsync().ConfigureAwait(false);
                _uiDispatcher.InvokeOrBeginInvoke(() => IsReplayPlaying = false);
                return;
            }

            if (_replayIndex >= _replayEntries.Count)
            {
                _isSeekingReplay = true;
                try
                {
                    Session.ResetOnDisconnect();
                    Session.ProcessIncomingBytes(_replayEntries[0].RawPacketBytes.Span);
                    _replayIndex = _replayEntries.Count > 1 ? 1 : _replayEntries.Count;
                }
                finally
                {
                    _isSeekingReplay = false;
                }

                long generation = Volatile.Read(ref _connectionGeneration);
                TelemetrySnapshot snap = Session.Current;
                IVehicleProfile? prof = Session.ActiveProfile;
                _uiDispatcher.InvokeOrBeginInvoke(() =>
                {
                    ReplayProgressPercent = 0.0;
                    ReplayTimeDisplay = FormatReplayTimeDisplay(0);
                    ApplyUiTelemetry(snap, prof, generation, updateHud: true, updatePanel: true);
                });
            }

            StartReplayLoopLocked();
        }
        finally
        {
            _replayLock.Release();
        }
    }

    public void CycleReplaySpeed()
    {
        ReplaySpeedMultiplier = ReplaySpeedMultiplier switch
        {
            < 1.5 => 2.0,
            < 3.0 => 4.0,
            _ => 1.0
        };
    }

    public async Task StopReplayAsync()
    {
        await _replayLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopReplayCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _replayLock.Release();
        }
    }

    public void AppendSystemLog(MavSeverity severity, string source, string message) =>
        _uiDispatcher.InvokeOrBeginInvoke(() => AppendLog(severity, source, message));

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposedFlag, 1) != 0)
        {
            return;
        }

        Volatile.Write(ref _disposed, true);
        Session.Bus.RawFrameReceived -= OnRawFrameReceived;
        Session.Bus.SnapshotUpdated -= OnSessionSnapshotUpdated;
        Session.Bus.StatusTextReceived -= OnStatusTextReceived;
        Session.Bus.CommandAckReceived -= OnCommandAckReceived;
        ParameterProtocol.ParameterUpdated -= OnParameterUpdated;
        MissionItems.CollectionChanged -= OnMissionItemsCollectionChanged;

        await StopReplayAsync().ConfigureAwait(false);
        await DisconnectAsync().ConfigureAwait(false);
        _udpForwarder?.Dispose();
        _udpForwarder = null;
    }

    private ITransport CreateConfiguredTransport() =>
        SelectedConnectionKind switch
        {
            ConnectionKind.Serial => string.IsNullOrWhiteSpace(SelectedSerialPort)
                ? throw new InvalidOperationException("Geçerli bir seri port (COM) seçilmedi.")
                : _transportFactory.CreateSerial(SelectedSerialPort, SelectedBaudRate),
            ConnectionKind.Udp => _transportFactory.CreateUdp(UdpListenPort),
            ConnectionKind.Tcp => _transportFactory.CreateTcp(TcpHost, TcpPort),
            _ => throw new InvalidOperationException($"Desteklenmeyen bağlantı türü: {SelectedConnectionKind}")
        };

    private void OnTransportDataReceived(ReadOnlyMemory<byte> bytes)
    {
        _udpForwarder?.ForwardRawBytes(bytes.Span);
        Session.ProcessIncomingBytes(bytes.Span);
    }

    private void OnTransportDisconnected(string reason)
    {
        if (Volatile.Read(ref _disposed))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            if (Volatile.Read(ref _disposed))
            {
                return;
            }

            await DisconnectAsync().ConfigureAwait(false);
            if (!Volatile.Read(ref _disposed))
            {
                _uiDispatcher.InvokeOrBeginInvoke(() =>
                    AppendLog(MavSeverity.Warning, "BAĞLANTI", $"Bağlantı koptu: {reason}"));
            }
        });
    }

    private void OnRawFrameReceived(MavlinkFrame frame)
    {
        if (!frame.RawPacketBytes.IsEmpty)
        {
            RecordRawFrameToRecorder(frame.RawPacketBytes.Span);
        }

        MissionProtocol.HandleIncomingFrame(frame);
        ParameterProtocol.HandleIncomingFrame(frame);
    }

    private void OnSessionSnapshotUpdated(TelemetrySnapshot snapshot)
    {
        if (_isSeekingReplay)
        {
            return;
        }

        RecordSnapshotToRecorder(snapshot);

        IVehicleProfile? profile = Session.ActiveProfile;
        long generation = Volatile.Read(ref _connectionGeneration);

        if (IsTransportConnected &&
            !IsReplayMode &&
            snapshot.IsConnected &&
            snapshot.SystemId != 0 &&
            (_dataStreamRequestedSysId != snapshot.SystemId || _dataStreamRequestedVehicleType != snapshot.VehicleType))
        {
            _dataStreamRequestedSysId = snapshot.SystemId;
            _dataStreamRequestedVehicleType = snapshot.VehicleType;
            byte sys = snapshot.SystemId;
            byte comp = snapshot.ComponentId == 0 ? (byte)MavComponent.Autopilot1 : snapshot.ComponentId;
            _ = SendMessageAsync(new RequestDataStreamMessage(sys, comp, (byte)MavDataStream.All, 4, 1), CancellationToken.None);
        }

        bool discreteChanged =
            snapshot.IsConnected != _snapshot.IsConnected ||
            snapshot.LinkLost != _snapshot.LinkLost ||
            snapshot.IsArmed != _snapshot.IsArmed ||
            snapshot.SystemId != _snapshot.SystemId ||
            snapshot.VehicleCategory != _snapshot.VehicleCategory ||
            snapshot.Autopilot != _snapshot.Autopilot ||
            !string.Equals(snapshot.ModeName, _snapshot.ModeName, StringComparison.Ordinal);

        long nowTicks = _timeProvider.GetTimestamp();
        bool hudDue = discreteChanged ||
                      _options.HudThrottleInterval <= TimeSpan.Zero ||
                      _timeProvider.GetElapsedTime(_lastHudPushTick, nowTicks) >= _options.HudThrottleInterval;
        bool panelDue = discreteChanged ||
                        _options.PanelThrottleInterval <= TimeSpan.Zero ||
                        _timeProvider.GetElapsedTime(_lastPanelPushTick, nowTicks) >= _options.PanelThrottleInterval;

        if (!hudDue && !panelDue)
        {
            Interlocked.Exchange(
                ref _pendingTelemetryUpdate,
                new PendingTelemetryUpdate(snapshot, profile, generation, UpdateHud: true, UpdatePanel: true));
            return;
        }

        if (hudDue)
        {
            _lastHudPushTick = nowTicks;
        }

        if (panelDue)
        {
            _lastPanelPushTick = nowTicks;
            Interlocked.Exchange(ref _pendingTelemetryUpdate, null);
        }
        else
        {
            Interlocked.Exchange(
                ref _pendingTelemetryUpdate,
                new PendingTelemetryUpdate(snapshot, profile, generation, UpdateHud: false, UpdatePanel: true));
        }

        _uiDispatcher.InvokeOrBeginInvoke(() => ApplyUiTelemetry(snapshot, profile, generation, updateHud: hudDue, updatePanel: panelDue));
    }

    private void ApplyUiTelemetry(TelemetrySnapshot snapshot, IVehicleProfile? profile, long generation, bool updateHud, bool updatePanel)
    {
        if (Volatile.Read(ref _disposed) || generation != Volatile.Read(ref _connectionGeneration))
        {
            return;
        }

        if (!ReferenceEquals(_activeProfile, profile))
        {
            ActiveProfile = profile;
        }

        if (updateHud)
        {
            HudSnapshot = snapshot;
        }

        if (updatePanel)
        {
            bool homeChanged =
                snapshot.HasHome != _snapshot.HasHome ||
                (snapshot.HasHome &&
                 (Math.Abs(snapshot.HomeLatitude - _snapshot.HomeLatitude) > 1e-7 ||
                  Math.Abs(snapshot.HomeLongitude - _snapshot.HomeLongitude) > 1e-7));

            Snapshot = snapshot;
            SyncProfileSelectorsIfNeeded(snapshot, profile);
            EvaluateLocalGeofence(snapshot);
            if (homeChanged)
            {
                RecalculateMissionMetrics();
            }

            RefreshPreFlightChecks();
            RefreshDataFreshness();
            RefreshSystemHealth();
            VehicleMapStateUpdated?.Invoke(snapshot);

            var currentCmdState = (IsTransportConnected, snapshot.SystemId, snapshot.IsArmed, HasPendingSafetyAction, SelectedMode);
            if (currentCmdState != _lastCommandRaiseState)
            {
                _lastCommandRaiseState = currentCmdState;
                RaiseCommandStates();
            }
        }
    }

    private void SyncProfileSelectorsIfNeeded(TelemetrySnapshot snapshot, IVehicleProfile? profile)
    {
        if (!snapshot.IsConnected || profile is null)
        {
            return;
        }

        var key = (profile.Category, snapshot.Autopilot, profile.GetType());
        if (key != _lastProfileKey)
        {
            _lastProfileKey = key;
            _lastSyncedVehicleMode = null;

            AvailableModes.Clear();
            foreach (FlightModeOption mode in profile.GetFlightModes(snapshot.Autopilot))
            {
                AvailableModes.Add(mode);
            }

            AvailableMissionCommands.Clear();
            foreach (MavCmd cmd in profile.SupportedMissionCommands)
            {
                AvailableMissionCommands.Add(cmd);
            }

            if (!AvailableMissionCommands.Contains(SelectedNewMissionCommand))
            {
                SelectedNewMissionCommand = AvailableMissionCommands.Contains(MavCmd.NavWaypoint)
                    ? MavCmd.NavWaypoint
                    : AvailableMissionCommands.FirstOrDefault();
            }

            DefaultWaypointAltitudeMeters = profile.SurfaceOnlyMode
                ? 0f
                : DefaultWaypointAltitudeMeters <= 0f ? 50f : DefaultWaypointAltitudeMeters;
        }

        // Only auto-sync SelectedMode when the vehicle's reported mode actually changes (or on first sync)
        // so live telemetry ticks never overwrite the operator's dropdown choice before clicking MOD.
        if (SelectedMode is null || !string.Equals(_lastSyncedVehicleMode, snapshot.ModeName, StringComparison.Ordinal))
        {
            _lastSyncedVehicleMode = snapshot.ModeName;
            FlightModeOption? matchingMode = AvailableModes.FirstOrDefault(
                m => string.Equals(m.Name, snapshot.ModeName, StringComparison.OrdinalIgnoreCase));
            if (matchingMode is not null)
            {
                SelectedMode = matchingMode;
            }
        }
    }

    private void OnStatusTextReceived(StatusTextMessage status)
    {
        if (_isSeekingReplay)
        {
            return;
        }

        _uiDispatcher.InvokeOrBeginInvoke(() => AppendLog(status.Severity, "OTOPİLOT", status.Text));
    }

    private void OnCommandAckReceived(MavlinkFrame frame)
    {
        if (_isSeekingReplay)
        {
            return;
        }

        CommandAckMessage ack = CommandAckMessage.Decode(frame);
        MavSeverity severity = ack.Result == MavResult.Accepted ? MavSeverity.Info : MavSeverity.Warning;
        _uiDispatcher.InvokeOrBeginInvoke(() => AppendLog(severity, "ACK", $"{ack.Command} -> {ack.Result}"));
    }

    private async Task RunBackgroundMonitorAsync(ITransport transport, long generation, CancellationToken ct)
    {
        TimeSpan interval = _options.HeartbeatCheckInterval > TimeSpan.Zero
            ? _options.HeartbeatCheckInterval
            : TimeSpan.FromMilliseconds(500);

        using var timer = new PeriodicTimer(interval, _timeProvider);
        long lastGcsHeartbeatTick = 0;
        bool sentFirstGcsHeartbeat = false;

        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (Volatile.Read(ref _connectionGeneration) != generation || Volatile.Read(ref _disposed))
                {
                    break;
                }

                Session.CheckHeartbeatTimeout(_timeProvider.GetUtcNow());

                PendingTelemetryUpdate? pending = Interlocked.Exchange(ref _pendingTelemetryUpdate, null);
                if (pending is not null && pending.Generation == Volatile.Read(ref _connectionGeneration))
                {
                    _uiDispatcher.InvokeOrBeginInvoke(() =>
                    {
                        ApplyUiTelemetry(pending.Snapshot, pending.Profile, pending.Generation, pending.UpdateHud, pending.UpdatePanel);
                        if (!pending.UpdatePanel &&
                            !Volatile.Read(ref _disposed) &&
                            pending.Generation == Volatile.Read(ref _connectionGeneration))
                        {
                            RefreshDataFreshness();
                            RefreshSystemHealth();
                        }
                    });
                }
                else
                {
                    _uiDispatcher.InvokeOrBeginInvoke(() =>
                    {
                        if (Volatile.Read(ref _disposed) || generation != Volatile.Read(ref _connectionGeneration))
                        {
                            return;
                        }

                        RefreshDataFreshness();
                        RefreshSystemHealth();
                    });
                }

                long nowTicks = _timeProvider.GetTimestamp();
                if (!sentFirstGcsHeartbeat || _timeProvider.GetElapsedTime(lastGcsHeartbeatTick, nowTicks) >= TimeSpan.FromSeconds(1))
                {
                    sentFirstGcsHeartbeat = true;
                    lastGcsHeartbeatTick = nowTicks;
                    if (transport.IsConnected)
                    {
                        var gcsHb = new HeartbeatMessage(MavType.Gcs, MavAutopilot.Invalid, MavModeFlag.None, 0u, MavState.Active);
                        byte[] packet = MavlinkEncoder.EncodeV2(GcsSystemId, GcsComponentId, NextTxSequence(), gcsHb);
                        try
                        {
                            await transport.SendAsync(packet, ct).ConfigureAwait(false);
                        }
                        catch
                        {
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void RecordRawFrameToRecorder(ReadOnlySpan<byte> rawPacket)
    {
        if (rawPacket.IsEmpty || IsReplayMode)
        {
            return;
        }

        lock (_recorderSync)
        {
            if (_recorder is { } recorder)
            {
                try
                {
                    recorder.RecordRawFrame(_timeProvider.GetUtcNow(), rawPacket);
                }
                catch
                {
                }
            }
        }
    }

    private void RecordSnapshotToRecorder(TelemetrySnapshot snapshot)
    {
        if (IsReplayMode)
        {
            return;
        }

        lock (_recorderSync)
        {
            if (_recorder is { } recorder)
            {
                try
                {
                    recorder.RecordSnapshot(snapshot);
                }
                catch
                {
                }
            }
        }
    }

    private void EvaluateLocalGeofence(TelemetrySnapshot snapshot)
    {
        if (FencePoints.Count < 3 ||
            !snapshot.IsConnected ||
            snapshot.SystemId == 0 ||
            !TelemetryDisplayFormatter.IsValidGeoCoordinate(snapshot.Latitude, snapshot.Longitude))
        {
            IsGeofenceBreached = false;
            GeofenceAlertMessage = string.Empty;
            return;
        }

        var polygon = new GeoPoint[FencePoints.Count];
        for (int i = 0; i < FencePoints.Count; i++)
        {
            polygon[i] = new GeoPoint(FencePoints[i].Latitude, FencePoints[i].Longitude);
        }

        if (!GeofenceEvaluator.IsPointInsidePolygon(snapshot.Latitude, snapshot.Longitude, polygon))
        {
            bool wasBreached = IsGeofenceBreached;
            IsGeofenceBreached = true;
            GeofenceAlertMessage = string.Create(
                CultureInfo.InvariantCulture,
                $"GEOFENCE İHLALİ: ARAÇ DAHİL ETME BÖLGESİ DIŞINDA ({snapshot.Latitude:0.000000}, {snapshot.Longitude:0.000000})");

            if (!wasBreached)
            {
                AppendLog(MavSeverity.Critical, "GEOFENCE", GeofenceAlertMessage);
            }
        }
        else
        {
            IsGeofenceBreached = false;
            GeofenceAlertMessage = string.Empty;
        }
    }

    private Task SendMessageAsync(IMavlinkMessage message, CancellationToken ct) =>
        SendRawPacketAsync(MavlinkEncoder.EncodeV2(GcsSystemId, GcsComponentId, NextTxSequence(), message), ct);

    private async Task SendRawPacketAsync(byte[] packet, CancellationToken ct)
    {
        ITransport? transport;
        lock (_sessionSync)
        {
            transport = _transport;
        }

        if (transport is null || !transport.IsConnected)
        {
            return;
        }

        await transport.SendAsync(packet, ct).ConfigureAwait(false);
    }

    private byte NextTxSequence() => unchecked((byte)(Interlocked.Increment(ref _txSequence) - 1));

    private bool CanIssueVehicleCommand() => IsTransportConnected && Snapshot.SystemId != 0;

    private void RaiseCommandStates()
    {
        ConnectOrDisconnectCommand.RaiseCanExecuteChanged();
        QuickConnectSitlCommand.RaiseCanExecuteChanged();
        RequestArmOrDisarmCommand.RaiseCanExecuteChanged();
        RequestRtlCommand.RaiseCanExecuteChanged();
        RequestHoldCommand.RaiseCanExecuteChanged();
        RequestTakeoffCommand.RaiseCanExecuteChanged();
        RequestLandCommand.RaiseCanExecuteChanged();
        RequestStartMissionCommand.RaiseCanExecuteChanged();
        ConfirmSafetyActionCommand.RaiseCanExecuteChanged();
        CancelSafetyActionCommand.RaiseCanExecuteChanged();
        ApplySelectedModeCommand.RaiseCanExecuteChanged();
        TriggerServoCommand.RaiseCanExecuteChanged();
        SetHomeCurrentCommand.RaiseCanExecuteChanged();
        ApplyCruiseSpeedCommand.RaiseCanExecuteChanged();
        UploadMissionCommand.RaiseCanExecuteChanged();
        DownloadMissionCommand.RaiseCanExecuteChanged();
        ClearMissionCommand.RaiseCanExecuteChanged();
        UploadFenceCommand.RaiseCanExecuteChanged();
        DownloadFenceCommand.RaiseCanExecuteChanged();
        ClearFenceCommand.RaiseCanExecuteChanged();
        UploadRallyCommand.RaiseCanExecuteChanged();
        DownloadRallyCommand.RaiseCanExecuteChanged();
        ClearRallyCommand.RaiseCanExecuteChanged();
        RefreshParametersCommand.RaiseCanExecuteChanged();
        ApplyParameterCommand.RaiseCanExecuteChanged();
        ToggleReplayPlayPauseCommand.RaiseCanExecuteChanged();
        StopReplayCommand.RaiseCanExecuteChanged();
    }

    private void ResetMissionCommandsToDefault()
    {
        AvailableMissionCommands.Clear();
        foreach (MavCmd cmd in DefaultCommands)
        {
            AvailableMissionCommands.Add(cmd);
        }

        SelectedNewMissionCommand = MavCmd.NavWaypoint;
    }

    private void RenumberMissionItems()
    {
        for (int i = 0; i < MissionItems.Count; i++)
        {
            MissionItems[i].Seq = i;
        }
    }

    private void RenumberFencePoints()
    {
        for (int i = 0; i < FencePoints.Count; i++)
        {
            FencePoints[i].Index = i;
        }
    }

    private void AddMissionItemTracked(EditableMissionItem item)
    {
        item.PropertyChanged += OnEditableMissionItemPropertyChanged;
        MissionItems.Add(item);
    }

    private void ClearMissionItemsTracked()
    {
        if (MissionItems.Count > 0)
        {
            RecordMissionUndoSnapshot();
        }

        foreach (EditableMissionItem existing in MissionItems)
        {
            existing.PropertyChanged -= OnEditableMissionItemPropertyChanged;
        }

        MissionItems.Clear();
    }

    private void AddFencePointTracked(EditableFencePoint point)
    {
        point.PropertyChanged += OnEditableFencePointPropertyChanged;
        FencePoints.Add(point);
    }

    private void ClearFencePointsTracked()
    {
        foreach (EditableFencePoint existing in FencePoints)
        {
            existing.PropertyChanged -= OnEditableFencePointPropertyChanged;
        }

        FencePoints.Clear();
    }

    private void OnMissionItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_suppressMetricsRecalc)
        {
            RecalculateMissionMetrics();
        }

        OnPropertyChanged(nameof(MissionSyncStateText));
    }

    private void OnEditableMissionItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EditableMissionItem.Latitude)
            or nameof(EditableMissionItem.Longitude)
            or nameof(EditableMissionItem.Altitude)
            or nameof(EditableMissionItem.Command)
            or nameof(EditableMissionItem.Frame)
            or nameof(EditableMissionItem.Param1)
            or nameof(EditableMissionItem.Param2)
            or nameof(EditableMissionItem.Param3)
            or nameof(EditableMissionItem.Param4)
            or nameof(EditableMissionItem.Autocontinue))
        {
            if (!_suppressMetricsRecalc)
            {
                RecalculateMissionMetrics();
            }

            if (!_suppressMissionPlanChanged)
            {
                MarkMissionDirty();
                MissionPlanChanged?.Invoke();
            }
        }
    }

    private void OnEditableFencePointPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EditableFencePoint.Latitude) or nameof(EditableFencePoint.Longitude))
        {
            EvaluateLocalGeofence(Snapshot);
            RefreshPreFlightChecks();
            FencePolygonChanged?.Invoke();
        }
    }

    private void RecalculateMissionMetrics()
    {
        if (MissionItems.Count == 0)
        {
            CurrentFlightMetrics = new FlightMetricsSummary(0.0, TimeSpan.Zero, Array.Empty<MissionLegMetric>());
            return;
        }

        var items = new List<MissionItem>(MissionItems.Count);
        for (int i = 0; i < MissionItems.Count; i++)
        {
            EditableMissionItem m = MissionItems[i];
            double lat = double.IsFinite(m.Latitude) && m.Latitude is >= -90.0 and <= 90.0 ? m.Latitude : 0.0;
            double lon = double.IsFinite(m.Longitude) && m.Longitude is >= -180.0 and <= 180.0 ? m.Longitude : 0.0;
            float alt = float.IsFinite(m.Altitude) ? m.Altitude : 0f;
            items.Add(new MissionItem(
                m.Seq,
                m.Frame,
                m.Command,
                m.Param1,
                m.Param2,
                m.Param3,
                m.Param4,
                lat,
                lon,
                alt,
                m.Autocontinue));
        }

        (double Latitude, double Longitude, double AltitudeMeters)? homePos =
            Snapshot.HasHome && TelemetryDisplayFormatter.IsValidGeoCoordinate(Snapshot.HomeLatitude, Snapshot.HomeLongitude)
                ? (Snapshot.HomeLatitude, Snapshot.HomeLongitude, Snapshot.HomeAltitudeMeters)
                : null;

        FlightMetricsSummary summary = FlightMetricsEstimator.Estimate(items, _cruiseSpeedMps, homePos);
        for (int i = 0; i < MissionItems.Count && i < summary.Legs.Count; i++)
        {
            MissionLegMetric leg = summary.Legs[i];
            EditableMissionItem target = MissionItems[i];
            target.LegDistanceMeters = leg.LegDistanceMeters;
            target.LegBearingDeg = leg.LegDistanceMeters > 0.0 ? leg.LegBearingDeg : null;
            target.CumulativeDistanceMeters = leg.CumulativeDistanceMeters;
        }

        CurrentFlightMetrics = summary;
    }

    private void RefreshPreFlightChecks()
    {
        TelemetrySnapshot snap = Snapshot;
        bool linkConnected = (IsTransportConnected || IsReplayMode) && snap.IsConnected && snap.SystemId != 0 && !snap.LinkLost;
        string linkDetail = !IsTransportConnected && !IsReplayMode
            ? "Bağlantı yok"
            : snap.LinkLost
                ? "Heartbeat zaman aşımı"
                : !snap.IsConnected || snap.SystemId == 0
                    ? "Heartbeat bekleniyor"
                    : $"SYS #{snap.SystemId} çevrimiçi";

        bool gpsOk = snap.IsConnected && snap.GpsFixType >= GpsFixType.Fix3D && snap.SatellitesVisible >= 6;
        string gpsDetail = snap.IsConnected
            ? $"{TelemetryDisplayFormatter.FormatGpsFix(snap.GpsFixType)} · {snap.SatellitesVisible} uydu"
            : "GPS verisi yok";
        string gpsSeverity = gpsOk ? "HAZIR" : (snap.IsConnected && snap.GpsFixType >= GpsFixType.Fix2D ? "UYARI" : "KRİTİK");

        bool batteryOk = snap.IsConnected &&
                         (snap.BatteryRemainingPercent > 20 ||
                          (snap.BatteryRemainingPercent < 0 && snap.BatteryVoltageVolts > 0.5));
        string batteryDetail = snap.IsConnected
            ? TelemetryDisplayFormatter.FormatBattery(snap)
            : "Batarya verisi yok";
        string batterySeverity = batteryOk
            ? "HAZIR"
            : (snap.BatteryRemainingPercent is > 10 and <= 20 ? "UYARI" : "KRİTİK");

        bool homeOk = snap.IsConnected &&
                      snap.HasHome &&
                      TelemetryDisplayFormatter.IsValidGeoCoordinate(snap.HomeLatitude, snap.HomeLongitude);
        string homeDetail = homeOk
            ? string.Create(CultureInfo.InvariantCulture, $"{snap.HomeLatitude:0.00000}, {snap.HomeLongitude:0.00000}")
            : "Home noktası bekleniyor";

        EkfStatusFlags ekfFlags = (EkfStatusFlags)snap.EkfFlags;
        bool hasEkf = snap.EkfFlags != 0;
        bool hasSensorHealth = snap.SensorHealthBits != 0;
        bool ekfHealthy = hasEkf
            ? !ekfFlags.HasFlag(EkfStatusFlags.Uninitialized) &&
              ekfFlags.HasFlag(EkfStatusFlags.Attitude) &&
              (ekfFlags.HasFlag(EkfStatusFlags.PosHorizRel) || ekfFlags.HasFlag(EkfStatusFlags.PosHorizAbs))
            : hasSensorHealth;
        bool fenceBlocking = FencePoints.Count is > 0 and < 3 || IsGeofenceBreached;
        bool sensorOk = snap.IsConnected && ekfHealthy && !fenceBlocking;
        string sensorDetail = !snap.IsConnected
            ? "Sensör verisi yok"
            : IsGeofenceBreached
                ? "Geofence ihlali aktif!"
                : FencePoints.Count is > 0 and < 3
                    ? $"Geofence eksik ({FencePoints.Count}/3)"
                    : !hasEkf && !hasSensorHealth
                        ? "EKF / sensör raporu bekleniyor"
                        : ekfHealthy
                            ? "EKF & sensörler sağlıklı"
                            : "EKF konum kilidi yok";

        var next = new List<PreFlightCheckItem>(5)
        {
            new("BAĞLANTI", linkDetail, linkConnected, linkConnected ? "HAZIR" : "KRİTİK"),
            new("GPS 3D FIX", gpsDetail, gpsOk, gpsSeverity),
            new("BATARYA", batteryDetail, batteryOk, batterySeverity),
            new("HOME NOKTASI", homeDetail, homeOk, homeOk ? "HAZIR" : "UYARI"),
            new("EKF / SENSÖR SAĞLIĞI", sensorDetail, sensorOk, sensorOk ? "HAZIR" : (IsGeofenceBreached ? "KRİTİK" : "UYARI"))
        };

        for (int i = 0; i < next.Count; i++)
        {
            if (i < PreFlightChecks.Count)
            {
                if (!Equals(PreFlightChecks[i], next[i]))
                {
                    PreFlightChecks[i] = next[i];
                }
            }
            else
            {
                PreFlightChecks.Add(next[i]);
            }
        }

        while (PreFlightChecks.Count > next.Count)
        {
            PreFlightChecks.RemoveAt(PreFlightChecks.Count - 1);
        }

        int passedCount = next.Count(c => c.IsPassed);
        bool allReady = passedCount == next.Count;
        IsPreFlightReady = allReady;
        PreFlightSummaryText = allReady
            ? $"UÇUŞA HAZIR ({passedCount}/{next.Count})"
            : $"HAZIR DEĞİL ({passedCount}/{next.Count})";
    }

    private void OnParameterUpdated(VehicleParameter param)
    {
        if (_isDownloadingParameters || Volatile.Read(ref _disposed))
        {
            return;
        }

        _uiDispatcher.InvokeOrBeginInvoke(() =>
        {
            if (Volatile.Read(ref _disposed))
            {
                return;
            }

            if (_allParameterRows.TryGetValue(param.Name, out VehicleParameterRow? existing))
            {
                existing.UpdateFrom(param);
            }
            else
            {
                var row = new VehicleParameterRow(param);
                _allParameterRows[param.Name] = row;
                if (MatchesParameterSearch(row))
                {
                    InsertSortedFilteredParameter(row);
                }
            }

            int total = Math.Max(ParameterProtocol.TotalParameterCount, _allParameterRows.Count);
            if (total > 0)
            {
                ParameterProgressPercent = Math.Clamp((int)Math.Round(_allParameterRows.Count * 100.0 / total), 0, 100);
                ParameterStatusText = $"PARAMETRELER: {_allParameterRows.Count}/{total}";
            }
        });
    }

    private void SyncAllParametersFromProtocol()
    {
        IReadOnlyList<VehicleParameter> list = ParameterProtocol.ParameterList;
        _allParameterRows.Clear();
        foreach (VehicleParameter param in list)
        {
            _allParameterRows[param.Name] = new VehicleParameterRow(param);
        }

        RebuildFilteredParameters();
    }

    private void RebuildFilteredParameters()
    {
        FilteredParameters.Clear();
        IEnumerable<VehicleParameterRow> ordered = _allParameterRows.Values
            .Where(MatchesParameterSearch)
            .OrderBy(r => r.Index)
            .ThenBy(r => r.Name, StringComparer.Ordinal);

        foreach (VehicleParameterRow row in ordered)
        {
            FilteredParameters.Add(row);
        }
    }

    private bool MatchesParameterSearch(VehicleParameterRow row)
    {
        if (string.IsNullOrWhiteSpace(_parameterSearchText))
        {
            return true;
        }

        return row.Name.Contains(_parameterSearchText.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private void InsertSortedFilteredParameter(VehicleParameterRow row)
    {
        int insertIdx = FilteredParameters.Count;
        for (int i = 0; i < FilteredParameters.Count; i++)
        {
            VehicleParameterRow current = FilteredParameters[i];
            int cmp = row.Index.CompareTo(current.Index);
            if (cmp == 0)
            {
                cmp = string.Compare(row.Name, current.Name, StringComparison.Ordinal);
            }

            if (cmp < 0)
            {
                insertIdx = i;
                break;
            }
        }

        FilteredParameters.Insert(insertIdx, row);
    }

    private void StartReplayLoopLocked()
    {
        var cts = new CancellationTokenSource();
        long replayGen = Interlocked.Increment(ref _replayGeneration);
        _replayCts = cts;
        _uiDispatcher.InvokeOrBeginInvoke(() => IsReplayPlaying = true);
        _replayLoopTask = Task.Run(() => RunReplayLoopAsync(replayGen, cts.Token));
    }

    private async Task StopReplayLoopCoreAsync()
    {
        CancellationTokenSource? cts = _replayCts;
        Task? loopTask = _replayLoopTask;
        _replayCts = null;
        _replayLoopTask = null;
        Interlocked.Increment(ref _replayGeneration);

        if (cts is not null)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        if (loopTask is not null)
        {
            try
            {
                await loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        cts?.Dispose();
    }

    private async Task StopReplayCoreAsync()
    {
        await StopReplayLoopCoreAsync().ConfigureAwait(false);
        bool wasReplay = _isReplayMode;
        _replayEntries = [];
        _replayIndex = 0;
        long generation = Interlocked.Increment(ref _connectionGeneration);
        Interlocked.Exchange(ref _pendingTelemetryUpdate, null);

        if (!IsTransportConnected)
        {
            Session.ResetOnDisconnect();
        }

        if (!wasReplay && Volatile.Read(ref _disposed))
        {
            return;
        }

        _uiDispatcher.InvokeOrBeginInvoke(() =>
        {
            if (Volatile.Read(ref _disposed))
            {
                return;
            }

            IsReplayMode = false;
            IsReplayPlaying = false;
            ReplayProgressPercent = 0.0;
            ReplayTimeDisplay = "00:00 / 00:00";
            ReplayFileName = string.Empty;

            if (!IsTransportConnected)
            {
                TelemetrySnapshot empty = Session.Current;
                ApplyUiTelemetry(empty, null, generation, updateHud: true, updatePanel: true);
            }

            ToggleReplayPlayPauseCommand.RaiseCanExecuteChanged();
            StopReplayCommand.RaiseCanExecuteChanged();
        });
    }

    private async Task RunReplayLoopAsync(long replayGen, CancellationToken ct)
    {
        try
        {
            int count = _replayEntries.Count;
            while (!ct.IsCancellationRequested)
            {
                int idx = Volatile.Read(ref _replayIndex);
                if (idx >= count)
                {
                    break;
                }

                TlogEntry entry = _replayEntries[idx];
                if (idx > 0)
                {
                    TimeSpan delta = entry.TimestampUtc - _replayEntries[idx - 1].TimestampUtc;
                    if (delta > TimeSpan.Zero)
                    {
                        double speed = Math.Max(0.25, ReplaySpeedMultiplier);
                        long scaledTicks = (long)(delta.Ticks / speed);
                        if (scaledTicks > 0)
                        {
                            TimeSpan delay = TimeSpan.FromTicks(Math.Min(scaledTicks, TimeSpan.FromSeconds(5).Ticks));
                            await Task.Delay(delay, ct).ConfigureAwait(false);
                        }
                    }
                }

                if (ct.IsCancellationRequested || Volatile.Read(ref _replayGeneration) != replayGen)
                {
                    break;
                }

                Session.ProcessIncomingBytes(entry.RawPacketBytes.Span);
                Volatile.Write(ref _replayIndex, idx + 1);

                double pct = count <= 1 ? 100.0 : idx * 100.0 / (count - 1);
                string timeText = FormatReplayTimeDisplay(idx);
                _uiDispatcher.InvokeOrBeginInvoke(() =>
                {
                    if (Volatile.Read(ref _replayGeneration) == replayGen && IsReplayMode)
                    {
                        ReplayProgressPercent = pct;
                        ReplayTimeDisplay = timeText;
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (Volatile.Read(ref _replayGeneration) == replayGen && !Volatile.Read(ref _disposed))
            {
                PendingTelemetryUpdate? pending = Interlocked.Exchange(ref _pendingTelemetryUpdate, null);
                long gen = Volatile.Read(ref _connectionGeneration);
                TelemetrySnapshot snap = Session.Current;
                IVehicleProfile? prof = Session.ActiveProfile;

                _uiDispatcher.InvokeOrBeginInvoke(() =>
                {
                    if (Volatile.Read(ref _replayGeneration) == replayGen && IsReplayMode)
                    {
                        IsReplayPlaying = false;
                        if (pending is not null && pending.Generation == gen)
                        {
                            ApplyUiTelemetry(pending.Snapshot, pending.Profile, gen, true, true);
                        }
                        else
                        {
                            ApplyUiTelemetry(snap, prof, gen, true, true);
                        }
                    }
                });
            }
        }
    }

    private string FormatReplayTimeDisplay(int index)
    {
        if (_replayEntries.Count == 0)
        {
            return "00:00 / 00:00";
        }

        int clamped = Math.Clamp(index, 0, _replayEntries.Count - 1);
        DateTimeOffset start = _replayEntries[0].TimestampUtc;
        DateTimeOffset current = _replayEntries[clamped].TimestampUtc;
        DateTimeOffset end = _replayEntries[^1].TimestampUtc;

        TimeSpan elapsed = current > start ? current - start : TimeSpan.Zero;
        TimeSpan total = end > start ? end - start : TimeSpan.Zero;
        return $"{FormatReplaySpan(elapsed)} / {FormatReplaySpan(total)}";
    }

    private static string FormatReplaySpan(TimeSpan span) =>
        span.TotalHours >= 1.0
            ? span.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
            : span.ToString(@"mm\:ss", CultureInfo.InvariantCulture);

    private static string FormatDistanceDisplay(double meters) =>
        meters >= 1000.0
            ? string.Create(CultureInfo.InvariantCulture, $"{meters / 1000.0:0.00} KM")
            : string.Create(CultureInfo.InvariantCulture, $"{meters:0} M");

    private static string FormatDurationDisplay(TimeSpan duration, bool compact)
    {
        if (duration <= TimeSpan.Zero)
        {
            return "--";
        }

        if (duration.TotalSeconds < 60.0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"~{Math.Max(1, Math.Round(duration.TotalSeconds)):0} SN");
        }

        return compact
            ? string.Create(CultureInfo.InvariantCulture, $"~{duration.TotalMinutes:0.#} DK")
            : string.Create(CultureInfo.InvariantCulture, $"~{duration.TotalMinutes:0.0} DK");
    }

    private static void ValidateImportedMissionItem(MissionItem item, int index)
    {
        if (!double.IsFinite(item.Latitude) || item.Latitude is < -90.0 or > 90.0)
        {
            throw new FormatException($"Görev adımı #{index} geçersiz enlem değerine sahip ({item.Latitude}).");
        }

        if (!double.IsFinite(item.Longitude) || item.Longitude is < -180.0 or > 180.0)
        {
            throw new FormatException($"Görev adımı #{index} geçersiz boylam değerine sahip ({item.Longitude}).");
        }

        if (!float.IsFinite(item.Altitude))
        {
            throw new FormatException($"Görev adımı #{index} geçersiz irtifa değerine sahip.");
        }
    }

    private sealed record PendingTelemetryUpdate(
        TelemetrySnapshot Snapshot,
        IVehicleProfile? Profile,
        long Generation,
        bool UpdateHud,
        bool UpdatePanel);

    public sealed class AsyncParameterCommand<T> : ICommand
    {
        private readonly Func<T?, Task> _executeAsync;
        private readonly Predicate<T?>? _canExecute;
        private readonly Action<Exception>? _onError;
        private bool _isExecuting;

        public AsyncParameterCommand(
            Func<T?, Task> executeAsync,
            Predicate<T?>? canExecute = null,
            Action<Exception>? onError = null)
        {
            _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
            _canExecute = canExecute;
            _onError = onError;
        }

        public event EventHandler? CanExecuteChanged;

        public bool IsExecuting => Volatile.Read(ref _isExecuting);

        public bool CanExecute(object? parameter)
        {
            if (IsExecuting)
            {
                return false;
            }

            if (_canExecute is null)
            {
                return true;
            }

            return parameter is T typed
                ? _canExecute(typed)
                : parameter is null && _canExecute(default);
        }

        public async void Execute(object? parameter)
        {
            T? typed = parameter is T value ? value : default;
            await ExecuteAsync(typed);
        }

        public async Task ExecuteAsync(T? parameter)
        {
            if (!CanExecute(parameter))
            {
                return;
            }

            Volatile.Write(ref _isExecuting, true);
            RaiseCanExecuteChanged();
            try
            {
                await _executeAsync(parameter);
            }
            catch (Exception ex)
            {
                _onError?.Invoke(ex);
            }
            finally
            {
                Volatile.Write(ref _isExecuting, false);
                RaiseCanExecuteChanged();
            }
        }

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    private IProgress<MissionTransferProgress> CreateMissionProgressReporter() =>
        new Progress<MissionTransferProgress>(report =>
        {
            int pct = report.TotalCount > 0
                ? Math.Clamp((int)Math.Round(report.CompletedCount * 100.0 / report.TotalCount), 0, 100)
                : 0;
            _uiDispatcher.InvokeOrBeginInvoke(() =>
            {
                MissionTransferStatusText = $"{report.MissionType.ToString().ToUpperInvariant()} · {report.Stage.ToUpperInvariant()} ({report.CompletedCount}/{report.TotalCount})";
                MissionTransferProgressPercent = pct;
            });
        });

    public void RebuildFilteredLogs()
    {
        FilteredLogs.Clear();
        for (int i = 0; i < Logs.Count; i++)
        {
            LogEntry entry = Logs[i];
            if (MatchesLogFilter(entry))
            {
                FilteredLogs.Add(entry);
            }
        }
    }

    private bool MatchesLogFilter(LogEntry entry)
    {
        bool severityMatches = _selectedLogSeverityFilter switch
        {
            "HATA" => entry.Severity <= MavSeverity.Error,
            "UYARI" => entry.Severity == MavSeverity.Warning,
            "BİLGİ" => entry.Severity >= MavSeverity.Notice,
            _ => true
        };

        if (!severityMatches)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(_logSearchText))
        {
            return true;
        }

        string term = _logSearchText.Trim();
        return entry.Source.Contains(term, StringComparison.OrdinalIgnoreCase) ||
               entry.Message.Contains(term, StringComparison.OrdinalIgnoreCase) ||
               entry.Severity.ToString().Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    private void AppendLog(MavSeverity severity, string source, string message)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (Logs.Count > 0 &&
            Logs[0].Severity == severity &&
            string.Equals(Logs[0].Source, source, StringComparison.Ordinal) &&
            string.Equals(Logs[0].Message, message, StringComparison.Ordinal))
        {
            LogEntry previousTop = Logs[0];
            LogEntry updated = previousTop with { TimestampUtc = now, RepeatCount = previousTop.RepeatCount + 1 };
            Logs[0] = updated;
            if (MatchesLogFilter(updated))
            {
                if (FilteredLogs.Count > 0 && Equals(FilteredLogs[0], previousTop))
                {
                    FilteredLogs[0] = updated;
                }
                else
                {
                    RebuildFilteredLogs();
                }
            }
            return;
        }

        var entry = new LogEntry(now, severity, source, message);
        Logs.Insert(0, entry);
        if (MatchesLogFilter(entry))
        {
            FilteredLogs.Insert(0, entry);
        }

        while (Logs.Count > MaxLogEntries)
        {
            LogEntry removed = Logs[^1];
            Logs.RemoveAt(Logs.Count - 1);
            if (FilteredLogs.Count > 0 && Equals(FilteredLogs[^1], removed))
            {
                FilteredLogs.RemoveAt(FilteredLogs.Count - 1);
            }
        }
    }

    private void NotifyTelemetryDisplayProperties()
    {
        OnPropertyChanged(nameof(HasLiveTelemetry));
        OnPropertyChanged(nameof(AltitudeRelativeDisplay));
        OnPropertyChanged(nameof(AltitudeAmslDisplay));
        OnPropertyChanged(nameof(AirspeedDisplay));
        OnPropertyChanged(nameof(GroundspeedDisplay));
        OnPropertyChanged(nameof(VerticalSpeedDisplay));
        OnPropertyChanged(nameof(CurrentThrottleDisplay));
        OnPropertyChanged(nameof(HeadingDisplay));
        OnPropertyChanged(nameof(RollPitchDisplay));
        OnPropertyChanged(nameof(LatitudeDisplay));
        OnPropertyChanged(nameof(LongitudeDisplay));
    }

    private void RefreshDataFreshness()
    {
        (string text, string state) = TelemetryDisplayFormatter.EvaluateDataFreshness(
            HasLiveTelemetry,
            _snapshot,
            _timeProvider.GetUtcNow());
        DataFreshnessText = text;
        DataFreshnessState = state;
    }

    private void RefreshSystemHealth()
    {
        IReadOnlyList<SystemHealthItem> next = TelemetryDisplayFormatter.BuildSystemHealthItems(
            IsTransportConnected || IsReplayMode,
            IsReplayMode,
            _snapshot,
            _dataFreshnessState);

        for (int i = 0; i < next.Count; i++)
        {
            if (i < SystemHealthItems.Count)
            {
                if (!Equals(SystemHealthItems[i], next[i]))
                {
                    SystemHealthItems[i] = next[i];
                }
            }
            else
            {
                SystemHealthItems.Add(next[i]);
            }
        }

        while (SystemHealthItems.Count > next.Count)
        {
            SystemHealthItems.RemoveAt(SystemHealthItems.Count - 1);
        }

        SystemHealthSummaryText = TelemetryDisplayFormatter.EvaluateSystemHealthSummary(HasLiveTelemetry, next);
    }

    private void MarkMissionDirty()
    {
        if (_missionUndoSuppressDepth > 0)
        {
            return;
        }

        IsMissionDirty = true;
        OnPropertyChanged(nameof(MissionSyncStateText));
    }

    private void RecordMissionUndoSnapshot()
    {
        if (_missionUndoSuppressDepth > 0)
        {
            return;
        }

        var items = new List<MissionItem>(MissionItems.Count);
        for (int i = 0; i < MissionItems.Count; i++)
        {
            items.Add(MissionItems[i].ToMissionItem());
        }

        int selectedIdx = SelectedMissionItem is not null ? MissionItems.IndexOf(SelectedMissionItem) : -1;
        _missionUndoStack.Add(new MissionUndoSnapshot(items, selectedIdx, IsMissionDirty));
        while (_missionUndoStack.Count > MaxMissionUndoHistory)
        {
            _missionUndoStack.RemoveAt(0);
        }

        OnPropertyChanged(nameof(CanUndoMission));
        UndoMissionCommand?.RaiseCanExecuteChanged();
    }

    private MissionUndoScope BeginMissionUndoBatch()
    {
        RecordMissionUndoSnapshot();
        _missionUndoSuppressDepth++;
        return new MissionUndoScope(this);
    }

    private sealed record MissionUndoSnapshot(
        IReadOnlyList<MissionItem> Items,
        int SelectedIndex,
        bool WasDirty);

    private readonly struct MissionUndoScope(MainViewModel owner) : IDisposable
    {
        public void Dispose() => owner._missionUndoSuppressDepth = Math.Max(0, owner._missionUndoSuppressDepth - 1);
    }

    private void LogCommandError(Exception ex) =>
        _uiDispatcher.InvokeOrBeginInvoke(() => AppendLog(MavSeverity.Error, "SİSTEM", ex.Message));

    private static bool TryGetLatLon(JsonElement root, out double lat, out double lon)
    {
        lat = 0;
        lon = 0;
        if (!root.TryGetProperty("lat", out JsonElement latEl) ||
            !root.TryGetProperty("lon", out JsonElement lonEl))
        {
            return false;
        }

        lat = latEl.GetDouble();
        lon = lonEl.GetDouble();
        return TelemetryDisplayFormatter.IsValidGeoCoordinate(lat, lon);
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
