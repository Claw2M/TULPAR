using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Tulpar.Core.Mavlink;
using Tulpar.Core.Missions;
using Tulpar.Core.Session;
using Tulpar.Core.Transport;
using Tulpar.Core.Vehicles;

namespace Tulpar.App.ViewModels;

public enum ConnectionKind
{
    Serial,
    Udp,
    Tcp
}

public enum SafetyActionKind
{
    None,
    Arm,
    Disarm,
    Rtl,
    Hold,
    Takeoff,
    Land,
    GuidedFlyHere
}

public enum MapEditMode
{
    None,
    Mission,
    Fence,
    Bounds
}

public enum VideoSourceKind
{
    UdpRtp,
    Rtsp,
    UsbDirectShow
}

public readonly record struct GeoPoint(double Latitude, double Longitude);

public sealed record MapBounds(double MinLat, double MinLon, double MaxLat, double MaxLon);

public sealed record SafetyActionRequest(
    SafetyActionKind Kind,
    string Title,
    string Description,
    double Latitude = 0.0,
    double Longitude = 0.0,
    float AltitudeMeters = 0f,
    float? LoiterRadius = null);

public sealed record LogEntry(
    DateTimeOffset TimestampUtc,
    MavSeverity Severity,
    string Source,
    string Message)
{
    public string DisplayLine =>
        $"[{TimestampUtc.ToLocalTime():HH:mm:ss}] [{Severity.ToString().ToUpperInvariant()}] {Source}: {Message}";
}

public interface ITransportFactory
{
    ITransport CreateSerial(string portName, int baudRate);
    ITransport CreateUdp(int listenPort, string? remoteHost = null, int remotePort = 0);
    ITransport CreateTcp(string host, int port);
}

public sealed class DefaultTransportFactory : ITransportFactory
{
    public ITransport CreateSerial(string portName, int baudRate) => new SerialTransport(portName, baudRate);
    public ITransport CreateUdp(int listenPort, string? remoteHost = null, int remotePort = 0) => new UdpTransport(listenPort, remoteHost, remotePort);
    public ITransport CreateTcp(string host, int port) => new TcpTransport(host, port);
}

public interface IUiDispatcher
{
    void InvokeOrBeginInvoke(Action action);
}

public sealed class InlineUiDispatcher : IUiDispatcher
{
    public void InvokeOrBeginInvoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
    }
}

public sealed class WpfUiDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher;

    public WpfUiDispatcher(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public void InvokeOrBeginInvoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else if (!_dispatcher.HasShutdownStarted && !_dispatcher.HasShutdownFinished)
        {
            _dispatcher.BeginInvoke(DispatcherPriority.DataBind, action);
        }
    }
}

public sealed class MainViewModelOptions
{
    public string? TelemetryLogDirectory { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "Tulpar",
        "Logs");

    public bool EnableUdpForwarder { get; init; } = true;
    public string UdpForwarderHost { get; init; } = "127.0.0.1";
    public int UdpForwarderPort { get; init; } = 14551;
    public TimeSpan HeartbeatCheckInterval { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan HudThrottleInterval { get; init; } = TimeSpan.FromMilliseconds(16);
    public TimeSpan PanelThrottleInterval { get; init; } = TimeSpan.FromMilliseconds(33);
    public TimeProvider? TimeProvider { get; init; }
    public Func<IReadOnlyList<string>>? SerialPortEnumerator { get; init; }
    public Func<IReadOnlyList<string>>? VideoDeviceEnumerator { get; init; }
}

/// <summary>
/// Pure geometry evaluator for local polygon geofence containment.
/// </summary>
public static class GeofenceEvaluator
{
    /// <summary>
    /// Returns true if the point is inside (or on the boundary of) the polygon using ray-casting.
    /// Polygons with fewer than 3 vertices are treated as inactive (returns true).
    /// </summary>
    public static bool IsPointInsidePolygon(double latitude, double longitude, IReadOnlyList<GeoPoint> polygon)
    {
        ArgumentNullException.ThrowIfNull(polygon);

        if (!double.IsFinite(latitude) || !double.IsFinite(longitude))
        {
            return false;
        }

        if (polygon.Count < 3)
        {
            return true;
        }

        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            double xi = polygon[i].Longitude;
            double yi = polygon[i].Latitude;
            double xj = polygon[j].Longitude;
            double yj = polygon[j].Latitude;

            if (IsPointOnSegment(longitude, latitude, xi, yi, xj, yj))
            {
                return true;
            }

            bool crossesLatBand = (yi > latitude) != (yj > latitude);
            if (crossesLatBand)
            {
                double intersectLon = (xj - xi) * (latitude - yi) / (yj - yi) + xi;
                if (longitude < intersectLon)
                {
                    inside = !inside;
                }
            }
        }

        return inside;
    }

    private static bool IsPointOnSegment(double px, double py, double ax, double ay, double bx, double by)
    {
        const double eps = 1e-9;
        double cross = (px - ax) * (by - ay) - (py - ay) * (bx - ax);
        if (Math.Abs(cross) > eps)
        {
            return false;
        }

        return px >= Math.Min(ax, bx) - eps &&
               px <= Math.Max(ax, bx) + eps &&
               py >= Math.Min(ay, by) - eps &&
               py <= Math.Max(ay, by) + eps;
    }
}

/// <summary>
/// Shared media resource locator (MRL) and LibVLC low-latency options builder.
/// </summary>
public static class VideoMrlBuilder
{
    public const string DefaultUdpMrl = "udp://@:5600";
    public const string DefaultRtspMrl = "rtsp://127.0.0.1:8554/stream";
    public const string DefaultDirectShowMrl = "dshow://";

    public static string BuildEffectiveMrl(VideoSourceKind kind, string? input)
    {
        string trimmed = input?.Trim() ?? string.Empty;
        return kind switch
        {
            VideoSourceKind.UdpRtp => string.IsNullOrWhiteSpace(trimmed) ? DefaultUdpMrl : trimmed,
            VideoSourceKind.Rtsp => string.IsNullOrWhiteSpace(trimmed) ? DefaultRtspMrl : trimmed,
            VideoSourceKind.UsbDirectShow => DefaultDirectShowMrl,
            _ => string.IsNullOrWhiteSpace(trimmed) ? DefaultUdpMrl : trimmed
        };
    }

    public static IReadOnlyList<string> BuildMediaOptions(VideoSourceKind kind, string? input)
    {
        var options = new List<string>
        {
            ":clock-jitter=0",
            ":clock-synchro=0"
        };

        switch (kind)
        {
            case VideoSourceKind.UdpRtp:
                options.Add(":network-caching=80");
                options.Add(":live-caching=80");
                break;
            case VideoSourceKind.Rtsp:
                options.Add(":network-caching=120");
                options.Add(":live-caching=80");
                break;
            case VideoSourceKind.UsbDirectShow:
                options.Add(":live-caching=50");
                options.Add(":dshow-adev=none");
                string device = NormalizeDirectShowDevice(input);
                if (!string.IsNullOrWhiteSpace(device))
                {
                    options.Add($":dshow-vdev={device}");
                }
                break;
        }

        return options;
    }

