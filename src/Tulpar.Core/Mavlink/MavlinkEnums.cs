namespace Tulpar.Core.Mavlink;

/// <summary>
/// MAV_TYPE: Vehicle or component type reported in HEARTBEAT.
/// </summary>
public enum MavType : byte
{
    Generic = 0,
    FixedWing = 1,
    Quadrotor = 2,
    Coaxial = 3,
    Helicopter = 4,
    AntennaTracker = 5,
    Gcs = 6,
    Airship = 7,
    FreeBalloon = 8,
    Rocket = 9,
    GroundRover = 10,
    SurfaceBoat = 11,
    Submarine = 12,
    Hexarotor = 13,
    Octorotor = 14,
    Tricopter = 15,
    FlappingWing = 16,
    Kite = 17,
    OnboardController = 18,
    VtolTailsitterDuorotor = 19,
    VtolTailsitterQuadrotor = 20,
    VtolTiltrotor = 21,
    VtolFixedrotor = 22,
    VtolTailsitter = 23,
    VtolTilthot = 24,
    VtolReserved5 = 25,
    Gimbal = 26,
    Adsb = 27,
    Parafoil = 28,
    Dodecarotor = 29,
    Camera = 30,
    ChargingStation = 31,
    Flarm = 32,
    Servo = 33,
    Odid = 34,
    Decarotor = 35,
    Battery = 36,
    Parachute = 37,
    Log = 38,
    Osd = 39,
    Imu = 40,
    Gps = 41,
    Winch = 42
}

/// <summary>
/// MAV_AUTOPILOT: Autopilot stack type reported in HEARTBEAT.
/// </summary>
public enum MavAutopilot : byte
{
    Generic = 0,
    Reserved = 1,
    Slugs = 2,
    ArduPilotMega = 3,
    OpenPilot = 4,
    GenericWaypointsOnly = 5,
    GenericWaypointsAndSimpleNavigationOnly = 6,
    GenericMissionFull = 7,
    Invalid = 8,
    Ppz = 9,
    Udb = 10,
    Fp = 11,
    Px4 = 12,
    Smaccmpilot = 13,
    Autoquad = 14,
    Armazila = 15,
    Aerob = 16,
    Asluav = 17,
    Smartap = 18,
    Airrails = 19,
    Reflex = 20
}

/// <summary>
/// MAV_MODE_FLAG: Bitmask encoding safety arming and autopilot mode flags.
/// </summary>
[Flags]
public enum MavModeFlag : byte
{
    None = 0,
    CustomModeEnabled = 1,
    TestEnabled = 2,
    AutoEnabled = 4,
    GuidedEnabled = 8,
    StabilizeEnabled = 16,
    HilEnabled = 32,
    ManualInputEnabled = 64,
    SafetyArmed = 128
}

/// <summary>
/// MAV_STATE: System operational state reported in HEARTBEAT.
/// </summary>
public enum MavState : byte
{
    Uninit = 0,
    Boot = 1,
    Calibrating = 2,
    Standby = 3,
    Active = 4,
    Critical = 5,
    Emergency = 6,
    Poweroff = 7,
    FlightTermination = 8
}

/// <summary>
/// GPS_FIX_TYPE: GNSS fix status reported in GPS_RAW_INT.
/// </summary>
public enum GpsFixType : byte
{
    NoGps = 0,
    NoFix = 1,
    Fix2D = 2,
    Fix3D = 3,
    Dgps = 4,
    RtkFloat = 5,
    RtkFixed = 6,
    Static = 7,
    Ppp = 8
}

/// <summary>
/// MAV_FRAME: Coordinate frame identifiers for mission items and target setpoints.
/// </summary>
public enum MavFrame : byte
{
    Global = 0,
    LocalNed = 1,
    Mission = 2,
    GlobalRelativeAlt = 3,
    LocalEnu = 4,
    GlobalInt = 5,
    GlobalRelativeAltInt = 6,
    LocalOffsetNed = 7,
    BodyNed = 8,
    BodyOffsetNed = 9,
    GlobalTerrainAlt = 10,
    GlobalTerrainAltInt = 11,
    BodyFrd = 12,
    LocalFrd = 20,
    LocalFlu = 21
}

/// <summary>
/// MAV_CMD: Navigation, DO, and system command IDs.
/// </summary>
public enum MavCmd : ushort
{
    NavWaypoint = 16,
    NavLoiterUnlim = 17,
    NavLoiterTurns = 18,
    NavLoiterTime = 19,
    NavReturnToLaunch = 20,
    NavLand = 21,
    NavTakeoff = 22,
    NavLandLocal = 23,
    NavTakeoffLocal = 24,
    NavFollow = 25,
    NavContinueAndChangeAlt = 30,
    NavLoiterToAlt = 31,
    NavRoi = 80,
    NavSplineWaypoint = 82,
    NavVtolTakeoff = 84,
    NavVtolLand = 85,
    NavDelay = 93,
    DoSetMode = 176,
    DoJump = 177,
    DoChangeSpeed = 178,
    DoSetHome = 179,
    DoSetRelay = 181,
    DoRepeatRelay = 182,
    DoSetServo = 183,
    DoRepeatServo = 184,
    DoReposition = 192,
    DoPauseContinue = 193,
    DoSetReverse = 194,
    DoSetRoi = 201,
    DoDigicamControl = 203,
    DoMountControl = 205,
    DoSetCamTriggDist = 206,
    DoFenceEnable = 207,
    DoParachute = 208,
    DoInvertedFlight = 210,
    DoGripper = 211,
    DoAutotuneEnable = 212,
    ComponentArmDisarm = 400,
    GetHomePosition = 410,
    StartRxPair = 500,
    SetMessageInterval = 511,
    RequestMessage = 512,
    DoVtolTransition = 3000,
    NavFenceReturnPoint = 5000,
    NavFencePolygonVertexInclusion = 5001,
    NavFencePolygonVertexExclusion = 5002,
    NavFenceCircleInclusion = 5003,
    NavFenceCircleExclusion = 5004,
    NavRallyPoint = 5100
}

