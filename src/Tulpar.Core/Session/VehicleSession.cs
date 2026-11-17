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
    /// Feeds raw transport bytes through the MAVLink parser and updates telemetry state.
    /// </summary>
    public void ProcessIncomingBytes(ReadOnlySpan<byte> data)
    {
        List<BusEvent>? pendingEvents = null;

        lock (_sync)
        {
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

                if (frame.SystemId == GcsSystemId)
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

                // Ignore telemetry from unrelated system IDs once a vehicle is identified.
                if (_current.SystemId != 0 && frame.SystemId != _current.SystemId)
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
            DispatchEvents(pendingEvents);
        }
    }

    /// <summary>
    /// Checks whether more than 3 seconds have elapsed since the last vehicle heartbeat.
    /// </summary>
    public void CheckHeartbeatTimeout(DateTimeOffset now)
    {
        TelemetrySnapshot? snapshotToEmit = null;

        lock (_sync)
        {
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

        if (snapshotToEmit is not null)
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
            _parser.Reset();
            _hasVfrHud = false;
            _hasGlobalPosition = false;
            _activeProfile = null;
            _current = TelemetrySnapshot.Empty with { UpdatedUtc = _timeProvider.GetUtcNow() };
            snapshotToEmit = _current;
        }

        Bus.PublishSnapshot(snapshotToEmit);
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
            _hasVfrHud = false;
            _hasGlobalPosition = false;
            _current = TelemetrySnapshot.Empty;
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
            ComponentId = frame.ComponentId,
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
        if (hb.Type == MavType.Gcs || frame.SystemId == GcsSystemId)
        {
            return false;
        }

        if (IsAuxiliaryComponentType(hb.Type) && hb.Autopilot == MavAutopilot.Invalid)
        {
            return false;
        }

        // Do not let non-autopilot components on the same vehicle overwrite the active flight controller.
        if (_current.SystemId == frame.SystemId &&
            _current.ComponentId == (byte)MavComponent.Autopilot1 &&
            frame.ComponentId != (byte)MavComponent.Autopilot1 &&
            hb.Autopilot == MavAutopilot.Invalid)
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
                    UpdatedUtc = now
                };
                updated = _current;
                return true;
            }

            case BatteryStatusMessage.MsgId:
            {
                var msg = BatteryStatusMessage.Decode(frame);
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

    private void DispatchEvents(List<BusEvent> events)
    {
        foreach (BusEvent item in events)
        {
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