    private static string NormalizeDirectShowDevice(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        string trimmed = input.Trim();
        if (trimmed.Equals(DefaultDirectShowMrl, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        const string vdevPrefix = "video=";
        if (trimmed.StartsWith(vdevPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return trimmed[vdevPrefix.Length..].Trim();
        }

        const string optPrefix = ":dshow-vdev=";
        if (trimmed.StartsWith(optPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return trimmed[optPrefix.Length..].Trim();
        }

        return trimmed;
    }
}

/// <summary>
/// Safe Windows DirectShow video input device enumerator falling back to empty list in headless environments.
/// </summary>
internal static class DirectShowDeviceEnumerator
{
    private static readonly Guid ClsidSystemDeviceEnum = new("62BE5D10-60EB-11d0-BD3B-00A0C911CE86");
    private static readonly Guid ClsidVideoInputDeviceCategory = new("860BB310-5D01-11d0-BD3B-00A0C911CE86");
    private static readonly Guid IidIPropertyBag = new("55272A00-42CB-11CE-8135-00AA004BB851");

    public static IReadOnlyList<string> GetVideoCaptureDevices()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<string>();
        }

        var devices = new List<string>();
        ICreateDevEnum? devEnum = null;
        IEnumMoniker? enumMoniker = null;

        try
        {
            Type? comType = Type.GetTypeFromCLSID(ClsidSystemDeviceEnum, throwOnError: false);
            if (comType is null)
            {
                return Array.Empty<string>();
            }

            devEnum = Activator.CreateInstance(comType) as ICreateDevEnum;
            if (devEnum is null)
            {
                return Array.Empty<string>();
            }

            Guid cat = ClsidVideoInputDeviceCategory;
            int hr = devEnum.CreateClassEnumerator(ref cat, out enumMoniker, 0);
            if (hr != 0 || enumMoniker is null)
            {
                return Array.Empty<string>();
            }

            var monikers = new IMoniker[1];
            while (enumMoniker.Next(1, monikers, IntPtr.Zero) == 0 && monikers[0] is not null)
            {
                IMoniker moniker = monikers[0];
                try
                {
                    Guid bagId = IidIPropertyBag;
                    moniker.BindToStorage(null!, null!, ref bagId, out object bagObj);
                    if (bagObj is IPropertyBag bag)
                    {
                        try
                        {
                            if (bag.Read("FriendlyName", out object? nameObj, IntPtr.Zero) == 0 &&
                                nameObj is string name &&
                                !string.IsNullOrWhiteSpace(name))
                            {
                                devices.Add(name.Trim());
                            }
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(bag);
                        }
                    }
                }
                catch
                {
                    // Skip broken DirectShow filters safely.
                }
                finally
                {
                    Marshal.ReleaseComObject(moniker);
                    monikers[0] = null!;
                }
            }
        }
        catch
        {
            return Array.Empty<string>();
        }
        finally
        {
            if (enumMoniker is not null)
            {
                Marshal.ReleaseComObject(enumMoniker);
            }

            if (devEnum is not null)
            {
                Marshal.ReleaseComObject(devEnum);
            }
        }

        return devices.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    [ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICreateDevEnum
    {
        [PreserveSig]
        int CreateClassEnumerator([In] ref Guid pType, [Out] out IEnumMoniker? ppEnumMoniker, [In] int dwFlags);
    }

    [ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyBag
    {
        [PreserveSig]
        int Read([In, MarshalAs(UnmanagedType.LPWStr)] string pszPropName, [Out] out object? pVar, [In] IntPtr pErrorLog);

        [PreserveSig]
        int Write([In, MarshalAs(UnmanagedType.LPWStr)] string pszPropName, [In] ref object pVar);
    }
}

public sealed class EditableMissionItem : INotifyPropertyChanged
{
    public static IReadOnlyList<string> ReferenceFrameOptions { get; } = ["REL", "AMSL"];

    private int _seq;
    private MavCmd _command;
    private MavFrame _frame;
    private double _latitude;
    private double _longitude;
    private float _altitude;
    private float _param1;
    private float _param2;
    private float _param3;
    private float _param4;
    private bool _autocontinue = true;

    public EditableMissionItem()
        : this(0, MavFrame.GlobalRelativeAltInt, MavCmd.NavWaypoint, 0f, 0f, 0f, 0f, 0.0, 0.0, 50f, true)
    {
    }

    public EditableMissionItem(
        int seq,
        MavFrame frame,
        MavCmd command,
        float param1,
        float param2,
        float param3,
        float param4,
        double latitude,
        double longitude,
        float altitude,
        bool autocontinue = true)
    {
        _seq = seq;
        _frame = NormalizeFrame(frame);
        _command = command;
        _param1 = param1;
        _param2 = param2;
        _param3 = param3;
        _param4 = param4;
        _latitude = latitude;
        _longitude = longitude;
        _altitude = altitude;
        _autocontinue = autocontinue;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Seq
    {
        get => _seq;
        set => SetField(ref _seq, value);
    }

    public MavCmd Command
    {
        get => _command;
        set => SetField(ref _command, value);
    }

    public MavFrame Frame
    {
        get => _frame;
        set
        {
            MavFrame normalized = NormalizeFrame(value);
            if (SetField(ref _frame, normalized))
            {
                OnPropertyChanged(nameof(ReferenceFrameLabel));
            }
        }
    }

    public string ReferenceFrameLabel
    {
        get => _frame is MavFrame.GlobalInt or MavFrame.Global ? "AMSL" : "REL";
        set
        {
            MavFrame target = string.Equals(value?.Trim(), "AMSL", StringComparison.OrdinalIgnoreCase)
                ? MavFrame.GlobalInt
                : MavFrame.GlobalRelativeAltInt;
            Frame = target;
        }
    }

    public double Latitude
    {
        get => _latitude;
        set => SetField(ref _latitude, value);
    }

    public double Longitude
    {
        get => _longitude;
        set => SetField(ref _longitude, value);
    }

    public float Altitude
    {
        get => _altitude;
        set => SetField(ref _altitude, value);
    }

    public float Param1
    {
        get => _param1;
        set => SetField(ref _param1, value);
    }

    public float Param2
    {
        get => _param2;
        set => SetField(ref _param2, value);
    }

    public float Param3
    {
        get => _param3;
        set => SetField(ref _param3, value);
    }

    public float Param4
    {
        get => _param4;
        set => SetField(ref _param4, value);
    }

    public bool Autocontinue
    {
        get => _autocontinue;
        set => SetField(ref _autocontinue, value);
    }

    public MissionItem ToMissionItem() =>
        new(Seq, Frame, Command, Param1, Param2, Param3, Param4, Latitude, Longitude, Altitude, Autocontinue);

    public static EditableMissionItem FromMissionItem(MissionItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new EditableMissionItem(
            item.Seq,
            item.Frame,
            item.Command,
            item.Param1,
            item.Param2,
            item.Param3,
            item.Param4,
            item.Latitude,
            item.Longitude,
            item.Altitude,
            item.Autocontinue);
    }

    private static MavFrame NormalizeFrame(MavFrame frame) =>
        frame switch
        {
            MavFrame.Global => MavFrame.GlobalInt,
            MavFrame.GlobalRelativeAlt => MavFrame.GlobalRelativeAltInt,
            _ => frame
        };

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

public sealed class EditableFencePoint : INotifyPropertyChanged
{
    private int _index;
    private double _latitude;
    private double _longitude;

    public EditableFencePoint(int index, double latitude, double longitude)
    {
        _index = index;
        _latitude = latitude;
        _longitude = longitude;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Index
    {
        get => _index;
        set => SetField(ref _index, value);
    }

    public double Latitude
    {
        get => _latitude;
        set => SetField(ref _latitude, value);
    }

    public double Longitude
    {
        get => _longitude;
        set => SetField(ref _longitude, value);
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

/// <summary>
/// Central cockpit ViewModel orchestrating MAVLink transport, vehicle profile adaptation,
/// 2-step safety guards, local polygon geofence monitoring, Map/Camera swap, and mission transfers.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private const byte GcsSystemId = 255;
    private const byte GcsComponentId = (byte)MavComponent.MissionPlanner;
    private const int MaxLogEntries = 500;

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

    private ITransport? _transport;
    private TelemetryRecorder? _recorder;
    private MavlinkUdpForwarder? _udpForwarder;
    private CancellationTokenSource? _backgroundCts;
    private Task? _backgroundLoopTask;

    private TelemetrySnapshot _snapshot = TelemetrySnapshot.Empty;
    private TelemetrySnapshot _hudSnapshot = TelemetrySnapshot.Empty;
    private IVehicleProfile? _activeProfile;
    private (VehicleCategory Category, MavAutopilot Autopilot, Type? ProfileType) _lastProfileKey;
    private byte _dataStreamRequestedSysId;
    private MavType _dataStreamRequestedVehicleType;
    private int _txSequence;
    private bool _disposed;

    private long _lastHudPushTick;
    private long _lastPanelPushTick;
    private volatile TelemetrySnapshot? _pendingTelemetrySnapshot;
    private volatile IVehicleProfile? _pendingProfile;

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
    private string _selectedMapLayerId = "satellite";
    private string _missionTransferStatusText = "HAZIR";
    private int _missionTransferProgressPercent;

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

        foreach (MavCmd cmd in DefaultCommands)
        {
            AvailableMissionCommands.Add(cmd);
        }

        RefreshSerialPorts();
        RefreshVideoDevices();
        InitializeCommands();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<TelemetrySnapshot>? VehicleMapStateUpdated;
    public event Action? MissionPlanChanged;
    public event Action? FencePolygonChanged;
    public event Action<MapEditMode>? MapEditModeChanged;
    public event Action<string, IReadOnlyList<string>>? VideoStartRequested;
    public event Action? VideoStopRequested;

    public VehicleSession Session { get; }
    public MissionProtocolManager MissionProtocol { get; }

    public IReadOnlyList<ConnectionKind> ConnectionKinds { get; } =
        [ConnectionKind.Udp, ConnectionKind.Serial, ConnectionKind.Tcp];

    public IReadOnlyList<int> AvailableBaudRates { get; } =
        [57600, 115200, 230400, 460800, 921600];

    public IReadOnlyList<VideoSourceKind> VideoSourceKinds { get; } =
        [VideoSourceKind.UdpRtp, VideoSourceKind.Rtsp, VideoSourceKind.UsbDirectShow];

    public ObservableCollection<string> AvailableSerialPorts { get; } = [];
    public ObservableCollection<string> AvailableVideoDevices { get; } = [];
    public ObservableCollection<FlightModeOption> AvailableModes { get; } = [];
    public ObservableCollection<MavCmd> AvailableMissionCommands { get; } = [];
    public ObservableCollection<EditableMissionItem> MissionItems { get; } = [];
    public ObservableCollection<EditableFencePoint> FencePoints { get; } = [];
    public ObservableCollection<LogEntry> Logs { get; } = [];

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

    public string VehicleBadgeText
    {
        get
        {
            if (!_snapshot.IsConnected || _snapshot.SystemId == 0)
            {
                return "BAĞLI DEĞİL";
            }

            string label = _activeProfile?.DisplayLabel ?? _snapshot.VehicleDisplayLabel;
            string ap = _snapshot.Autopilot switch
            {
                MavAutopilot.ArduPilotMega => "ARDUPILOT",
                MavAutopilot.Px4 => "PX4",
                _ => _snapshot.Autopilot.ToString().ToUpperInvariant()
            };
            return $"{label} · {ap}";
        }
    }

    public ConnectionKind SelectedConnectionKind
    {
        get => _selectedConnectionKind;
        set
        {
            if (SetField(ref _selectedConnectionKind, value))
            {
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

    public string BatteryDisplayText =>
        _snapshot.BatteryVoltageVolts > 0.0 || _snapshot.BatteryRemainingPercent >= 0
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{_snapshot.BatteryVoltageVolts:0.0}V · {(_snapshot.BatteryRemainingPercent >= 0 ? $"{_snapshot.BatteryRemainingPercent}%" : "--")}")
            : "-- V · --%";

    public bool IsBatteryCritical =>
        _snapshot.IsConnected &&
        _snapshot.BatteryRemainingPercent is >= 0 and <= 15;

    public bool IsBatteryLow =>
        _snapshot.IsConnected &&
        _snapshot.BatteryRemainingPercent is > 15 and <= 25;

    public string GpsDisplayText =>
        _snapshot.IsConnected
            ? $"{FormatGpsFix(_snapshot.GpsFixType)} · {_snapshot.SatellitesVisible} UYDU"
            : "NO GPS · 0 UYDU";

    public string RssiDisplayText =>
        _snapshot.IsConnected && (_snapshot.RadioRssi > 0 || _snapshot.RemoteRssi > 0)
            ? $"{Math.Clamp((int)Math.Round(_snapshot.RadioRssi / 2.55), 0, 100)}%"
            : "--%";

    public double HomeDistanceMeters =>
        _snapshot.HasHome && IsValidGeoCoordinate(_snapshot.Latitude, _snapshot.Longitude) && IsValidGeoCoordinate(_snapshot.HomeLatitude, _snapshot.HomeLongitude)
            ? ComputeHaversineMeters(_snapshot.HomeLatitude, _snapshot.HomeLongitude, _snapshot.Latitude, _snapshot.Longitude)
            : 0.0;

    public string HomeDistanceDisplayText =>
        _snapshot.HasHome
            ? string.Create(CultureInfo.InvariantCulture, $"{HomeDistanceMeters:0.0} m")
            : "-- m";

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
                MapEditModeChanged?.Invoke(value);
            }
        }
    }

    public bool IsMissionMapPickActive => ActiveMapEditMode == MapEditMode.Mission;
    public bool IsFenceMapPickActive => ActiveMapEditMode == MapEditMode.Fence;

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
                        ? $"UDP MAVLink yansıtıcı aktif ({_options.UdpForwarderHost}:{_options.UdpForwarderPort})."
                        : "UDP MAVLink yansıtıcı durduruldu.");
            }
        }
    }

    public string UdpForwarderEndpointText => $"{_options.UdpForwarderHost}:{_options.UdpForwarderPort}";

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

    public AsyncRelayCommand ConnectOrDisconnectCommand { get; private set; } = null!;
    public RelayCommand RefreshSerialPortsCommand { get; private set; } = null!;
    public RelayCommand RefreshVideoDevicesCommand { get; private set; } = null!;
    public RelayCommand SwapMapAndCameraCommand { get; private set; } = null!;
    public RelayCommand RequestArmOrDisarmCommand { get; private set; } = null!;
    public RelayCommand RequestRtlCommand { get; private set; } = null!;
    public RelayCommand RequestHoldCommand { get; private set; } = null!;
    public RelayCommand RequestTakeoffCommand { get; private set; } = null!;
    public RelayCommand RequestLandCommand { get; private set; } = null!;
    public AsyncRelayCommand ConfirmSafetyActionCommand { get; private set; } = null!;
    public RelayCommand CancelSafetyActionCommand { get; private set; } = null!;
    public AsyncRelayCommand ApplySelectedModeCommand { get; private set; } = null!;
    public AsyncRelayCommand TriggerServoCommand { get; private set; } = null!;
    public AsyncRelayCommand SetHomeCurrentCommand { get; private set; } = null!;
    public RelayCommand StartVideoCommand { get; private set; } = null!;
    public RelayCommand StopVideoCommand { get; private set; } = null!;
    public RelayCommand ToggleMissionMapEditCommand { get; private set; } = null!;
    public RelayCommand ToggleFenceMapEditCommand { get; private set; } = null!;
    public RelayCommand AddManualWaypointCommand { get; private set; } = null!;
    public RelayCommand RemoveSelectedMissionWaypointCommand { get; private set; } = null!;
    public RelayCommand MoveWaypointUpCommand { get; private set; } = null!;
    public RelayCommand MoveWaypointDownCommand { get; private set; } = null!;
    public RelayCommand RemoveSelectedFencePointCommand { get; private set; } = null!;
    public RelayCommand ClearFencePointsCommand { get; private set; } = null!;
    public AsyncRelayCommand UploadMissionCommand { get; private set; } = null!;
    public AsyncRelayCommand DownloadMissionCommand { get; private set; } = null!;
    public AsyncRelayCommand ClearMissionCommand { get; private set; } = null!;
    public AsyncRelayCommand UploadFenceCommand { get; private set; } = null!;
    public AsyncRelayCommand DownloadFenceCommand { get; private set; } = null!;
    public AsyncRelayCommand ClearFenceCommand { get; private set; } = null!;
    public RelayCommand ToggleLeftPanelCommand { get; private set; } = null!;
    public RelayCommand ToggleMissionDrawerCommand { get; private set; } = null!;

    private void InitializeCommands()
    {
        ConnectOrDisconnectCommand = new AsyncRelayCommand(ToggleConnectionAsync, () => !IsBusy, LogCommandError);
        RefreshSerialPortsCommand = new RelayCommand(RefreshSerialPorts);
        RefreshVideoDevicesCommand = new RelayCommand(RefreshVideoDevices);
        SwapMapAndCameraCommand = new RelayCommand(SwapMapAndCamera);
        RequestArmOrDisarmCommand = new RelayCommand(RequestArmOrDisarm, CanIssueVehicleCommand);
        RequestRtlCommand = new RelayCommand(RequestRtl, CanIssueVehicleCommand);
        RequestHoldCommand = new RelayCommand(RequestHold, CanIssueVehicleCommand);
        RequestTakeoffCommand = new RelayCommand(RequestTakeoff, CanIssueVehicleCommand);
        RequestLandCommand = new RelayCommand(RequestLand, CanIssueVehicleCommand);
        ConfirmSafetyActionCommand = new AsyncRelayCommand(ConfirmSafetyActionAsync, () => HasPendingSafetyAction && CanIssueVehicleCommand(), LogCommandError);
        CancelSafetyActionCommand = new RelayCommand(CancelSafetyAction, () => HasPendingSafetyAction);
        ApplySelectedModeCommand = new AsyncRelayCommand(() => ApplyFlightModeAsync(SelectedMode), () => CanIssueVehicleCommand() && SelectedMode is not null, LogCommandError);
        TriggerServoCommand = new AsyncRelayCommand(() => TriggerServoAsync(ServoChannel, ServoPwm), CanIssueVehicleCommand, LogCommandError);
        SetHomeCurrentCommand = new AsyncRelayCommand(SetHomeCurrentAsync, CanIssueVehicleCommand, LogCommandError);
        StartVideoCommand = new RelayCommand(() => StartVideo(null));
        StopVideoCommand = new RelayCommand(StopVideo);
        ToggleMissionMapEditCommand = new RelayCommand(() => ActiveMapEditMode = ActiveMapEditMode == MapEditMode.Mission ? MapEditMode.None : MapEditMode.Mission);
        ToggleFenceMapEditCommand = new RelayCommand(() => ActiveMapEditMode = ActiveMapEditMode == MapEditMode.Fence ? MapEditMode.None : MapEditMode.Fence);
        AddManualWaypointCommand = new RelayCommand(AddManualWaypoint);
        RemoveSelectedMissionWaypointCommand = new RelayCommand(RemoveSelectedMissionWaypoint, () => SelectedMissionItem is not null);
        MoveWaypointUpCommand = new RelayCommand(() => MoveSelectedWaypoint(-1), () => SelectedMissionItem is not null);
        MoveWaypointDownCommand = new RelayCommand(() => MoveSelectedWaypoint(1), () => SelectedMissionItem is not null);
        RemoveSelectedFencePointCommand = new RelayCommand(RemoveSelectedFencePoint, () => SelectedFencePoint is not null);
        ClearFencePointsCommand = new RelayCommand(ClearFencePoints);
        UploadMissionCommand = new AsyncRelayCommand(UploadMissionAsync, CanIssueVehicleCommand, LogCommandError);
        DownloadMissionCommand = new AsyncRelayCommand(DownloadMissionAsync, CanIssueVehicleCommand, LogCommandError);
        ClearMissionCommand = new AsyncRelayCommand(ClearMissionAsync, CanIssueVehicleCommand, LogCommandError);
        UploadFenceCommand = new AsyncRelayCommand(UploadFenceAsync, CanIssueVehicleCommand, LogCommandError);
        DownloadFenceCommand = new AsyncRelayCommand(DownloadFenceAsync, CanIssueVehicleCommand, LogCommandError);
        ClearFenceCommand = new AsyncRelayCommand(ClearFenceAsync, CanIssueVehicleCommand, LogCommandError);
        ToggleLeftPanelCommand = new RelayCommand(() => IsLeftPanelVisible = !IsLeftPanelVisible);
        ToggleMissionDrawerCommand = new RelayCommand(() => IsMissionDrawerExpanded = !IsMissionDrawerExpanded);
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

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (IsTransportConnected || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            ITransport transport = CreateConfiguredTransport();
            TelemetryRecorder? recorder = null;
            if (!string.IsNullOrWhiteSpace(_options.TelemetryLogDirectory))
            {
                recorder = new TelemetryRecorder(_options.TelemetryLogDirectory);
            }

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

            lock (_sessionSync)
            {
                _transport = transport;
                _recorder = recorder;
                _dataStreamRequestedSysId = 0;
                _dataStreamRequestedVehicleType = MavType.Generic;
                _backgroundCts = new CancellationTokenSource();
                _backgroundLoopTask = RunBackgroundMonitorAsync(transport, _backgroundCts.Token);
            }

            _uiDispatcher.InvokeOrBeginInvoke(() =>
            {
                IsTransportConnected = true;
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

    public async Task DisconnectAsync()
    {
        ITransport? transport;
        TelemetryRecorder? recorder;
        CancellationTokenSource? cts;
        Task? monitorTask;

        lock (_sessionSync)
        {
            transport = _transport;
            recorder = _recorder;
            cts = _backgroundCts;
            monitorTask = _backgroundLoopTask;

            _transport = null;
            _recorder = null;
            _backgroundCts = null;
            _backgroundLoopTask = null;
            _dataStreamRequestedSysId = 0;
            _dataStreamRequestedVehicleType = MavType.Generic;
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
        recorder?.Dispose();

        Session.ResetOnDisconnect();

        _uiDispatcher.InvokeOrBeginInvoke(() =>
        {
            PendingSafetyAction = null;
            IsTransportConnected = false;
            IsGeofenceBreached = false;
            GeofenceAlertMessage = string.Empty;
            ActiveProfile = null;
            _lastProfileKey = default;
            AvailableModes.Clear();
            SelectedMode = null;
            ResetMissionCommandsToDefault();
            AppendLog(MavSeverity.Notice, "BAĞLANTI", "Bağlantı kesildi.");
        });
    }

    public void SwapMapAndCamera()
    {
        IsCameraPrimary = !IsCameraPrimary;
    }

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
            "HOLD / LOITER ONAYI",
            "Araç mevcut konumunda bekleme (HOLD / LOITER) moduna geçirilecek. Onaylıyor musunuz?");
    }

    public void RequestTakeoff()
    {
        if (!CanIssueVehicleCommand())
        {
            AppendLog(MavSeverity.Warning, "GÜVENLİK", "Kalkış komutu için bağlı ve tanımlı bir araç gereklidir.");
            return;
        }

        float alt = (float)Math.Clamp(TakeoffAltitudeMeters, 1.0, 2000.0);
        PendingSafetyAction = new SafetyActionRequest(
            SafetyActionKind.Takeoff,
            "OTONOM KALKIŞ ONAYI",
            string.Create(CultureInfo.InvariantCulture, $"Araç {alt:0.0} m hedef irtifaya kalkış (TAKEOFF) yapacak. Onaylıyor musunuz?"),
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

        float effectiveAlt = altMeters ?? (float)GuidedDefaultAltitudeMeters;
        PendingSafetyAction = new SafetyActionRequest(
            SafetyActionKind.GuidedFlyHere,
            "GUIDED YÖNLENDİRME ONAYI",
            string.Create(
                CultureInfo.InvariantCulture,
                $"Araç {lat:0.000000}, {lon:0.000000} ({effectiveAlt:0.0}m) hedefine yönlendirilecek. Onaylıyor musunuz?"),
            Latitude: lat,
            Longitude: lon,
            AltitudeMeters: effectiveAlt,
            LoiterRadius: loiterRadius);
    }

    public void CancelSafetyAction()
    {
        PendingSafetyAction = null;
    }

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
                FlightModeOption? holdMode = FindPreferredHoldMode(profile, Snapshot.Autopilot);
                commandMessage = holdMode is not null
                    ? new SetModeMessage(targetSys, (MavModeFlag)holdMode.BaseMode, holdMode.CustomMode)
                    : new CommandLongMessage(targetSys, targetComp, MavCmd.DoPauseContinue, 0, Param1: 0f);
                break;

            case SafetyActionKind.Takeoff:
                MavCmd takeoffCmd = profile.Category == VehicleCategory.Vtol ? MavCmd.NavVtolTakeoff : MavCmd.NavTakeoff;
                commandMessage = new CommandLongMessage(targetSys, targetComp, takeoffCmd, 0, Param7: action.AltitudeMeters);
                break;

            case SafetyActionKind.Land:
                MavCmd landCmd = profile.Category == VehicleCategory.Vtol ? MavCmd.NavVtolLand : MavCmd.NavLand;
                commandMessage = new CommandLongMessage(targetSys, targetComp, landCmd, 0);
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
        if (!IsValidGeoCoordinate(lat, lon))
        {
            return;
        }

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

        AddMissionItemTracked(item);
        SelectedMissionItem = item;
        RenumberMissionItems();
        MissionPlanChanged?.Invoke();
    }

    public void MoveMissionWaypoint(int seq, double lat, double lon)
    {
        if (!IsValidGeoCoordinate(lat, lon))
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
        MissionPlanChanged?.Invoke();
    }

    public void AddManualWaypoint()
    {
        double lat = IsValidGeoCoordinate(Snapshot.Latitude, Snapshot.Longitude)
            ? Snapshot.Latitude
            : IsValidGeoCoordinate(Snapshot.HomeLatitude, Snapshot.HomeLongitude)
                ? Snapshot.HomeLatitude
                : 39.9334;
        double lon = IsValidGeoCoordinate(Snapshot.Latitude, Snapshot.Longitude)
            ? Snapshot.Longitude
            : IsValidGeoCoordinate(Snapshot.HomeLatitude, Snapshot.HomeLongitude)
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

    public void RemoveSelectedMissionWaypoint()
    {
        if (SelectedMissionItem is null)
        {
            return;
        }

        SelectedMissionItem.PropertyChanged -= OnEditableMissionItemPropertyChanged;
        MissionItems.Remove(SelectedMissionItem);
        SelectedMissionItem = null;
        RenumberMissionItems();
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

        MissionItems.Move(currentIdx, targetIdx);
        RenumberMissionItems();
        MissionPlanChanged?.Invoke();
    }

    public void AddFencePoint(double lat, double lon)
    {
        if (!IsValidGeoCoordinate(lat, lon))
        {
            return;
        }

        var pt = new EditableFencePoint(FencePoints.Count, lat, lon);
        AddFencePointTracked(pt);
        SelectedFencePoint = pt;
        RenumberFencePoints();
        EvaluateLocalGeofence(Snapshot);
        OnPropertyChanged(nameof(GeofenceStatusSummary));
        FencePolygonChanged?.Invoke();
    }

    public void MoveFencePoint(int index, double lat, double lon)
    {
        if (!IsValidGeoCoordinate(lat, lon) || index < 0 || index >= FencePoints.Count)
        {
            return;
        }

        FencePoints[index].Latitude = lat;
        FencePoints[index].Longitude = lon;
        EvaluateLocalGeofence(Snapshot);
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
        OnPropertyChanged(nameof(GeofenceStatusSummary));
        FencePolygonChanged?.Invoke();
    }

    public void ClearFencePoints()
    {
        ClearFencePointsTracked();
        SelectedFencePoint = null;
        EvaluateLocalGeofence(Snapshot);
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
        var progress = CreateMissionProgressReporter();

        try
        {
            await MissionProtocol.UploadAsync(
                Snapshot.SystemId,
                compId,
                MavMissionType.Mission,
                items,
                (bytes, ct) => SendRawPacketAsync(bytes, ct),
                progress,
                CancellationToken.None).ConfigureAwait(false);

            _uiDispatcher.InvokeOrBeginInvoke(() =>
            {
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
        var progress = CreateMissionProgressReporter();

        try
        {
            IReadOnlyList<MissionItem> downloaded = await MissionProtocol.DownloadAsync(
                Snapshot.SystemId,
                compId,
                MavMissionType.Mission,
                (bytes, ct) => SendRawPacketAsync(bytes, ct),
                progress,
                CancellationToken.None).ConfigureAwait(false);

            _uiDispatcher.InvokeOrBeginInvoke(() =>
            {
                ClearMissionItemsTracked();
                foreach (MissionItem item in downloaded)
                {
                    AddMissionItemTracked(EditableMissionItem.FromMissionItem(item));
                }

                RenumberMissionItems();
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

        var fenceItems = new List<MissionItem>(FencePoints.Count);
        int vertexCount = FencePoints.Count;
        for (int i = 0; i < vertexCount; i++)
        {
            EditableFencePoint pt = FencePoints[i];
            fenceItems.Add(new MissionItem(
                Seq: i,
                Frame: MavFrame.GlobalInt,
                Command: MavCmd.NavFencePolygonVertexInclusion,
                Param1: vertexCount,
                Param2: 0f,
                Param3: 0f,
                Param4: 0f,
                Latitude: pt.Latitude,
                Longitude: pt.Longitude,
                Altitude: 0f,
                Autocontinue: false));
        }

        byte compId = Snapshot.ComponentId == 0 ? (byte)MavComponent.Autopilot1 : Snapshot.ComponentId;
        var progress = CreateMissionProgressReporter();

        try
        {
            await MissionProtocol.UploadAsync(
                Snapshot.SystemId,
                compId,
                MavMissionType.Fence,
                fenceItems,
                (bytes, ct) => SendRawPacketAsync(bytes, ct),
                progress,
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
        var progress = CreateMissionProgressReporter();

        try
        {
            IReadOnlyList<MissionItem> downloaded = await MissionProtocol.DownloadAsync(
                Snapshot.SystemId,
                compId,
                MavMissionType.Fence,
                (bytes, ct) => SendRawPacketAsync(bytes, ct),
                progress,
                CancellationToken.None).ConfigureAwait(false);

            _uiDispatcher.InvokeOrBeginInvoke(() =>
            {
                ClearFencePointsTracked();
                int idx = 0;
                foreach (MissionItem item in downloaded)
                {
                    if (item.Command == MavCmd.NavFencePolygonVertexInclusion ||
                        (item.Command != MavCmd.NavFenceReturnPoint && IsValidGeoCoordinate(item.Latitude, item.Longitude)))
                    {
                        AddFencePointTracked(new EditableFencePoint(idx++, item.Latitude, item.Longitude));
                    }
                }

                EvaluateLocalGeofence(Snapshot);
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

    public string ExportWpl110()
    {
        RenumberMissionItems();
        return MissionFileCodec.ExportQgcWpl110(MissionItems.Select(x => x.ToMissionItem()).ToList());
    }

    public void ImportWpl110(string content)
    {
        IReadOnlyList<MissionItem> imported = MissionFileCodec.ImportQgcWpl110(content);
        ClearMissionItemsTracked();
        foreach (MissionItem item in imported)
        {
            AddMissionItemTracked(EditableMissionItem.FromMissionItem(item));
        }

        RenumberMissionItems();
        MissionPlanChanged?.Invoke();
        AppendLog(MavSeverity.Info, "DOSYA", $"WPL 110 görev dosyası içe aktarıldı ({MissionItems.Count} WP).");
    }

    public string ExportMissionJson()
    {
        RenumberMissionItems();
        var fence = FencePoints.Select((p, i) => new MissionItem(
            i, MavFrame.GlobalInt, MavCmd.NavFencePolygonVertexInclusion, FencePoints.Count, 0f, 0f, 0f, p.Latitude, p.Longitude, 0f, false)).ToList();
        var doc = new MissionDocument(MavMissionType.Mission, MissionItems.Select(x => x.ToMissionItem()).ToList(), fence);
        return MissionFileCodec.ExportJson(doc);
    }

    public void ImportMissionJson(string json)
    {
        MissionDocument doc = MissionFileCodec.ImportJson(json);
        ClearMissionItemsTracked();
        foreach (MissionItem item in doc.Items)
        {
            AddMissionItemTracked(EditableMissionItem.FromMissionItem(item));
        }

        RenumberMissionItems();
        ClearFencePointsTracked();
        int idx = 0;
        foreach (MissionItem f in doc.FenceItems)
        {
            AddFencePointTracked(new EditableFencePoint(idx++, f.Latitude, f.Longitude));
        }

        EvaluateLocalGeofence(Snapshot);
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

                string? type = typeEl.GetString();
                switch (type)
                {
                    case "missionAdd":
                        if (TryGetLatLon(root, out double mLat, out double mLon))
                        {
                            AddMissionWaypoint(mLat, mLon);
                        }
                        break;

                    case "missionMove":
                        if (root.TryGetProperty("seq", out JsonElement seqEl) &&
                            seqEl.TryGetInt32(out int seq) &&
                            TryGetLatLon(root, out double mmLat, out double mmLon))
                        {
                            MoveMissionWaypoint(seq, mmLat, mmLon);
                        }
                        break;

                    case "fenceAdd":
                        if (TryGetLatLon(root, out double fLat, out double fLon))
                        {
                            AddFencePoint(fLat, fLon);
                        }
                        break;

                    case "fenceMove":
                        if (root.TryGetProperty("index", out JsonElement idxEl) &&
                            idxEl.TryGetInt32(out int idx) &&
                            TryGetLatLon(root, out double fmLat, out double fmLon))
                        {
                            MoveFencePoint(idx, fmLat, fmLon);
                        }
                        break;

                    case "guidedTarget":
                        if (TryGetLatLon(root, out double gLat, out double gLon))
                        {
                            RequestGuidedFlyHere(gLat, gLon);
                        }
                        break;

                    case "boundsSelected":
                        if (root.TryGetProperty("bounds", out JsonElement boundsEl) &&
                            boundsEl.TryGetProperty("minLat", out JsonElement minLatEl) &&
                            boundsEl.TryGetProperty("minLon", out JsonElement minLonEl) &&
                            boundsEl.TryGetProperty("maxLat", out JsonElement maxLatEl) &&
                            boundsEl.TryGetProperty("maxLon", out JsonElement maxLonEl))
                        {
                            SelectedOfflineBounds = new MapBounds(
                                minLatEl.GetDouble(),
                                minLonEl.GetDouble(),
                                maxLatEl.GetDouble(),
                                maxLonEl.GetDouble());
                            OnPropertyChanged(nameof(SelectedOfflineBounds));
                            if (ActiveMapEditMode == MapEditMode.Bounds)
                            {
                                ActiveMapEditMode = MapEditMode.None;
                            }
                            AppendLog(MavSeverity.Info, "HARİTA", "Çevrimdışı indirme dikdörtgeni seçildi.");
                        }
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

    public void AppendSystemLog(MavSeverity severity, string source, string message) =>
        _uiDispatcher.InvokeOrBeginInvoke(() => AppendLog(severity, source, message));

    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _disposed))
        {
            return;
        }

        Volatile.Write(ref _disposed, true);
        Session.Bus.RawFrameReceived -= OnRawFrameReceived;
        Session.Bus.SnapshotUpdated -= OnSessionSnapshotUpdated;
        Session.Bus.StatusTextReceived -= OnStatusTextReceived;
        Session.Bus.CommandAckReceived -= OnCommandAckReceived;

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
        try
        {
            _recorder?.RecordRawFrame(_timeProvider.GetUtcNow(), bytes.Span);
        }
        catch
        {
            // Recorder errors should never interrupt live telemetry.
        }

        _udpForwarder?.ForwardRawBytes(bytes.Span);
        Session.ProcessIncomingBytes(bytes.Span);
    }

    private void OnTransportDisconnected(string reason)
    {
        _ = Task.Run(async () =>
        {
            await DisconnectAsync().ConfigureAwait(false);
            _uiDispatcher.InvokeOrBeginInvoke(() =>
                AppendLog(MavSeverity.Warning, "BAĞLANTI", $"Bağlantı koptu: {reason}"));
        });
    }

    private void OnRawFrameReceived(MavlinkFrame frame)
    {
        MissionProtocol.HandleIncomingFrame(frame);
    }

    private void OnSessionSnapshotUpdated(TelemetrySnapshot snapshot)
    {
        try
        {
            _recorder?.RecordSnapshot(snapshot);
        }
        catch
        {
        }

        IVehicleProfile? profile = Session.ActiveProfile;

        if (snapshot.IsConnected &&
            snapshot.SystemId != 0 &&
            (_dataStreamRequestedSysId != snapshot.SystemId || _dataStreamRequestedVehicleType != snapshot.VehicleType))
        {
            _dataStreamRequestedSysId = snapshot.SystemId;
            _dataStreamRequestedVehicleType = snapshot.VehicleType;
            byte sys = snapshot.SystemId;
            byte comp = snapshot.ComponentId == 0 ? (byte)MavComponent.Autopilot1 : snapshot.ComponentId;
            _ = SendMessageAsync(
                new RequestDataStreamMessage(sys, comp, (byte)MavDataStream.All, ReqMessageRate: 4, StartStop: 1),
                CancellationToken.None);
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
            _pendingProfile = profile;
            _pendingTelemetrySnapshot = snapshot;
            return;
        }

        if (hudDue)
        {
            _lastHudPushTick = nowTicks;
        }

        if (panelDue)
        {
            _lastPanelPushTick = nowTicks;
            _pendingTelemetrySnapshot = null;
        }

        _uiDispatcher.InvokeOrBeginInvoke(() => ApplyUiTelemetry(snapshot, profile, updateHud: hudDue, updatePanel: panelDue));
    }

    private void ApplyUiTelemetry(TelemetrySnapshot snapshot, IVehicleProfile? profile, bool updateHud, bool updatePanel)
    {
        if (profile is not null && !ReferenceEquals(_activeProfile, profile))
        {
            ActiveProfile = profile;
        }
        else if (profile is null && _activeProfile is not null)
        {
            ActiveProfile = null;
        }

        if (updateHud)
        {
            HudSnapshot = snapshot;
        }

        if (updatePanel)
        {
            Snapshot = snapshot;
            SyncProfileSelectorsIfNeeded(snapshot, profile);
            EvaluateLocalGeofence(snapshot);
            VehicleMapStateUpdated?.Invoke(snapshot);
            RaiseCommandStates();
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

            if (profile.SurfaceOnlyMode)
            {
                DefaultWaypointAltitudeMeters = 0f;
            }
            else if (DefaultWaypointAltitudeMeters <= 0f)
            {
                DefaultWaypointAltitudeMeters = 50f;
            }
        }

        if (SelectedMode is null || !string.Equals(SelectedMode.Name, snapshot.ModeName, StringComparison.Ordinal))
        {
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
        _uiDispatcher.InvokeOrBeginInvoke(() =>
            AppendLog(status.Severity, "OTOPİLOT", status.Text));
    }

    private void OnCommandAckReceived(MavlinkFrame frame)
    {
        CommandAckMessage ack = CommandAckMessage.Decode(frame);
        MavSeverity severity = ack.Result == MavResult.Accepted ? MavSeverity.Info : MavSeverity.Warning;
        _uiDispatcher.InvokeOrBeginInvoke(() =>
            AppendLog(severity, "ACK", $"{ack.Command} -> {ack.Result}"));
    }

    private async Task RunBackgroundMonitorAsync(ITransport transport, CancellationToken ct)
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
                DateTimeOffset now = _timeProvider.GetUtcNow();
                Session.CheckHeartbeatTimeout(now);

                // Flush any throttled trailing telemetry frame so UI never stays stale after burst end.
                TelemetrySnapshot? pending = Interlocked.Exchange(ref _pendingTelemetrySnapshot, null);
                if (pending is not null)
                {
                    IVehicleProfile? pendingProf = _pendingProfile;
                    _uiDispatcher.InvokeOrBeginInvoke(() =>
                        ApplyUiTelemetry(pending, pendingProf, updateHud: true, updatePanel: true));
                }

                long nowTicks = _timeProvider.GetTimestamp();
                if (!sentFirstGcsHeartbeat || _timeProvider.GetElapsedTime(lastGcsHeartbeatTick, nowTicks) >= TimeSpan.FromSeconds(1))
                {
                    sentFirstGcsHeartbeat = true;
                    lastGcsHeartbeatTick = nowTicks;
                    if (transport.IsConnected)
                    {
                        var gcsHb = new HeartbeatMessage(
                            MavType.Gcs,
                            MavAutopilot.Invalid,
                            MavModeFlag.None,
                            0u,
                            MavState.Active);
                        byte[] packet = MavlinkEncoder.EncodeV2(GcsSystemId, GcsComponentId, NextTxSequence(), gcsHb);
                        try
                        {
                            await transport.SendAsync(packet, ct).ConfigureAwait(false);
                        }
                        catch
                        {
                            // Transport error handled via Disconnected event.
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void EvaluateLocalGeofence(TelemetrySnapshot snapshot)
    {
        if (FencePoints.Count < 3 ||
            !snapshot.IsConnected ||
            snapshot.SystemId == 0 ||
            !IsValidGeoCoordinate(snapshot.Latitude, snapshot.Longitude))
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

        bool inside = GeofenceEvaluator.IsPointInsidePolygon(snapshot.Latitude, snapshot.Longitude, polygon);
        if (!inside)
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

    private Task SendMessageAsync(IMavlinkMessage message, CancellationToken ct)
    {
        byte[] packet = MavlinkEncoder.EncodeV2(GcsSystemId, GcsComponentId, NextTxSequence(), message);
        return SendRawPacketAsync(packet, ct);
    }

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
        RequestArmOrDisarmCommand.RaiseCanExecuteChanged();
        RequestRtlCommand.RaiseCanExecuteChanged();
        RequestHoldCommand.RaiseCanExecuteChanged();
        RequestTakeoffCommand.RaiseCanExecuteChanged();
        RequestLandCommand.RaiseCanExecuteChanged();
        ConfirmSafetyActionCommand.RaiseCanExecuteChanged();
        CancelSafetyActionCommand.RaiseCanExecuteChanged();
        ApplySelectedModeCommand.RaiseCanExecuteChanged();
        TriggerServoCommand.RaiseCanExecuteChanged();
        SetHomeCurrentCommand.RaiseCanExecuteChanged();
        UploadMissionCommand.RaiseCanExecuteChanged();
        DownloadMissionCommand.RaiseCanExecuteChanged();
        ClearMissionCommand.RaiseCanExecuteChanged();
        UploadFenceCommand.RaiseCanExecuteChanged();
        DownloadFenceCommand.RaiseCanExecuteChanged();
        ClearFenceCommand.RaiseCanExecuteChanged();
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

    private void OnEditableMissionItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EditableMissionItem.Latitude) or nameof(EditableMissionItem.Longitude))
        {
            MissionPlanChanged?.Invoke();
        }
    }

    private void OnEditableFencePointPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EditableFencePoint.Latitude) or nameof(EditableFencePoint.Longitude))
        {
            EvaluateLocalGeofence(Snapshot);
            FencePolygonChanged?.Invoke();
        }
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

    private void AppendLog(MavSeverity severity, string source, string message)
    {
        var entry = new LogEntry(_timeProvider.GetUtcNow(), severity, source, message);
        Logs.Insert(0, entry);
        while (Logs.Count > MaxLogEntries)
        {
            Logs.RemoveAt(Logs.Count - 1);
        }
    }

    private void LogCommandError(Exception ex)
    {
        _uiDispatcher.InvokeOrBeginInvoke(() =>
            AppendLog(MavSeverity.Error, "SİSTEM", ex.Message));
    }

    private static FlightModeOption? FindPreferredHoldMode(IVehicleProfile profile, MavAutopilot autopilot)
    {
        IReadOnlyList<FlightModeOption> modes = profile.GetFlightModes(autopilot);
        foreach (string preferred in new[] { "HOLD", "LOITER", "AUTO_LOITER", "QLOITER", "POSHOLD", "BRAKE" })
        {
            FlightModeOption? match = modes.FirstOrDefault(m => string.Equals(m.Name, preferred, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static string FormatGpsFix(GpsFixType fix) =>
        fix switch
        {
            GpsFixType.NoGps => "NO GPS",
            GpsFixType.NoFix => "NO FIX",
            GpsFixType.Fix2D => "2D FIX",
            GpsFixType.Fix3D => "3D FIX",
            GpsFixType.Dgps => "DGPS",
            GpsFixType.RtkFloat => "RTK FLT",
            GpsFixType.RtkFixed => "RTK FIX",
            _ => fix.ToString().ToUpperInvariant()
        };

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
        return IsValidGeoCoordinate(lat, lon);
    }

    private static bool IsValidGeoCoordinate(double lat, double lon) =>
        double.IsFinite(lat) &&
        double.IsFinite(lon) &&
        Math.Abs(lat) <= 90.0 &&
        Math.Abs(lon) <= 180.0 &&
        !(lat == 0.0 && lon == 0.0);

    private static double ComputeHaversineMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double EarthRadiusMeters = 6371000.0;
        double dLat = (lat2 - lat1) * (Math.PI / 180.0);
        double dLon = (lon2 - lon1) * (Math.PI / 180.0);
        double a = Math.Sin(dLat / 2.0) * Math.Sin(dLat / 2.0) +
                   Math.Cos(lat1 * (Math.PI / 180.0)) * Math.Cos(lat2 * (Math.PI / 180.0)) *
                   Math.Sin(dLon / 2.0) * Math.Sin(dLon / 2.0);
        double c = 2.0 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1.0 - a));
        return EarthRadiusMeters * c;
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