/// <summary>
/// MAV_MISSION_TYPE: Plan domain for mission protocol exchanges.
/// </summary>
public enum MavMissionType : byte
{
    Mission = 0,
    Fence = 1,
    Rally = 2,
    All = 255
}

/// <summary>
/// MAV_MISSION_RESULT: Result codes returned in MISSION_ACK.
/// </summary>
public enum MavMissionResult : byte
{
    Accepted = 0,
    Error = 1,
    UnsupportedFrame = 2,
    Unsupported = 3,
    NoSpace = 4,
    Invalid = 5,
    InvalidParam1 = 6,
    InvalidParam2 = 7,
    InvalidParam3 = 8,
    InvalidParam4 = 9,
    InvalidParam5X = 10,
    InvalidParam6Y = 11,
    InvalidParam7 = 12,
    InvalidSequence = 13,
    Denied = 14,
    OperationCancelled = 15
}

/// <summary>
/// MAV_RESULT: Execution result codes returned in COMMAND_ACK.
/// </summary>
public enum MavResult : byte
{
    Accepted = 0,
    TemporarilyRejected = 1,
    Denied = 2,
    Unsupported = 3,
    Failed = 4,
    InProgress = 5,
    Cancelled = 6,
    CommandLongOnly = 7,
    CommandIntOnly = 8,
    CommandUnsupportedMavFrame = 9
}

/// <summary>
/// MAV_SEVERITY: Severity levels for STATUSTEXT messages (RFC 5424).
/// </summary>
public enum MavSeverity : byte
{
    Emergency = 0,
    Alert = 1,
    Critical = 2,
    Error = 3,
    Warning = 4,
    Notice = 5,
    Info = 6,
    Debug = 7
}

/// <summary>
/// MAV_DATA_STREAM: Stream categories used by REQUEST_DATA_STREAM.
/// </summary>
public enum MavDataStream : byte
{
    All = 0,
    RawSensors = 1,
    ExtendedStatus = 2,
    RcChannels = 3,
    RawController = 4,
    Position = 6,
    Extra1 = 10,
    Extra2 = 11,
    Extra3 = 12
}

/// <summary>
/// MAV_COMPONENT: Standard MAVLink component IDs.
/// </summary>
public enum MavComponent : byte
{
    All = 0,
    Autopilot1 = 1,
    MissionPlanner = 190
}

/// <summary>
/// MAV_BATTERY_FUNCTION: Intended power role of a battery pack.
/// </summary>
public enum MavBatteryFunction : byte
{
    Unknown = 0,
    All = 1,
    Propulsion = 2,
    Avionics = 3,
    Payload = 4
}

/// <summary>
/// MAV_BATTERY_TYPE: Battery chemistry type.
/// </summary>
public enum MavBatteryType : byte
{
    Unknown = 0,
    Lipo = 1,
    Life = 2,
    Lion = 3,
    Nimh = 4
}

/// <summary>
/// MAV_BATTERY_CHARGE_STATE: Battery discharge / health state.
/// </summary>
public enum MavBatteryChargeState : byte
{
    Undefined = 0,
    Ok = 1,
    Low = 2,
    Critical = 3,
    Emergency = 4,
    Failed = 5,
    Unhealthy = 6,
    Charging = 7
}

/// <summary>
/// MAV_BATTERY_MODE: Smart battery operational mode.
/// </summary>
public enum MavBatteryMode : byte
{
    Unknown = 0,
    AutoDischarging = 1,
    HotSwap = 2
}

/// <summary>
/// MAV_BATTERY_FAULT: Smart battery fault bitmask.
/// </summary>
[Flags]
public enum MavBatteryFault : uint
{
    None = 0,
    DeepDischarge = 1,
    Spikes = 2,
    CellFail = 4,
    OverCurrent = 8,
    OverTemperature = 16,
    UnderTemperature = 32,
    IncompatibleVoltage = 64,
    IncompatibleFirmware = 128,
    IncompatibleCellsConfiguration = 256
}

/// <summary>
/// MAV_VTOL_STATE: VTOL flight configuration states for MAV_CMD_DO_VTOL_TRANSITION.
/// </summary>
public enum MavVtolState : byte
{
    Undefined = 0,
    TransitionToFw = 1,
    TransitionToMc = 2,
    Mc = 3,
    Fw = 4
}

/// <summary>
/// POSITION_TARGET_TYPEMASK: Bitmask indicating ignored setpoint axes in SET_POSITION_TARGET_GLOBAL_INT.
/// </summary>
[Flags]
public enum PositionTargetTypemask : ushort
{
    None = 0,
    XIgnore = 1,
    YIgnore = 2,
    ZIgnore = 4,
    VxIgnore = 8,
    VyIgnore = 16,
    VzIgnore = 32,
    AxIgnore = 64,
    AyIgnore = 128,
    AzIgnore = 256,
    ForceSet = 512,
    YawIgnore = 1024,
    YawRateIgnore = 2048
}
