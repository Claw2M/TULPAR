using Tulpar.Core.Mavlink;
using Tulpar.Core.Vehicles;

namespace Tulpar.Core.Session;

/// <summary>
/// Stateful single-vehicle MAVLink telemetry session managing stream parsing, adaptive profile binding,
/// stale-state reset on vehicle swap, and 3-second heartbeat timeout detection.
/// </summary>
public sealed class VehicleSession
{
    private const byte GcsSystemId = 255;
    private const double HeartbeatTimeoutSeconds = 3.0;
    private const double RadToDeg = 180.0 / Math.PI;

    private readonly Lock _sync = new();
    private readonly MavlinkParser _parser = new();
    private readonly TimeProvider _timeProvider;

    private TelemetrySnapshot _current = TelemetrySnapshot.Empty;
    private IVehicleProfile? _activeProfile;
    private byte _targetComponentId;
    private byte? _lockedSystemId;
    private bool _autoLockOnFirstHeartbeat;
    private long _sessionEpoch;
    private bool _hasVfrHud;
    private bool _hasGlobalPosition;

    public VehicleSession()
        : this(TimeProvider.System, null)
    {
    }

    public VehicleSession(TelemetryBus bus)
        : this(TimeProvider.System, bus)
    {
    }

    public VehicleSession(TimeProvider timeProvider, TelemetryBus? bus = null)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        Bus = bus ?? new TelemetryBus();
    }

    public TelemetrySnapshot Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    public IVehicleProfile? ActiveProfile
    {
        get
        {
            lock (_sync)
            {
                return _activeProfile;
            }
        }
    }

    public TelemetryBus Bus { get; }

    /// <summary>
    /// Gets or sets whether the session automatically locks onto the first valid vehicle system ID
    /// to prevent multi-system heartbeat thrashing until unlocked or explicitly swapped.
    /// </summary>
    public bool AutoLockOnFirstHeartbeat
    {
        get
        {
            lock (_sync)
            {
                return _autoLockOnFirstHeartbeat;
            }
        }
        set
        {
            lock (_sync)
            {
                _autoLockOnFirstHeartbeat = value;
            }
        }
    }

    /// <summary>
    /// Gets the currently locked MAVLink system ID, or <see langword="null"/> when unlocked.
    /// </summary>
    public byte? LockedSystemId
    {
        get
        {
            lock (_sync)
            {
                return _lockedSystemId;
            }
        }
    }

    /// <summary>
    /// Locks the session to the specified MAVLink system ID, resetting stale state if a different vehicle was active.
    /// </summary>
    public void LockSystem(byte systemId)
    {
        if (systemId == 0 || systemId == GcsSystemId)
        {
            throw new ArgumentOutOfRangeException(nameof(systemId), "Vehicle system ID must be in [1, 254].");
        }

        TelemetrySnapshot? snapshotToEmit = null;
        long epoch;

        lock (_sync)
        {
            epoch = _sessionEpoch;
            _lockedSystemId = systemId;

            if (_current.SystemId != 0 && _current.SystemId != systemId)
            {
                ResetVehicleStateCore(_timeProvider.GetUtcNow());
                snapshotToEmit = _current;
            }
        }

        if (snapshotToEmit is not null && Interlocked.Read(ref _sessionEpoch) == epoch)
        {
            Bus.PublishSnapshot(snapshotToEmit);
        }
    }

    /// <summary>
    /// Locks the session onto the currently connected vehicle system ID, if one is active.
    /// </summary>
    public bool LockCurrentSystem()
    {
        lock (_sync)
        {
            if (_current.SystemId == 0)
            {
                return false;
            }

            _lockedSystemId = _current.SystemId;
            return true;
        }
    }

    /// <summary>
    /// Clears any active system ID lock so another vehicle can be bound.
    /// </summary>
    public void UnlockSystem()
    {
        lock (_sync)
        {
            _lockedSystemId = null;
        }
    }

    /// <summary>
    /// Explicitly swaps the session to <paramref name="systemId"/> and locks onto it.
    /// </summary>
    public void SelectSystem(byte systemId) => LockSystem(systemId);

    /// <summary>
    /// Feeds raw transport bytes through the MAVLink parser and updates telemetry state.
    /// </summary>
    public void ProcessIncomingBytes(ReadOnlySpan<byte> data)
    {
        List<BusEvent>? pendingEvents = null;
        long epoch;

        lock (_sync)
        {
            epoch = _sessionEpoch;
            IEnumerable<MavlinkFrame> frames = _parser.Feed(data);
            DateTimeOffset now = _timeProvider.GetUtcNow();

            foreach (MavlinkFrame frame in frames)
            {
                pendingEvents ??= new List<BusEvent>();
                pendingEvents.Add(BusEvent.Raw(frame));

                if (frame.MessageId == HeartbeatMessage.MsgId)
                {
                    if (TryApplyHeartbeat(frame, now, out TelemetrySnapshot updated))
                    {
                        pendingEvents.Add(BusEvent.Snapshot(updated));
                    }

                    continue;
                }

                // Require an identified vehicle HEARTBEAT first and ignore frames from GCS or unrelated system IDs.
                if (_current.SystemId == 0 || frame.SystemId == GcsSystemId || frame.SystemId != _current.SystemId)
                {
                    continue;
                }

                if (frame.MessageId == StatusTextMessage.MsgId)
                {
                    pendingEvents.Add(BusEvent.Status(StatusTextMessage.Decode(frame)));
                    continue;
                }

                if (frame.MessageId == CommandAckMessage.MsgId)
                {
                    pendingEvents.Add(BusEvent.Ack(frame));
                    continue;
                }

                // Filter flight telemetry by target component so secondary gimbals/cameras cannot overwrite autopilot state.
                if (_targetComponentId != 0 &&
                    frame.ComponentId != _targetComponentId &&
                    frame.ComponentId != (byte)MavComponent.Autopilot1)
                {
                    continue;
                }

                if (TryApplyTelemetryFrame(frame, now, out TelemetrySnapshot telemetrySnapshot))
                {
                    pendingEvents.Add(BusEvent.Snapshot(telemetrySnapshot));
                }
            }
        }

        if (pendingEvents is not null)
        {
            DispatchEvents(pendingEvents, epoch);
        }
    }

    /// <summary>
    /// Checks whether more than 3 seconds have elapsed since the last vehicle heartbeat.
    /// </summary>
    public void CheckHeartbeatTimeout(DateTimeOffset now)
    {
        TelemetrySnapshot? snapshotToEmit = null;
        long epoch;

        lock (_sync)
        {
            epoch = _sessionEpoch;
            if (_current.LastHeartbeatUtc == default || _current.LinkLost)
            {
                return;
            }

            if ((now - _current.LastHeartbeatUtc).TotalSeconds > HeartbeatTimeoutSeconds)
            {
                _current = _current with
                {
                    LinkLost = true,
                    UpdatedUtc = now
                };
                snapshotToEmit = _current;
            }
        }

        if (snapshotToEmit is not null && Interlocked.Read(ref _sessionEpoch) == epoch)
        {
            Bus.PublishSnapshot(snapshotToEmit);
        }
    }

    /// <summary>
    /// Clears parser buffers, vehicle profile, and telemetry state on transport disconnect.
    /// </summary>
    public void ResetOnDisconnect()
    {
        TelemetrySnapshot snapshotToEmit;

        lock (_sync)
        {
            Interlocked.Increment(ref _sessionEpoch);
            _parser.Reset();
            _lockedSystemId = null;
            ResetVehicleStateCore(_timeProvider.GetUtcNow());
            snapshotToEmit = _current;
        }

        Bus.PublishSnapshot(snapshotToEmit);
    }

    private void ResetVehicleStateCore(DateTimeOffset now)
    {
        _targetComponentId = 0;
        _hasVfrHud = false;
        _hasGlobalPosition = false;
        _activeProfile = null;
        _current = TelemetrySnapshot.Empty with { UpdatedUtc = now };
    }

    private bool TryApplyHeartbeat(MavlinkFrame frame, DateTimeOffset now, out TelemetrySnapshot updated)
    {
        HeartbeatMessage hb = HeartbeatMessage.Decode(frame);

        if (!IsVehicleHeartbeat(frame, hb))
        {
            updated = _current;
            return false;
        }

        bool isVehicleSwap =
            _current.SystemId != 0 &&
            (_current.SystemId != frame.SystemId ||
             _current.VehicleType != hb.Type ||
             _current.Autopilot != hb.Autopilot);

        if (isVehicleSwap)
        {
            _targetComponentId = 0;
            _hasVfrHud = false;
            _hasGlobalPosition = false;
            _current = TelemetrySnapshot.Empty;
        }

        if (_targetComponentId == 0 || frame.ComponentId == (byte)MavComponent.Autopilot1)
        {
            _targetComponentId = frame.ComponentId;
        }

        if (_autoLockOnFirstHeartbeat && _lockedSystemId is null)
        {
            _lockedSystemId = frame.SystemId;
        }

        if (_activeProfile is null || isVehicleSwap)
        {
            _activeProfile = VehicleProfileFactory.Create(hb.Type, hb.Autopilot);
        }

        string modeName = _activeProfile.ResolveModeName(hb.Autopilot, hb.CustomMode, (byte)hb.BaseMode);
        bool isArmed = hb.BaseMode.HasFlag(MavModeFlag.SafetyArmed);

        _current = _current with
        {
            IsConnected = true,
            LinkLost = false,
            SystemId = frame.SystemId,
            ComponentId = _targetComponentId,
            VehicleType = hb.Type,
            Autopilot = hb.Autopilot,
            VehicleDisplayLabel = _activeProfile.DisplayLabel,
            VehicleCategory = _activeProfile.Category,
            ModeName = modeName,
            IsArmed = isArmed,
            LastHeartbeatUtc = now,
            UpdatedUtc = now
        };

        updated = _current;
        return true;
    }

    private bool IsVehicleHeartbeat(MavlinkFrame frame, HeartbeatMessage hb)
    {
        if (frame.SystemId == 0 || frame.SystemId == GcsSystemId || hb.Type == MavType.Gcs)
        {
            return false;
        }

        if (_lockedSystemId is byte lockedSysId && frame.SystemId != lockedSysId)
        {
            return false;
        }

        if (IsAuxiliaryComponentType(hb.Type))
        {
            return false;
        }

        if (hb.Type == MavType.AntennaTracker && hb.Autopilot == MavAutopilot.Invalid)
        {
            return false;
        }

        // Do not let non-autopilot components on the same vehicle overwrite the active flight controller.
        if (_current.SystemId == frame.SystemId &&
            _targetComponentId != 0 &&
            frame.ComponentId != _targetComponentId &&
            frame.ComponentId != (byte)MavComponent.Autopilot1)
        {
            return false;
        }

        return true;
    }

    private static bool IsAuxiliaryComponentType(MavType type) =>
        type is MavType.OnboardController
            or MavType.Gimbal
            or MavType.Adsb
            or MavType.Camera
            or MavType.ChargingStation
            or MavType.Flarm
            or MavType.Servo
            or MavType.Odid
            or MavType.Battery
            or MavType.Parachute
            or MavType.Log
            or MavType.Osd
            or MavType.Imu
            or MavType.Gps
            or MavType.Winch;

    private bool TryApplyTelemetryFrame(MavlinkFrame frame, DateTimeOffset now, out TelemetrySnapshot updated)
    {
        switch (frame.MessageId)
        {
            case SysStatusMessage.MsgId:
            {
                var msg = SysStatusMessage.Decode(frame);
                _current = _current with
                {
                    BatteryVoltageVolts = msg.VoltageBattery != ushort.MaxValue
                        ? msg.VoltageBattery / 1000.0
                        : _current.BatteryVoltageVolts,
                    BatteryCurrentAmps = msg.CurrentBattery >= 0
                        ? msg.CurrentBattery / 100.0
                        : _current.BatteryCurrentAmps,
                    BatteryRemainingPercent = msg.BatteryRemaining >= 0
                        ? msg.BatteryRemaining
                        : _current.BatteryRemainingPercent,
                    CpuLoadPercent = msg.Load != ushort.MaxValue
                        ? msg.Load / 10.0
                        : _current.CpuLoadPercent,
                    DropRateCommPercent = msg.DropRateComm != ushort.MaxValue
                        ? msg.DropRateComm / 100.0
                        : _current.DropRateCommPercent,
                    SensorHealthBits = msg.OnboardControlSensorsHealth,
                    UpdatedUtc = now
                };
                updated = _current;
                return true;
            }

            case BatteryStatusMessage.MsgId:
            {
                var msg = BatteryStatusMessage.Decode(frame);
                if (msg.Id != 0)
                {
                    updated = _current;
                    return false;
                }

                double? cellPackVolts = ComputeBatteryPackVolts(msg);
                _current = _current with
                {
                    BatteryVoltageVolts = cellPackVolts ?? _current.BatteryVoltageVolts,
                    BatteryCurrentAmps = msg.CurrentBattery >= 0
                        ? msg.CurrentBattery / 100.0
                        : _current.BatteryCurrentAmps,
                    BatteryRemainingPercent = msg.BatteryRemaining >= 0
                        ? msg.BatteryRemaining
                        : _current.BatteryRemainingPercent,
                    BatteryCurrentConsumedMah = msg.CurrentConsumed >= 0
                        ? msg.CurrentConsumed
                        : _current.BatteryCurrentConsumedMah,
                    BatteryTemperatureCelsius = msg.Temperature != short.MaxValue
                        ? msg.Temperature / 100.0
                        : _current.BatteryTemperatureCelsius,
                    UpdatedUtc = now
                };
                updated = _current;
                return true;
            }

            case GpsRawIntMessage.MsgId:
            {
                var msg = GpsRawIntMessage.Decode(frame);
                _current = _current with
                {
                    GpsFixType = msg.FixType,
                    SatellitesVisible = msg.SatellitesVisible != byte.MaxValue
                        ? msg.SatellitesVisible
                        : _current.SatellitesVisible,
                    Hdop = msg.Eph != ushort.MaxValue
                        ? msg.Eph / 100.0
                        : _current.Hdop,
                    UpdatedUtc = now
                };
                updated = _current;
                return true;
            }

            case AttitudeMessage.MsgId:
            {
                var msg = AttitudeMessage.Decode(frame);
                _current = _current with
                {
                    RollDeg = msg.Roll * RadToDeg,
                    PitchDeg = msg.Pitch * RadToDeg,
                    YawDeg = msg.Yaw * RadToDeg,
                    UpdatedUtc = now
                };
                updated = _current;
                return true;
            }

            case GlobalPositionIntMessage.MsgId:
            {
                var msg = GlobalPositionIntMessage.Decode(frame);
                _hasGlobalPosition = true;
                _current = _current with
                {
                    Latitude = msg.Lat / 1e7,
                    Longitude = msg.Lon / 1e7,
                    AltitudeAmslMeters = msg.Alt / 1000.0,
                    AltitudeRelativeMeters = msg.RelativeAlt / 1000.0,
                    HeadingDeg = msg.Hdg != ushort.MaxValue
                        ? (msg.Hdg / 100.0) % 360.0
                        : _current.HeadingDeg,
                    GroundspeedMps = !_hasVfrHud
                        ? Math.Sqrt((double)msg.Vx * msg.Vx + (double)msg.Vy * msg.Vy) / 100.0
                        : _current.GroundspeedMps,
                    VerticalSpeedMps = !_hasVfrHud
                        ? -msg.Vz / 100.0
                        : _current.VerticalSpeedMps,
                    UpdatedUtc = now
                };
                updated = _current;
                return true;
            }

            case VfrHudMessage.MsgId:
            {
                var msg = VfrHudMessage.Decode(frame);
                _hasVfrHud = true;
                _current = _current with
                {
                    AirspeedMps = msg.Airspeed,
                    GroundspeedMps = msg.Groundspeed,
                    VerticalSpeedMps = msg.Climb,
                    ThrottlePercent = msg.Throttle,
                    HeadingDeg = msg.Heading >= 0 ? msg.Heading % 360.0 : _current.HeadingDeg,
                    AltitudeAmslMeters = !_hasGlobalPosition ? msg.Alt : _current.AltitudeAmslMeters,
                    UpdatedUtc = now
                };
                updated = _current;
                return true;
            }

            case RadioStatusMessage.MsgId:
            {
                var msg = RadioStatusMessage.Decode(frame);
                _current = _current with
                {
                    RadioRssi = msg.Rssi,
                    RemoteRssi = msg.RemRssi,
                    UpdatedUtc = now
                };
                updated = _current;
                return true;
            }

            case HomePositionMessage.MsgId:
            {
                var msg = HomePositionMessage.Decode(frame);
                _current = _current with
                {
                    HomeLatitude = msg.Latitude / 1e7,
                    HomeLongitude = msg.Longitude / 1e7,
                    HomeAltitudeMeters = msg.Altitude / 1000.0,
                    HasHome = true,
                    UpdatedUtc = now
                };
                updated = _current;
                return true;
            }

            case MissionCurrentMessage.MsgId:
            {
                var msg = MissionCurrentMessage.Decode(frame);
                _current = _current with
                {
                    ActiveWaypointSeq = msg.Seq,
                    UpdatedUtc = now
                };
                updated = _current;
                return true;
            }

            case MissionItemReachedMessage.MsgId:
            {
                var msg = MissionItemReachedMessage.Decode(frame);
                _current = _current with
                {
                    LastReachedWaypointSeq = msg.Seq,
                    UpdatedUtc = now
                };
                updated = _current;
                return true;
            }

            case RcChannelsMessage.MsgId:
            {
                var msg = RcChannelsMessage.Decode(frame);
                ushort[] ch = msg.Channels;
                _current = _current with
                {
                    RcChannelCount = msg.ChanCount,
                    RcCh1 = ch is { Length: > 0 } ? ch[0] : (ushort)0,
                    RcCh2 = ch is { Length: > 1 } ? ch[1] : (ushort)0,
                    RcCh3 = ch is { Length: > 2 } ? ch[2] : (ushort)0,
                    RcCh4 = ch is { Length: > 3 } ? ch[3] : (ushort)0,
                    RcCh5 = ch is { Length: > 4 } ? ch[4] : (ushort)0,
                    RcCh6 = ch is { Length: > 5 } ? ch[5] : (ushort)0,
                    RcCh7 = ch is { Length: > 6 } ? ch[6] : (ushort)0,
                    RcCh8 = ch is { Length: > 7 } ? ch[7] : (ushort)0,
                    UpdatedUtc = now
                };
                updated = _current;
                return true;
            }

            case EkfStatusReportMessage.MsgId:
            {
                var msg = EkfStatusReportMessage.Decode(frame);
                _current = _current with
                {
                    EkfFlags = (ushort)msg.Flags,
                    UpdatedUtc = now
                };
                updated = _current;
                return true;
            }

            case VibrationMessage.MsgId:
            {
                var msg = VibrationMessage.Decode(frame);
                _current = _current with
                {
                    VibrationX = msg.VibrationX,
                    VibrationY = msg.VibrationY,
                    VibrationZ = msg.VibrationZ,
                    UpdatedUtc = now
                };
                updated = _current;
                return true;
            }

            case ExtendedSysStateMessage.MsgId:
            {
                var msg = ExtendedSysStateMessage.Decode(frame);
                _current = _current with
                {
                    VtolState = msg.VtolState,
                    LandedState = msg.LandedState,
                    UpdatedUtc = now
                };
                updated = _current;
                return true;
            }

            default:
                updated = _current;
                return false;
        }
    }

    private static double? ComputeBatteryPackVolts(BatteryStatusMessage msg)
    {
        long totalMillivolts = 0;
        foreach (ushort cellMv in msg.Voltages)
        {
            if (cellMv > 0 && cellMv != ushort.MaxValue)
            {
                totalMillivolts += cellMv;
            }
        }

        if (msg.VoltagesExt is not null)
        {
            foreach (ushort cellMv in msg.VoltagesExt)
            {
                if (cellMv > 0 && cellMv != ushort.MaxValue)
                {
                    totalMillivolts += cellMv;
                }
            }
        }

        return totalMillivolts > 0 ? totalMillivolts / 1000.0 : null;
    }

    private void DispatchEvents(List<BusEvent> events, long epoch)
    {
        foreach (BusEvent item in events)
        {
            if (Interlocked.Read(ref _sessionEpoch) != epoch)
            {
                return;
            }

            switch (item.Kind)
            {
                case BusEventKind.RawFrame:
                    Bus.PublishRawFrame(item.Frame!);
                    break;
                case BusEventKind.Snapshot:
                    Bus.PublishSnapshot(item.SnapshotValue!);
                    break;
                case BusEventKind.StatusText:
                    Bus.PublishStatusText(item.StatusValue!);
                    break;
                case BusEventKind.CommandAck:
                    Bus.PublishCommandAck(item.Frame!);
                    break;
            }
        }
    }

    private enum BusEventKind
    {
        RawFrame,
        Snapshot,
        StatusText,
        CommandAck
    }

    private readonly record struct BusEvent(
        BusEventKind Kind,
        MavlinkFrame? Frame = null,
        TelemetrySnapshot? SnapshotValue = null,
        StatusTextMessage? StatusValue = null)
    {
        public static BusEvent Raw(MavlinkFrame frame) => new(BusEventKind.RawFrame, Frame: frame);
        public static BusEvent Snapshot(TelemetrySnapshot snapshot) => new(BusEventKind.Snapshot, SnapshotValue: snapshot);
        public static BusEvent Status(StatusTextMessage status) => new(BusEventKind.StatusText, StatusValue: status);
        public static BusEvent Ack(MavlinkFrame frame) => new(BusEventKind.CommandAck, Frame: frame);
    }
}
