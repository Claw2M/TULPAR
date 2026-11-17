using System.Buffers.Binary;
using System.Text;

namespace Tulpar.Core.Mavlink;

/// <summary>
/// Common contract implemented by all strongly-typed MAVLink message payloads.
/// </summary>
public interface IMavlinkMessage
{
    uint MessageId { get; }
    byte CrcExtra { get; }
    byte[] EncodePayload();
}

internal static class MavlinkWire
{
    public static void ZeroPad(ReadOnlySpan<byte> payload, Span<byte> destination)
    {
        destination.Clear();
        int copyLength = Math.Min(payload.Length, destination.Length);
        payload[..copyLength].CopyTo(destination);
    }

    public static void EnsureMessageId(MavlinkFrame frame, uint expectedMessageId)
    {
        if (frame.MessageId != expectedMessageId)
        {
            throw new ArgumentException(
                $"Expected MAVLink message ID {expectedMessageId}, but got {frame.MessageId}.",
                nameof(frame));
        }
    }

    public static T[] NormalizeArray<T>(T[]? source, int fixedLength)
    {
        T[] result = new T[fixedLength];
        if (source is not null)
        {
            Array.Copy(source, result, Math.Min(source.Length, fixedLength));
        }

        return result;
    }

    public static int ArrayHashCode<T>(T[]? values, int fixedLength) where T : notnull
    {
        var hash = new HashCode();
        for (int i = 0; i < fixedLength; i++)
        {
            hash.Add(values is not null && i < values.Length ? values[i] : default!);
        }

        return hash.ToHashCode();
    }

    public static bool ArraysEqual<T>(T[]? left, T[]? right, int fixedLength) where T : IEquatable<T>
    {
        for (int i = 0; i < fixedLength; i++)
        {
            T a = left is not null && i < left.Length ? left[i] : default!;
            T b = right is not null && i < right.Length ? right[i] : default!;
            if (!a.Equals(b))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Extension helpers for wrapping strongly-typed messages in <see cref="MavlinkFrame"/>.
/// </summary>
public static class MavlinkMessageExtensions
{
    public static MavlinkFrame ToFrame(
        this IMavlinkMessage message,
        byte systemId,
        byte componentId,
        byte sequence,
        byte version = 2) =>
        new(version, sequence, systemId, componentId, message.MessageId, message.EncodePayload());
}

/// <summary>
/// HEARTBEAT (#0, CRC_EXTRA=50, 9 bytes).
/// </summary>
public sealed record HeartbeatMessage(
    MavType Type,
    MavAutopilot Autopilot,
    MavModeFlag BaseMode,
    uint CustomMode,
    MavState SystemStatus,
    byte MavlinkVersion = 3) : IMavlinkMessage
{
    public const uint MsgId = 0;
    public const byte MsgCrcExtra = 50;
    public const int WireSize = 9;

    public uint MessageId => MsgId;
    public byte CrcExtra => MsgCrcExtra;

    public byte[] EncodePayload()
    {
        byte[] buf = new byte[WireSize];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), CustomMode);
        buf[4] = (byte)Type;
        buf[5] = (byte)Autopilot;
        buf[6] = (byte)BaseMode;
        buf[7] = (byte)SystemStatus;
        buf[8] = MavlinkVersion;
        return buf;
    }

    public static HeartbeatMessage Decode(MavlinkFrame frame)
    {
        MavlinkWire.EnsureMessageId(frame, MsgId);
        return Decode(frame.Payload.Span);
    }

    public static HeartbeatMessage Decode(ReadOnlySpan<byte> payload)
    {
        Span<byte> buf = stackalloc byte[WireSize];
        MavlinkWire.ZeroPad(payload, buf);
        return new HeartbeatMessage(
            Type: (MavType)buf[4],
            Autopilot: (MavAutopilot)buf[5],
            BaseMode: (MavModeFlag)buf[6],
            CustomMode: BinaryPrimitives.ReadUInt32LittleEndian(buf[0..4]),
            SystemStatus: (MavState)buf[7],
            MavlinkVersion: buf[8]);
    }
}

/// <summary>
/// SYS_STATUS (#1, CRC_EXTRA=124, 31 base + 12 extension = 43 bytes).
/// </summary>
public sealed record SysStatusMessage(
    uint OnboardControlSensorsPresent,
    uint OnboardControlSensorsEnabled,
    uint OnboardControlSensorsHealth,
    ushort Load,
    ushort VoltageBattery,
    short CurrentBattery,
    sbyte BatteryRemaining,
    ushort DropRateComm = 0,
    ushort ErrorsComm = 0,
    ushort ErrorsCount1 = 0,
    ushort ErrorsCount2 = 0,
    ushort ErrorsCount3 = 0,
    ushort ErrorsCount4 = 0,
    uint OnboardControlSensorsPresentExtended = 0,
    uint OnboardControlSensorsEnabledExtended = 0,
    uint OnboardControlSensorsHealthExtended = 0) : IMavlinkMessage
{
    public const uint MsgId = 1;
    public const byte MsgCrcExtra = 124;
    public const int WireSize = 43;

    public uint MessageId => MsgId;
    public byte CrcExtra => MsgCrcExtra;

    public byte[] EncodePayload()
    {
        byte[] buf = new byte[WireSize];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), OnboardControlSensorsPresent);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4, 4), OnboardControlSensorsEnabled);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8, 4), OnboardControlSensorsHealth);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(12, 2), Load);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(14, 2), VoltageBattery);
        BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(16, 2), CurrentBattery);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(18, 2), DropRateComm);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(20, 2), ErrorsComm);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(22, 2), ErrorsCount1);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(24, 2), ErrorsCount2);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(26, 2), ErrorsCount3);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(28, 2), ErrorsCount4);
        buf[30] = unchecked((byte)BatteryRemaining);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(31, 4), OnboardControlSensorsPresentExtended);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(35, 4), OnboardControlSensorsEnabledExtended);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(39, 4), OnboardControlSensorsHealthExtended);
        return buf;
    }

    public static SysStatusMessage Decode(MavlinkFrame frame)
    {
        MavlinkWire.EnsureMessageId(frame, MsgId);
        return Decode(frame.Payload.Span);
    }

    public static SysStatusMessage Decode(ReadOnlySpan<byte> payload)
    {
        Span<byte> buf = stackalloc byte[WireSize];
        MavlinkWire.ZeroPad(payload, buf);
        return new SysStatusMessage(
            OnboardControlSensorsPresent: BinaryPrimitives.ReadUInt32LittleEndian(buf[0..4]),
            OnboardControlSensorsEnabled: BinaryPrimitives.ReadUInt32LittleEndian(buf[4..8]),
            OnboardControlSensorsHealth: BinaryPrimitives.ReadUInt32LittleEndian(buf[8..12]),
            Load: BinaryPrimitives.ReadUInt16LittleEndian(buf[12..14]),
            VoltageBattery: BinaryPrimitives.ReadUInt16LittleEndian(buf[14..16]),
            CurrentBattery: BinaryPrimitives.ReadInt16LittleEndian(buf[16..18]),
            BatteryRemaining: unchecked((sbyte)buf[30]),
            DropRateComm: BinaryPrimitives.ReadUInt16LittleEndian(buf[18..20]),
            ErrorsComm: BinaryPrimitives.ReadUInt16LittleEndian(buf[20..22]),
            ErrorsCount1: BinaryPrimitives.ReadUInt16LittleEndian(buf[22..24]),
            ErrorsCount2: BinaryPrimitives.ReadUInt16LittleEndian(buf[24..26]),
            ErrorsCount3: BinaryPrimitives.ReadUInt16LittleEndian(buf[26..28]),
            ErrorsCount4: BinaryPrimitives.ReadUInt16LittleEndian(buf[28..30]),
            OnboardControlSensorsPresentExtended: BinaryPrimitives.ReadUInt32LittleEndian(buf[31..35]),
            OnboardControlSensorsEnabledExtended: BinaryPrimitives.ReadUInt32LittleEndian(buf[35..39]),
            OnboardControlSensorsHealthExtended: BinaryPrimitives.ReadUInt32LittleEndian(buf[39..43]));
    }
}

/// <summary>
/// SYSTEM_TIME (#2, CRC_EXTRA=137, 12 bytes).
/// </summary>
public sealed record SystemTimeMessage(ulong TimeUnixUsec, uint TimeBootMs) : IMavlinkMessage
{
    public const uint MsgId = 2;
    public const byte MsgCrcExtra = 137;
    public const int WireSize = 12;

    public uint MessageId => MsgId;
    public byte CrcExtra => MsgCrcExtra;

    public byte[] EncodePayload()
    {
        byte[] buf = new byte[WireSize];
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(0, 8), TimeUnixUsec);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8, 4), TimeBootMs);
        return buf;
    }

    public static SystemTimeMessage Decode(MavlinkFrame frame)
    {
        MavlinkWire.EnsureMessageId(frame, MsgId);
        return Decode(frame.Payload.Span);
    }

    public static SystemTimeMessage Decode(ReadOnlySpan<byte> payload)
    {
        Span<byte> buf = stackalloc byte[WireSize];
        MavlinkWire.ZeroPad(payload, buf);
        return new SystemTimeMessage(
            TimeUnixUsec: BinaryPrimitives.ReadUInt64LittleEndian(buf[0..8]),
            TimeBootMs: BinaryPrimitives.ReadUInt32LittleEndian(buf[8..12]));
    }
}

/// <summary>
/// SET_MODE (#11, CRC_EXTRA=89, 6 bytes).
/// </summary>
public sealed record SetModeMessage(byte TargetSystem, MavModeFlag BaseMode, uint CustomMode) : IMavlinkMessage
{
    public const uint MsgId = 11;
    public const byte MsgCrcExtra = 89;
    public const int WireSize = 6;

    public uint MessageId => MsgId;
    public byte CrcExtra => MsgCrcExtra;

    public byte[] EncodePayload()
    {
        byte[] buf = new byte[WireSize];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), CustomMode);
        buf[4] = TargetSystem;
        buf[5] = (byte)BaseMode;
        return buf;
    }

    public static SetModeMessage Decode(MavlinkFrame frame)
    {
        MavlinkWire.EnsureMessageId(frame, MsgId);
        return Decode(frame.Payload.Span);
    }

    public static SetModeMessage Decode(ReadOnlySpan<byte> payload)
    {
        Span<byte> buf = stackalloc byte[WireSize];
        MavlinkWire.ZeroPad(payload, buf);
        return new SetModeMessage(
            TargetSystem: buf[4],
            BaseMode: (MavModeFlag)buf[5],
            CustomMode: BinaryPrimitives.ReadUInt32LittleEndian(buf[0..4]));
    }
}

/// <summary>
/// GPS_RAW_INT (#24, CRC_EXTRA=24, 30 base + 22 extension = 52 bytes).
/// </summary>
public sealed record GpsRawIntMessage(
    ulong TimeUsec,
    GpsFixType FixType,
    int Lat,
    int Lon,
    int Alt,
    ushort Eph,
    ushort Epv,
    ushort Vel,
    ushort Cog,
    byte SatellitesVisible,
    int AltEllipsoid = 0,
    uint HAcc = 0,
    uint VAcc = 0,
    uint VelAcc = 0,
    uint HdgAcc = 0,
    ushort Yaw = 0) : IMavlinkMessage
{
    public const uint MsgId = 24;
    public const byte MsgCrcExtra = 24;
    public const int WireSize = 52;

    public uint MessageId => MsgId;
    public byte CrcExtra => MsgCrcExtra;

    public byte[] EncodePayload()
    {
        byte[] buf = new byte[WireSize];
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(0, 8), TimeUsec);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8, 4), Lat);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(12, 4), Lon);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(16, 4), Alt);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(20, 2), Eph);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(22, 2), Epv);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(24, 2), Vel);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(26, 2), Cog);
        buf[28] = (byte)FixType;
        buf[29] = SatellitesVisible;
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(30, 4), AltEllipsoid);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(34, 4), HAcc);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(38, 4), VAcc);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(42, 4), VelAcc);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(46, 4), HdgAcc);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(50, 2), Yaw);
        return buf;
    }

    public static GpsRawIntMessage Decode(MavlinkFrame frame)
    {
        MavlinkWire.EnsureMessageId(frame, MsgId);
        return Decode(frame.Payload.Span);
    }

    public static GpsRawIntMessage Decode(ReadOnlySpan<byte> payload)
    {
        Span<byte> buf = stackalloc byte[WireSize];
        MavlinkWire.ZeroPad(payload, buf);
        return new GpsRawIntMessage(
            TimeUsec: BinaryPrimitives.ReadUInt64LittleEndian(buf[0..8]),
            FixType: (GpsFixType)buf[28],
            Lat: BinaryPrimitives.ReadInt32LittleEndian(buf[8..12]),
            Lon: BinaryPrimitives.ReadInt32LittleEndian(buf[12..16]),
            Alt: BinaryPrimitives.ReadInt32LittleEndian(buf[16..20]),
            Eph: BinaryPrimitives.ReadUInt16LittleEndian(buf[20..22]),
            Epv: BinaryPrimitives.ReadUInt16LittleEndian(buf[22..24]),
            Vel: BinaryPrimitives.ReadUInt16LittleEndian(buf[24..26]),
            Cog: BinaryPrimitives.ReadUInt16LittleEndian(buf[26..28]),
            SatellitesVisible: buf[29],
            AltEllipsoid: BinaryPrimitives.ReadInt32LittleEndian(buf[30..34]),
            HAcc: BinaryPrimitives.ReadUInt32LittleEndian(buf[34..38]),
            VAcc: BinaryPrimitives.ReadUInt32LittleEndian(buf[38..42]),
            VelAcc: BinaryPrimitives.ReadUInt32LittleEndian(buf[42..46]),
            HdgAcc: BinaryPrimitives.ReadUInt32LittleEndian(buf[46..50]),
            Yaw: BinaryPrimitives.ReadUInt16LittleEndian(buf[50..52]));
    }
}

/// <summary>
/// ATTITUDE (#30, CRC_EXTRA=39, 28 bytes).
/// </summary>
public sealed record AttitudeMessage(
    uint TimeBootMs,
    float Roll,
    float Pitch,
    float Yaw,
    float Rollspeed = 0f,
    float Pitchspeed = 0f,
    float Yawspeed = 0f) : IMavlinkMessage
{
    public const uint MsgId = 30;
    public const byte MsgCrcExtra = 39;
    public const int WireSize = 28;

    public uint MessageId => MsgId;
    public byte CrcExtra => MsgCrcExtra;

    public byte[] EncodePayload()
    {
        byte[] buf = new byte[WireSize];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), TimeBootMs);
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(4, 4), Roll);
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(8, 4), Pitch);
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(12, 4), Yaw);
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(16, 4), Rollspeed);
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(20, 4), Pitchspeed);
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(24, 4), Yawspeed);
        return buf;
    }

    public static AttitudeMessage Decode(MavlinkFrame frame)
    {
        MavlinkWire.EnsureMessageId(frame, MsgId);
        return Decode(frame.Payload.Span);
    }

    public static AttitudeMessage Decode(ReadOnlySpan<byte> payload)
    {
        Span<byte> buf = stackalloc byte[WireSize];
        MavlinkWire.ZeroPad(payload, buf);
        return new AttitudeMessage(
            TimeBootMs: BinaryPrimitives.ReadUInt32LittleEndian(buf[0..4]),
            Roll: BinaryPrimitives.ReadSingleLittleEndian(buf[4..8]),
            Pitch: BinaryPrimitives.ReadSingleLittleEndian(buf[8..12]),
            Yaw: BinaryPrimitives.ReadSingleLittleEndian(buf[12..16]),
            Rollspeed: BinaryPrimitives.ReadSingleLittleEndian(buf[16..20]),
            Pitchspeed: BinaryPrimitives.ReadSingleLittleEndian(buf[20..24]),
            Yawspeed: BinaryPrimitives.ReadSingleLittleEndian(buf[24..28]));
    }
}

/// <summary>
/// GLOBAL_POSITION_INT (#33, CRC_EXTRA=104, 28 bytes).
/// </summary>
public sealed record GlobalPositionIntMessage(
    uint TimeBootMs,
    int Lat,
    int Lon,
    int Alt,
    int RelativeAlt,
    short Vx = 0,
    short Vy = 0,
    short Vz = 0,
    ushort Hdg = ushort.MaxValue) : IMavlinkMessage
{
    public const uint MsgId = 33;
    public const byte MsgCrcExtra = 104;
    public const int WireSize = 28;

    public uint MessageId => MsgId;
    public byte CrcExtra => MsgCrcExtra;

    public byte[] EncodePayload()
    {
        byte[] buf = new byte[WireSize];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), TimeBootMs);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), Lat);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8, 4), Lon);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(12, 4), Alt);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(16, 4), RelativeAlt);
        BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(20, 2), Vx);
        BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(22, 2), Vy);
        BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(24, 2), Vz);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(26, 2), Hdg);
        return buf;
    }

    public static GlobalPositionIntMessage Decode(MavlinkFrame frame)
    {
        MavlinkWire.EnsureMessageId(frame, MsgId);
        return Decode(frame.Payload.Span);
    }

    public static GlobalPositionIntMessage Decode(ReadOnlySpan<byte> payload)
    {
        Span<byte> buf = stackalloc byte[WireSize];
        MavlinkWire.ZeroPad(payload, buf);
        return new GlobalPositionIntMessage(
            TimeBootMs: BinaryPrimitives.ReadUInt32LittleEndian(buf[0..4]),
            Lat: BinaryPrimitives.ReadInt32LittleEndian(buf[4..8]),
            Lon: BinaryPrimitives.ReadInt32LittleEndian(buf[8..12]),
            Alt: BinaryPrimitives.ReadInt32LittleEndian(buf[12..16]),
            RelativeAlt: BinaryPrimitives.ReadInt32LittleEndian(buf[16..20]),
            Vx: BinaryPrimitives.ReadInt16LittleEndian(buf[20..22]),
            Vy: BinaryPrimitives.ReadInt16LittleEndian(buf[22..24]),
            Vz: BinaryPrimitives.ReadInt16LittleEndian(buf[24..26]),
            Hdg: BinaryPrimitives.ReadUInt16LittleEndian(buf[26..28]));
    }
}

/// <summary>
/// MISSION_ITEM (#39, CRC_EXTRA=254, 37 base + 1 extension = 38 bytes).
/// </summary>
public sealed record MissionItemMessage(
    byte TargetSystem,
    byte TargetComponent,
    ushort Seq,
    MavFrame Frame,
    MavCmd Command,
    byte Current,
    byte Autocontinue,
    float Param1,
    float Param2,
    float Param3,
    float Param4,
    float X,
    float Y,
    float Z,
    MavMissionType MissionType = MavMissionType.Mission) : IMavlinkMessage
{
    public const uint MsgId = 39;
    public const byte MsgCrcExtra = 254;
    public const int WireSize = 38;

    public uint MessageId => MsgId;
    public byte CrcExtra => MsgCrcExtra;

    public byte[] EncodePayload()
    {
        byte[] buf = new byte[WireSize];
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(0, 4), Param1);
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(4, 4), Param2);
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(8, 4), Param3);
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(12, 4), Param4);
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(16, 4), X);
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(20, 4), Y);
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(24, 4), Z);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(28, 2), Seq);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(30, 2), (ushort)Command);
        buf[32] = TargetSystem;
        buf[33] = TargetComponent;
        buf[34] = (byte)Frame;
        buf[35] = Current;
        buf[36] = Autocontinue;
        buf[37] = (byte)MissionType;
        return buf;
    }

    public static MissionItemMessage Decode(MavlinkFrame frame)
    {
        MavlinkWire.EnsureMessageId(frame, MsgId);
        return Decode(frame.Payload.Span);
    }

    public static MissionItemMessage Decode(ReadOnlySpan<byte> payload)
    {
        Span<byte> buf = stackalloc byte[WireSize];
        MavlinkWire.ZeroPad(payload, buf);
        return new MissionItemMessage(
            TargetSystem: buf[32],
            TargetComponent: buf[33],
            Seq: BinaryPrimitives.ReadUInt16LittleEndian(buf[28..30]),
            Frame: (MavFrame)buf[34],
            Command: (MavCmd)BinaryPrimitives.ReadUInt16LittleEndian(buf[30..32]),
            Current: buf[35],
            Autocontinue: buf[36],
            Param1: BinaryPrimitives.ReadSingleLittleEndian(buf[0..4]),
            Param2: BinaryPrimitives.ReadSingleLittleEndian(buf[4..8]),
            Param3: BinaryPrimitives.ReadSingleLittleEndian(buf[8..12]),
            Param4: BinaryPrimitives.ReadSingleLittleEndian(buf[12..16]),
            X: BinaryPrimitives.ReadSingleLittleEndian(buf[16..20]),
            Y: BinaryPrimitives.ReadSingleLittleEndian(buf[20..24]),
            Z: BinaryPrimitives.ReadSingleLittleEndian(buf[24..28]),
            MissionType: (MavMissionType)buf[37]);
    }
}

/// <summary>
/// MISSION_REQUEST (#40, CRC_EXTRA=230, 4 base + 1 extension = 5 bytes).
/// </summary>
public sealed record MissionRequestMessage(
    byte TargetSystem,
    byte TargetComponent,
    ushort Seq,
    MavMissionType MissionType = MavMissionType.Mission) : IMavlinkMessage
{
    public const uint MsgId = 40;
    public const byte MsgCrcExtra = 230;
    public const int WireSize = 5;

    public uint MessageId => MsgId;
    public byte CrcExtra => MsgCrcExtra;

    public byte[] EncodePayload()
    {
        byte[] buf = new byte[WireSize];
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0, 2), Seq);
        buf[2] = TargetSystem;
        buf[3] = TargetComponent;
        buf[4] = (byte)MissionType;
        return buf;
    }

    public static MissionRequestMessage Decode(MavlinkFrame frame)
    {
        MavlinkWire.EnsureMessageId(frame, MsgId);
        return Decode(frame.Payload.Span);
    }

    public static MissionRequestMessage Decode(ReadOnlySpan<byte> payload)
    {
        Span<byte> buf = stackalloc byte[WireSize];
        MavlinkWire.ZeroPad(payload, buf);
        return new MissionRequestMessage(
            TargetSystem: buf[2],
            TargetComponent: buf[3],
            Seq: BinaryPrimitives.ReadUInt16LittleEndian(buf[0..2]),
            MissionType: (MavMissionType)buf[4]);
    }
}

/// <summary>
/// MISSION_SET_CURRENT (#41, CRC_EXTRA=28, 4 bytes).
/// </summary>
public sealed record MissionSetCurrentMessage(
    byte TargetSystem,
    byte TargetComponent,
    ushort Seq) : IMavlinkMessage
{
    public const uint MsgId = 41;
    public const byte MsgCrcExtra = 28;
    public const int WireSize = 4;

    public uint MessageId => MsgId;
    public byte CrcExtra => MsgCrcExtra;

    public byte[] EncodePayload()
    {
        byte[] buf = new byte[WireSize];
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0, 2), Seq);
        buf[2] = TargetSystem;
        buf[3] = TargetComponent;
        return buf;
    }

    public static MissionSetCurrentMessage Decode(MavlinkFrame frame)
    {
        MavlinkWire.EnsureMessageId(frame, MsgId);
        return Decode(frame.Payload.Span);
    }

    public static MissionSetCurrentMessage Decode(ReadOnlySpan<byte> payload)
    {
        Span<byte> buf = stackalloc byte[WireSize];
        MavlinkWire.ZeroPad(payload, buf);
        return new MissionSetCurrentMessage(
            TargetSystem: buf[2],
            TargetComponent: buf[3],
            Seq: BinaryPrimitives.ReadUInt16LittleEndian(buf[0..2]));
    }
}

/// <summary>
/// MISSION_CURRENT (#42, CRC_EXTRA=28, 2 base + 16 extension = 18 bytes).
/// </summary>
public sealed record MissionCurrentMessage(
    ushort Seq,
    ushort Total = 0,
    byte MissionState = 0,
    byte MissionMode = 0,
    uint MissionId = 0,
    uint FenceId = 0,
    uint RallyPointsId = 0) : IMavlinkMessage
{
    public const uint MsgId = 42;
    public const byte MsgCrcExtra = 28;
    public const int WireSize = 18;

    public uint MessageId => MsgId;
    public byte CrcExtra => MsgCrcExtra;

    public byte[] EncodePayload()
    {
        byte[] buf = new byte[WireSize];
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0, 2), Seq);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2, 2), Total);
        buf[4] = MissionState;
        buf[5] = MissionMode;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(6, 4), MissionId);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(10, 4), FenceId);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(14, 4), RallyPointsId);
        return buf;
    }

    public static MissionCurrentMessage Decode(MavlinkFrame frame)
    {
        MavlinkWire.EnsureMessageId(frame, MsgId);
        return Decode(frame.Payload.Span);
    }

    public static MissionCurrentMessage Decode(ReadOnlySpan<byte> payload)
    {
        Span<byte> buf = stackalloc byte[WireSize];
        MavlinkWire.ZeroPad(payload, buf);
        return new MissionCurrentMessage(
            Seq: BinaryPrimitives.ReadUInt16LittleEndian(buf[0..2]),
            Total: BinaryPrimitives.ReadUInt16LittleEndian(buf[2..4]),
            MissionState: buf[4],
            MissionMode: buf[5],
            MissionId: BinaryPrimitives.ReadUInt32LittleEndian(buf[6..10]),
            FenceId: BinaryPrimitives.ReadUInt32LittleEndian(buf[10..14]),
            RallyPointsId: BinaryPrimitives.ReadUInt32LittleEndian(buf[14..18]));
    }
}

/// <summary>
/// MISSION_REQUEST_LIST (#43, CRC_EXTRA=132, 2 base + 1 extension = 3 bytes).
/// </summary>
public sealed record MissionRequestListMessage(
    byte TargetSystem,
    byte TargetComponent,
    MavMissionType MissionType = MavMissionType.Mission) : IMavlinkMessage
{
    public const uint MsgId = 43;
    public const byte MsgCrcExtra = 132;
    public const int WireSize = 3;

    public uint MessageId => MsgId;
    public byte CrcExtra => MsgCrcExtra;

    public byte[] EncodePayload()
    {
        return [TargetSystem, TargetComponent, (byte)MissionType];
    }

    public static MissionRequestListMessage Decode(MavlinkFrame frame)
    {
        MavlinkWire.EnsureMessageId(frame, MsgId);
        return Decode(frame.Payload.Span);
    }

    public static MissionRequestListMessage Decode(ReadOnlySpan<byte> payload)
    {
        Span<byte> buf = stackalloc byte[WireSize];
        MavlinkWire.ZeroPad(payload, buf);
        return new MissionRequestListMessage(buf[0], buf[1], (MavMissionType)buf[2]);
    }
}

/// <summary>
/// MISSION_COUNT (#44, CRC_EXTRA=221, 4 base + 5 extension = 9 bytes).
/// </summary>
public sealed record MissionCountMessage(
    byte TargetSystem,
    byte TargetComponent,
    ushort Count,
    MavMissionType MissionType = MavMissionType.Mission,
    uint OpaqueId = 0) : IMavlinkMessage
{
    public const uint MsgId = 44;
    public const byte MsgCrcExtra = 221;
    public const int WireSize = 9;

    public uint MessageId => MsgId;
    public byte CrcExtra => MsgCrcExtra;

    public byte[] EncodePayload()
    {
        byte[] buf = new byte[WireSize];
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0, 2), Count);
        buf[2] = TargetSystem;
        buf[3] = TargetComponent;
        buf[4] = (byte)MissionType;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(5, 4), OpaqueId);
        return buf;
    }

    public static MissionCountMessage Decode(MavlinkFrame frame)
    {
        MavlinkWire.EnsureMessageId(frame, MsgId);
        return Decode(frame.Payload.Span);
    }

    public static MissionCountMessage Decode(ReadOnlySpan<byte> payload)
    {
        Span<byte> buf = stackalloc byte[WireSize];
        MavlinkWire.ZeroPad(payload, buf);
        return new MissionCountMessage(
            TargetSystem: buf[2],
            TargetComponent: buf[3],
            Count: BinaryPrimitives.ReadUInt16LittleEndian(buf[0..2]),
            MissionType: (MavMissionType)buf[4],
            OpaqueId: BinaryPrimitives.ReadUInt32LittleEndian(buf[5..9]));
    }
}

/// <summary>
/// MISSION_CLEAR_ALL (#45, CRC_EXTRA=232, 2 base + 1 extension = 3 bytes).
/// </summary>
public sealed record MissionClearAllMessage(
    byte TargetSystem,
    byte TargetComponent,
    MavMissionType MissionType = MavMissionType.Mission) : IMavlinkMessage
{
    public const uint MsgId = 45;
    public const byte MsgCrcExtra = 232;
    public const int WireSize = 3;

    public uint MessageId => MsgId;
    public byte CrcExtra => MsgCrcExtra;

    public byte[] EncodePayload()
    {
        return [TargetSystem, TargetComponent, (byte)MissionType];
    }

    public static MissionClearAllMessage Decode(MavlinkFrame frame)
    {
        MavlinkWire.EnsureMessageId(frame, MsgId);
        return Decode(frame.Payload.Span);
    }

    public static MissionClearAllMessage Decode(ReadOnlySpan<byte> payload)
    {
        Span<byte> buf = stackalloc byte[WireSize];
        MavlinkWire.ZeroPad(payload, buf);
        return new MissionClearAllMessage(buf[0], buf[1], (MavMissionType)buf[2]);
    }
}

/// <summary>
/// MISSION_ITEM_REACHED (#46, CRC_EXTRA=11, 2 bytes).
/// </summary>
public sealed record MissionItemReachedMessage(ushort Seq) : IMavlinkMessage
{
    public const uint MsgId = 46;
    public const byte MsgCrcExtra = 11;
    public const int WireSize = 2;

    public uint MessageId => MsgId;
    public byte CrcExtra => MsgCrcExtra;

    public byte[] EncodePayload()
    {
        byte[] buf = new byte[WireSize];
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0, 2), Seq);
        return buf;
    }

    public static MissionItemReachedMessage Decode(MavlinkFrame frame)
    {
        MavlinkWire.EnsureMessageId(frame, MsgId);
        return Decode(frame.Payload.Span);
    }

    public static MissionItemReachedMessage Decode(ReadOnlySpan<byte> payload)
    {
        Span<byte> buf = stackalloc byte[WireSize];
        MavlinkWire.ZeroPad(payload, buf);
        return new MissionItemReachedMessage(BinaryPrimitives.ReadUInt16LittleEndian(buf[0..2]));
    }
}

/// <summary>
/// MISSION_ACK (#47, CRC_EXTRA=153, 3 base + 5 extension = 8 bytes).
/// </summary>
public sealed record MissionAckMessage(
    byte TargetSystem,
    byte TargetComponent,
    MavMissionResult Type,
    MavMissionType MissionType = MavMissionType.Mission,
    uint OpaqueId = 0) : IMavlinkMessage
{
    public const uint MsgId = 47;
    public const byte MsgCrcExtra = 153;
    public const int WireSize = 8;

    public uint MessageId => MsgId;
    public byte CrcExtra => MsgCrcExtra;

    public byte[] EncodePayload()
    {
        byte[] buf = new byte[WireSize];
        buf[0] = TargetSystem;
        buf[1] = TargetComponent;
        buf[2] = (byte)Type;
        buf[3] = (byte)MissionType;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4, 4), OpaqueId);
        return buf;
    }

    public static MissionAckMessage Decode(MavlinkFrame frame)
    {
        MavlinkWire.EnsureMessageId(frame, MsgId);
        return Decode(frame.Payload.Span);
    }

    public static MissionAckMessage Decode(ReadOnlySpan<byte> payload)
    {
        Span<byte> buf = stackalloc byte[WireSize];
        MavlinkWire.ZeroPad(payload, buf);
        return new MissionAckMessage(
            TargetSystem: buf[0],
            TargetComponent: buf[1],
            Type: (MavMissionResult)buf[2],
            MissionType: (MavMissionType)buf[3],
            OpaqueId: BinaryPrimitives.ReadUInt32LittleEndian(buf[4..8]));
    }
}

/// <summary>
/// MISSION_REQUEST_INT (#51, CRC_EXTRA=196, 4 base + 1 extension = 5 bytes).
/// </summary>
public sealed record MissionRequestIntMessage(
    byte TargetSystem,
    byte TargetComponent,
    ushort Seq,
    MavMissionType MissionType = MavMissionType.Mission) : IMavlinkMessage
{
    public const uint MsgId = 51;
    public const byte MsgCrcExtra = 196;
    public const int WireSize = 5;

    public uint MessageId => MsgId;
    public byte CrcExtra => MsgCrcExtra;

    public byte[] EncodePayload()
    {
        byte[] buf = new byte[WireSize];
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0, 2), Seq);
        buf[2] = TargetSystem;
        buf[3] = TargetComponent;
        buf[4] = (byte)MissionType;
        return buf;
    }

    public static MissionRequestIntMessage Decode(MavlinkFrame frame)
    {
        MavlinkWire.EnsureMessageId(frame, MsgId);
        return Decode(frame.Payload.Span);
    }

    public static MissionRequestIntMessage Decode(ReadOnlySpan<byte> payload)
    {
        Span<byte> buf = stackalloc byte[WireSize];
        MavlinkWire.ZeroPad(payload, buf);
        return new MissionRequestIntMessage(
            TargetSystem: buf[2],
            TargetComponent: buf[3],
            Seq: BinaryPrimitives.ReadUInt16LittleEndian(buf[0..2]),
            MissionType: (MavMissionType)buf[4]);
    }
}

/// <summary>
/// RC_CHANNELS (#65, CRC_EXTRA=118, 42 bytes).
/// </summary>
public sealed record RcChannelsMessage(
    uint TimeBootMs,
    byte ChanCount,
    ushort[] Channels,
    byte Rssi = byte.MaxValue) : IMavlinkMessage
{
    public const uint MsgId = 65;
    public const byte MsgCrcExtra = 118;
    public const int WireSize = 42;
    public const int ChannelArrayLength = 18;

    public uint MessageId => MsgId;
    public byte CrcExtra => MsgCrcExtra;

    public bool Equals(RcChannelsMessage? other) =>
        other is not null &&
        TimeBootMs == other.TimeBootMs &&
        ChanCount == other.ChanCount &&
        Rssi == other.Rssi &&
        MavlinkWire.ArraysEqual(Channels, other.Channels, ChannelArrayLength);

    public override int GetHashCode() =>
        HashCode.Combine(TimeBootMs, ChanCount, Rssi, MavlinkWire.ArrayHashCode(Channels, ChannelArrayLength));

    public byte[] EncodePayload()
    {
        byte[] buf = new byte[WireSize];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), TimeBootMs);
        ushort[] normalized = MavlinkWire.NormalizeArray(Channels, ChannelArrayLength);
        for (int i = 0; i < ChannelArrayLength; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4 + (i * 2), 2), normalized[i]);
        }

        buf[40] = ChanCount;
        buf[41] = Rssi;
        return buf;
    }

    public static RcChannelsMessage Decode(MavlinkFrame frame)
    {
        MavlinkWire.EnsureMessageId(frame, MsgId);
        return Decode(frame.Payload.Span);
    }

    public static RcChannelsMessage Decode(ReadOnlySpan<byte> payload)
    {
        Span<byte> buf = stackalloc byte[WireSize];
        MavlinkWire.ZeroPad(payload, buf);
        ushort[] channels = new ushort[ChannelArrayLength];
        for (int i = 0; i < ChannelArrayLength; i++)
        {
            channels[i] = BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(4 + (i * 2), 2));
        }

        return new RcChannelsMessage(
            TimeBootMs: BinaryPrimitives.ReadUInt32LittleEndian(buf[0..4]),
            ChanCount: buf[40],
            Channels: channels,
            Rssi: buf[41]);
    }
}

/// <summary>
/// REQUEST_DATA_STREAM (#66, CRC_EXTRA=148, 6 bytes).
/// </summary>
public sealed record RequestDataStreamMessage(
    byte TargetSystem,
    byte TargetComponent,
    byte ReqStreamId,
    ushort ReqMessageRate,
    byte StartStop) : IMavlinkMessage
{
    public const uint MsgId = 66;
    public const byte MsgCrcExtra = 148;
    public const int WireSize = 6;

    public uint MessageId => MsgId;
    public byte CrcExtra => MsgCrcExtra;

    public byte[] EncodePayload()
    {
        byte[] buf = new byte[WireSize];
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0, 2), ReqMessageRate);
        buf[2] = TargetSystem;
        buf[3] = TargetComponent;
        buf[4] = ReqStreamId;
        buf[5] = StartStop;
        return buf;
    }

    public static RequestDataStreamMessage Decode(MavlinkFrame frame)
    {
        MavlinkWire.EnsureMessageId(frame, MsgId);
        return Decode(frame.Payload.Span);
    }

    public static RequestDataStreamMessage Decode(ReadOnlySpan<byte> payload)
    {
        Span<byte> buf = stackalloc byte[WireSize];
        MavlinkWire.ZeroPad(payload, buf);
        return new RequestDataStreamMessage(
            TargetSystem: buf[2],
            TargetComponent: buf[3],
            ReqStreamId: buf[4],
            ReqMessageRate: BinaryPrimitives.ReadUInt16LittleEndian(buf[0..2]),
            StartStop: buf[5]);
    }
}

/// <summary>
/// MISSION_ITEM_INT (#73, CRC_EXTRA=38, 37 base + 1 extension = 38 bytes).
/// </summary>
public sealed record MissionItemIntMessage(
    byte TargetSystem,
    byte TargetComponent,
    ushort Seq,
    MavFrame Frame,
    MavCmd Command,
    byte Current,
    byte Autocontinue,
    float Param1,
    float Param2,
    float Param3,
    float Param4,
    int X,
    int Y,
    float Z,
    MavMissionType MissionType = MavMissionType.Mission) : IMavlinkMessage
{
    public const uint MsgId = 73;
    public const byte MsgCrcExtra = 38;
    public const int WireSize = 38;

    public uint MessageId => MsgId;
    public byte CrcExtra => MsgCrcExtra;

    public byte[] EncodePayload()
    {
        byte[] buf = new byte[WireSize];
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(0, 4), Param1);
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(4, 4), Param2);
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(8, 4), Param3);
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(12, 4), Param4);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(16, 4), X);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(20, 4), Y);
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(24, 4), Z);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(28, 2), Seq);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(30, 2), (ushort)Command);
        buf[32] = TargetSystem;
        buf[33] = TargetComponent;
        buf[34] = (byte)Frame;
        buf[35] = Current;
        buf[36] = Autocontinue;
        buf[37] = (byte)MissionType;
        return buf;
    }

    public static MissionItemIntMessage Decode(MavlinkFrame frame)
    {
        MavlinkWire.EnsureMessageId(frame, MsgId);
        return Decode(frame.Payload.Span);
    }

    public static MissionItemIntMessage Decode(ReadOnlySpan<byte> payload)
    {
        Span<byte> buf = stackalloc byte[WireSize];
        MavlinkWire.ZeroPad(payload, buf);
        return new MissionItemIntMessage(
            TargetSystem: buf[32],
            TargetComponent: buf[33],
            Seq: BinaryPrimitives.ReadUInt16LittleEndian(buf[28..30]),
            Frame: (MavFrame)buf[34],
            Command: (MavCmd)BinaryPrimitives.ReadUInt16LittleEndian(buf[30..32]),
            Current: buf[35],
            Autocontinue: buf[36],
            Param1: BinaryPrimitives.ReadSingleLittleEndian(buf[0..4]),
            Param2: BinaryPrimitives.ReadSingleLittleEndian(buf[4..8]),
            Param3: BinaryPrimitives.ReadSingleLittleEndian(buf[8..12]),
            Param4: BinaryPrimitives.ReadSingleLittleEndian(buf[12..16]),
            X: BinaryPrimitives.ReadInt32LittleEndian(buf[16..20]),
            Y: BinaryPrimitives.ReadInt32LittleEndian(buf[20..24]),
            Z: BinaryPrimitives.ReadSingleLittleEndian(buf[24..28]),
            MissionType: (MavMissionType)buf[37]);
    }
}

/// <summary>
/// VFR_HUD (#74, CRC_EXTRA=20, 20 bytes).
/// </summary>
public sealed record VfrHudMessage(
    float Airspeed,
    float Groundspeed,
    short Heading,
    ushort Throttle,
    float Alt,
    float Climb) : IMavlinkMessage
{
    public const uint MsgId = 74;
    public const byte MsgCrcExtra = 20;
    public const int WireSize = 20;

    public uint MessageId => MsgId;
    public byte CrcExtra => MsgCrcExtra;

    public byte[] EncodePayload()
    {
        byte[] buf = new byte[WireSize];
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(0, 4), Airspeed);
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(4, 4), Groundspeed);
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(8, 4), Alt);
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(12, 4), Climb);
        BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(16, 2), Heading);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(18, 2), Throttle);
        return buf;
    }

    public static VfrHudMessage Decode(MavlinkFrame frame)
    {
        MavlinkWire.EnsureMessageId(frame, MsgId);
        return Decode(frame.Payload.Span);
    }

    public static VfrHudMessage Decode(ReadOnlySpan<byte> payload)
    {
        Span<byte> buf = stackalloc byte[WireSize];
        MavlinkWire.ZeroPad(payload, buf);
        return new VfrHudMessage(
            Airspeed: BinaryPrimitives.ReadSingleLittleEndian(buf[0..4]),
            Groundspeed: BinaryPrimitives.ReadSingleLittleEndian(buf[4..8]),
            Heading: BinaryPrimitives.ReadInt16LittleEndian(buf[16..18]),
            Throttle: BinaryPrimitives.ReadUInt16LittleEndian(buf[18..20]),
            Alt: BinaryPrimitives.ReadSingleLittleEndian(buf[8..12]),
            Climb: BinaryPrimitives.ReadSingleLittleEndian(buf[12..16]));
    }
}

/// <summary>
/// COMMAND_LONG (#76, CRC_EXTRA=152, 33 bytes).
/// </summary>
public sealed record CommandLongMessage(
    byte TargetSystem,
    byte TargetComponent,
    MavCmd Command,
    byte Confirmation = 0,
    float Param1 = 0f,
    float Param2 = 0f,
    float Param3 = 0f,
    float Param4 = 0f,
    float Param5 = 0f,
    float Param6 = 0f,
    float Param7 = 0f) : IMavlinkMessage
{
    public const uint MsgId = 76;
    public const byte MsgCrcExtra = 152;
    public const int WireSize = 33;

    public uint MessageId => MsgId;
    public byte CrcExtra => MsgCrcExtra;

    public byte[] EncodePayload()
    {
        byte[] buf = new byte[WireSize];
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(0, 4), Param1);
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(4, 4), Param2);
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(8, 4), Param3);
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(12, 4), Param4);
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(16, 4), Param5);
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(20, 4), Param6);
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(24, 4), Param7);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(28, 2), (ushort)Command);
        buf[30] = TargetSystem;
        buf[31] = TargetComponent;
        buf[32] = Confirmation;
        return buf;
    }

    public static CommandLongMessage Decode(MavlinkFrame frame)
    {
        MavlinkWire.EnsureMessageId(frame, MsgId);
        return Decode(frame.Payload.Span);
    }

    public static CommandLongMessage Decode(ReadOnlySpan<byte> payload)
    {
        Span<byte> buf = stackalloc byte[WireSize];
        MavlinkWire.ZeroPad(payload, buf);
        return new CommandLongMessage(
            TargetSystem: buf[30],
            TargetComponent: buf[31],
            Command: (MavCmd)BinaryPrimitives.ReadUInt16LittleEndian(buf[28..30]),
            Confirmation: buf[32],
            Param1: BinaryPrimitives.ReadSingleLittleEndian(buf[0..4]),
            Param2: BinaryPrimitives.ReadSingleLittleEndian(buf[4..8]),
            Param3: BinaryPrimitives.ReadSingleLittleEndian(buf[8..12]),
            Param4: BinaryPrimitives.ReadSingleLittleEndian(buf[12..16]),
            Param5: BinaryPrimitives.ReadSingleLittleEndian(buf[16..20]),
            Param6: BinaryPrimitives.ReadSingleLittleEndian(buf[20..24]),
            Param7: BinaryPrimitives.ReadSingleLittleEndian(buf[24..28]));
    }
}

/// <summary>
/// COMMAND_ACK (#77, CRC_EXTRA=143, 3 base + 7 extension = 10 bytes).
/// </summary>
public sealed record CommandAckMessage(
    MavCmd Command,
    MavResult Result,
    byte Progress = byte.MaxValue,
    int ResultParam2 = 0,
    byte TargetSystem = 0,
    byte TargetComponent = 0) : IMavlinkMessage
{
    public const uint MsgId = 77;
    public const byte MsgCrcExtra = 143;
    public const int WireSize = 10;

    public uint MessageId => MsgId;
    public byte CrcExtra => MsgCrcExtra;

    public byte[] EncodePayload()
    {
        byte[] buf = new byte[WireSize];
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0, 2), (ushort)Command);
        buf[2] = (byte)Result;
        buf[3] = Progress;
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), ResultParam2);
        buf[8] = TargetSystem;
        buf[9] = TargetComponent;
        return buf;
    }

    public static CommandAckMessage Decode(MavlinkFrame frame)
    {
        MavlinkWire.EnsureMessageId(frame, MsgId);
        return Decode(frame.Payload.Span);
    }

    public static CommandAckMessage Decode(ReadOnlySpan<byte> payload)
    {
        Span<byte> buf = stackalloc byte[WireSize];
        MavlinkWire.ZeroPad(payload, buf);
        return new CommandAckMessage(
            Command: (MavCmd)BinaryPrimitives.ReadUInt16LittleEndian(buf[0..2]),
            Result: (MavResult)buf[2],
            Progress: buf[3],
            ResultParam2: BinaryPrimitives.ReadInt32LittleEndian(buf[4..8]),
            TargetSystem: buf[8],
            TargetComponent: buf[9]);
    }
}

/// <summary>
/// SET_POSITION_TARGET_GLOBAL_INT (#86, CRC_EXTRA=5, 53 bytes).
/// </summary>
public sealed record SetPositionTargetGlobalIntMessage(
    uint TimeBootMs,
    byte TargetSystem,
    byte TargetComponent,
    MavFrame CoordinateFrame,
    PositionTargetTypemask TypeMask,
    int LatInt,
    int LonInt,
    float Alt,
    float Vx = 0f,
    float Vy = 0f,
    float Vz = 0f,
    float Afx = 0f,
    float Afy = 0f,
    float Afz = 0f,
    float Yaw = 0f,
    float YawRate = 0f) : IMavlinkMessage
{
    public const uint MsgId = 86;
    public const byte MsgCrcExtra = 5;
    public const int WireSize = 53;

    public uint MessageId => MsgId;
    public byte CrcExtra => MsgCrcExtra;

    public byte[] EncodePayload()
    {
        byte[] buf = new byte[WireSize];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), TimeBootMs);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), LatInt);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8, 4), LonInt);
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(12, 4), Alt);
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(16, 4), Vx);
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(20, 4), Vy);
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(24, 4), Vz);
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(28, 4), Afx);
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(32, 4), Afy);
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(36, 4), Afz);
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(40, 4), Yaw);
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(44, 4), YawRate);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(48, 2), (ushort)TypeMask);
        buf[50] = TargetSystem;
        buf[51] = TargetComponent;
        buf[52] = (byte)CoordinateFrame;
        return buf;
    }

    public static SetPositionTargetGlobalIntMessage Decode(MavlinkFrame frame)
    {
        MavlinkWire.EnsureMessageId(frame, MsgId);
        return Decode(frame.Payload.Span);
    }

    public static SetPositionTargetGlobalIntMessage Decode(ReadOnlySpan<byte> payload)
    {
        Span<byte> buf = stackalloc byte[WireSize];
        MavlinkWire.ZeroPad(payload, buf);
        return new SetPositionTargetGlobalIntMessage(
            TimeBootMs: BinaryPrimitives.ReadUInt32LittleEndian(buf[0..4]),
            TargetSystem: buf[50],
            TargetComponent: buf[51],
            CoordinateFrame: (MavFrame)buf[52],
            TypeMask: (PositionTargetTypemask)BinaryPrimitives.ReadUInt16LittleEndian(buf[48..50]),
            LatInt: BinaryPrimitives.ReadInt32LittleEndian(buf[4..8]),
            LonInt: BinaryPrimitives.ReadInt32LittleEndian(buf[8..12]),
            Alt: BinaryPrimitives.ReadSingleLittleEndian(buf[12..16]),
            Vx: BinaryPrimitives.ReadSingleLittleEndian(buf[16..20]),
            Vy: BinaryPrimitives.ReadSingleLittleEndian(buf[20..24]),
            Vz: BinaryPrimitives.ReadSingleLittleEndian(buf[24..28]),
            Afx: BinaryPrimitives.ReadSingleLittleEndian(buf[28..32]),
            Afy: BinaryPrimitives.ReadSingleLittleEndian(buf[32..36]),
            Afz: BinaryPrimitives.ReadSingleLittleEndian(buf[36..40]),
            Yaw: BinaryPrimitives.ReadSingleLittleEndian(buf[40..44]),
            YawRate: BinaryPrimitives.ReadSingleLittleEndian(buf[44..48]));
    }
}

/// <summary>
/// RADIO_STATUS (#109, CRC_EXTRA=185, 9 bytes).
/// </summary>
public sealed record RadioStatusMessage(
    byte Rssi,
    byte RemRssi,
    byte TxBuf,
    byte Noise,
    byte RemNoise,
    ushort RxErrors,
    ushort Fixed) : IMavlinkMessage
{
    public const uint MsgId = 109;
    public const byte MsgCrcExtra = 185;
    public const int WireSize = 9;

    public uint MessageId => MsgId;
    public byte CrcExtra => MsgCrcExtra;

    public byte[] EncodePayload()
    {
        byte[] buf = new byte[WireSize];
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0, 2), RxErrors);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2, 2), Fixed);
        buf[4] = Rssi;
        buf[5] = RemRssi;
        buf[6] = TxBuf;
        buf[7] = Noise;
        buf[8] = RemNoise;
        return buf;
    }

    public static RadioStatusMessage Decode(MavlinkFrame frame)
    {
        MavlinkWire.EnsureMessageId(frame, MsgId);
        return Decode(frame.Payload.Span);
    }

    public static RadioStatusMessage Decode(ReadOnlySpan<byte> payload)
    {
        Span<byte> buf = stackalloc byte[WireSize];
        MavlinkWire.ZeroPad(payload, buf);
        return new RadioStatusMessage(
            Rssi: buf[4],
            RemRssi: buf[5],
            TxBuf: buf[6],
            Noise: buf[7],
            RemNoise: buf[8],
            RxErrors: BinaryPrimitives.ReadUInt16LittleEndian(buf[0..2]),
            Fixed: BinaryPrimitives.ReadUInt16LittleEndian(buf[2..4]));
    }
}

/// <summary>
/// BATTERY_STATUS (#147, CRC_EXTRA=154, 36 base + 18 extension = 54 bytes).
/// </summary>
public sealed record BatteryStatusMessage(
    byte Id,
    MavBatteryFunction BatteryFunction,
    MavBatteryType Type,
    short Temperature,
    ushort[] Voltages,
    short CurrentBattery,
    int CurrentConsumed,
    int EnergyConsumed,
    sbyte BatteryRemaining,
    int TimeRemaining = 0,
    MavBatteryChargeState ChargeState = MavBatteryChargeState.Undefined,
    ushort[]? VoltagesExt = null,
    MavBatteryMode Mode = MavBatteryMode.Unknown,
    MavBatteryFault FaultBitmask = MavBatteryFault.None) : IMavlinkMessage
{
    public const uint MsgId = 147;
    public const byte MsgCrcExtra = 154;
    public const int WireSize = 54;
    public const int VoltagesLength = 10;
    public const int VoltagesExtLength = 4;

    public uint MessageId => MsgId;
    public byte CrcExtra => MsgCrcExtra;

    public bool Equals(BatteryStatusMessage? other) =>
        other is not null &&
        Id == other.Id &&
        BatteryFunction == other.BatteryFunction &&
        Type == other.Type &&
        Temperature == other.Temperature &&
        MavlinkWire.ArraysEqual(Voltages, other.Voltages, VoltagesLength) &&
        CurrentBattery == other.CurrentBattery &&
        CurrentConsumed == other.CurrentConsumed &&
        EnergyConsumed == other.EnergyConsumed &&
        BatteryRemaining == other.BatteryRemaining &&
        TimeRemaining == other.TimeRemaining &&
        ChargeState == other.ChargeState &&
        MavlinkWire.ArraysEqual(VoltagesExt, other.VoltagesExt, VoltagesExtLength) &&
        Mode == other.Mode &&
        FaultBitmask == other.FaultBitmask;

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        hash.Add(BatteryFunction);
        hash.Add(Type);
        hash.Add(Temperature);
        hash.Add(MavlinkWire.ArrayHashCode(Voltages, VoltagesLength));
        hash.Add(CurrentBattery);
        hash.Add(CurrentConsumed);
        hash.Add(EnergyConsumed);
        hash.Add(BatteryRemaining);
        hash.Add(TimeRemaining);
        hash.Add(ChargeState);
        hash.Add(MavlinkWire.ArrayHashCode(VoltagesExt, VoltagesExtLength));
        hash.Add(Mode);
        hash.Add(FaultBitmask);
        return hash.ToHashCode();
    }

    public byte[] EncodePayload()
    {
        byte[] buf = new byte[WireSize];
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), CurrentConsumed);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), EnergyConsumed);
        BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(8, 2), Temperature);

        ushort[] voltages = MavlinkWire.NormalizeArray(Voltages, VoltagesLength);
        for (int i = 0; i < VoltagesLength; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(10 + (i * 2), 2), voltages[i]);
        }

        BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(30, 2), CurrentBattery);
        buf[32] = Id;
        buf[33] = (byte)BatteryFunction;
        buf[34] = (byte)Type;
        buf[35] = unchecked((byte)BatteryRemaining);

        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(36, 4), TimeRemaining);
        buf[40] = (byte)ChargeState;

        ushort[] voltagesExt = MavlinkWire.NormalizeArray(VoltagesExt, VoltagesExtLength);
        for (int i = 0; i < VoltagesExtLength; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(41 + (i * 2), 2), voltagesExt[i]);
        }

        buf[49] = (byte)Mode;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(50, 4), (uint)FaultBitmask);
        return buf;
    }

    public static BatteryStatusMessage Decode(MavlinkFrame frame)
    {
        MavlinkWire.EnsureMessageId(frame, MsgId);
        return Decode(frame.Payload.Span);
    }

    public static BatteryStatusMessage Decode(ReadOnlySpan<byte> payload)
    {
        Span<byte> buf = stackalloc byte[WireSize];
        MavlinkWire.ZeroPad(payload, buf);

        ushort[] voltages = new ushort[VoltagesLength];
        for (int i = 0; i < VoltagesLength; i++)
        {
            voltages[i] = BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(10 + (i * 2), 2));
        }

        ushort[] voltagesExt = new ushort[VoltagesExtLength];
        for (int i = 0; i < VoltagesExtLength; i++)
        {
            voltagesExt[i] = BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(41 + (i * 2), 2));
        }

        return new BatteryStatusMessage(
            Id: buf[32],
            BatteryFunction: (MavBatteryFunction)buf[33],
            Type: (MavBatteryType)buf[34],
            Temperature: BinaryPrimitives.ReadInt16LittleEndian(buf[8..10]),
            Voltages: voltages,
            CurrentBattery: BinaryPrimitives.ReadInt16LittleEndian(buf[30..32]),
            CurrentConsumed: BinaryPrimitives.ReadInt32LittleEndian(buf[0..4]),
            EnergyConsumed: BinaryPrimitives.ReadInt32LittleEndian(buf[4..8]),
            BatteryRemaining: unchecked((sbyte)buf[35]),
            TimeRemaining: BinaryPrimitives.ReadInt32LittleEndian(buf[36..40]),
            ChargeState: (MavBatteryChargeState)buf[40],
            VoltagesExt: voltagesExt,
            Mode: (MavBatteryMode)buf[49],
            FaultBitmask: (MavBatteryFault)BinaryPrimitives.ReadUInt32LittleEndian(buf[50..54]));
    }
}

/// <summary>
/// HOME_POSITION (#242, CRC_EXTRA=104, 52 base + 8 extension = 60 bytes).
/// </summary>
public sealed record HomePositionMessage(
    int Latitude,
    int Longitude,
    int Altitude,
    float X = 0f,
    float Y = 0f,
    float Z = 0f,
    float[]? Q = null,
    float ApproachX = 0f,
    float ApproachY = 0f,
    float ApproachZ = 0f,
    ulong TimeUsec = 0) : IMavlinkMessage
{
    public const uint MsgId = 242;
    public const byte MsgCrcExtra = 104;
    public const int WireSize = 60;
    public const int QuaternionLength = 4;

    public uint MessageId => MsgId;
    public byte CrcExtra => MsgCrcExtra;

    public bool Equals(HomePositionMessage? other) =>
        other is not null &&
        Latitude == other.Latitude &&
        Longitude == other.Longitude &&
        Altitude == other.Altitude &&
        X.Equals(other.X) &&
        Y.Equals(other.Y) &&
        Z.Equals(other.Z) &&
        MavlinkWire.ArraysEqual(Q, other.Q, QuaternionLength) &&
        ApproachX.Equals(other.ApproachX) &&
        ApproachY.Equals(other.ApproachY) &&
        ApproachZ.Equals(other.ApproachZ) &&
        TimeUsec == other.TimeUsec;

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Latitude);
        hash.Add(Longitude);
        hash.Add(Altitude);
        hash.Add(X);
        hash.Add(Y);
        hash.Add(Z);
        hash.Add(MavlinkWire.ArrayHashCode(Q, QuaternionLength));
        hash.Add(ApproachX);
        hash.Add(ApproachY);
        hash.Add(ApproachZ);
        hash.Add(TimeUsec);
        return hash.ToHashCode();
    }

    public byte[] EncodePayload()
    {
        byte[] buf = new byte[WireSize];
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), Latitude);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), Longitude);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8, 4), Altitude);
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(12, 4), X);
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(16, 4), Y);
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(20, 4), Z);

        float[] q = MavlinkWire.NormalizeArray(Q, QuaternionLength);
        for (int i = 0; i < QuaternionLength; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(24 + (i * 4), 4), q[i]);
        }

        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(40, 4), ApproachX);
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(44, 4), ApproachY);
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(48, 4), ApproachZ);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(52, 8), TimeUsec);
        return buf;
    }

    public static HomePositionMessage Decode(MavlinkFrame frame)
    {
        MavlinkWire.EnsureMessageId(frame, MsgId);
        return Decode(frame.Payload.Span);
    }

    public static HomePositionMessage Decode(ReadOnlySpan<byte> payload)
    {
        Span<byte> buf = stackalloc byte[WireSize];
        MavlinkWire.ZeroPad(payload, buf);

        float[] q = new float[QuaternionLength];
        for (int i = 0; i < QuaternionLength; i++)
        {
            q[i] = BinaryPrimitives.ReadSingleLittleEndian(buf.Slice(24 + (i * 4), 4));
        }

        return new HomePositionMessage(
            Latitude: BinaryPrimitives.ReadInt32LittleEndian(buf[0..4]),
            Longitude: BinaryPrimitives.ReadInt32LittleEndian(buf[4..8]),
            Altitude: BinaryPrimitives.ReadInt32LittleEndian(buf[8..12]),
            X: BinaryPrimitives.ReadSingleLittleEndian(buf[12..16]),
            Y: BinaryPrimitives.ReadSingleLittleEndian(buf[16..20]),
            Z: BinaryPrimitives.ReadSingleLittleEndian(buf[20..24]),
            Q: q,
            ApproachX: BinaryPrimitives.ReadSingleLittleEndian(buf[40..44]),
            ApproachY: BinaryPrimitives.ReadSingleLittleEndian(buf[44..48]),
            ApproachZ: BinaryPrimitives.ReadSingleLittleEndian(buf[48..52]),
            TimeUsec: BinaryPrimitives.ReadUInt64LittleEndian(buf[52..60]));
    }
}

/// <summary>
/// STATUSTEXT (#253, CRC_EXTRA=83, 51 base + 3 extension = 54 bytes).
/// </summary>
public sealed record StatusTextMessage(
    MavSeverity Severity,
    string Text,
    ushort Id = 0,
    byte ChunkSeq = 0) : IMavlinkMessage
{
    public const uint MsgId = 253;
    public const byte MsgCrcExtra = 83;
    public const int WireSize = 54;
    public const int MaxTextLength = 50;

    public uint MessageId => MsgId;
    public byte CrcExtra => MsgCrcExtra;

    public byte[] EncodePayload()
    {
        byte[] buf = new byte[WireSize];
        buf[0] = (byte)Severity;
        if (!string.IsNullOrEmpty(Text))
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(Text);
            int copyLength = Math.Min(utf8.Length, MaxTextLength);
            utf8.AsSpan(0, copyLength).CopyTo(buf.AsSpan(1, MaxTextLength));
        }

        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(51, 2), Id);
        buf[53] = ChunkSeq;
        return buf;
    }

    public static StatusTextMessage Decode(MavlinkFrame frame)
    {
        MavlinkWire.EnsureMessageId(frame, MsgId);
        return Decode(frame.Payload.Span);
    }

    public static StatusTextMessage Decode(ReadOnlySpan<byte> payload)
    {
        Span<byte> buf = stackalloc byte[WireSize];
        MavlinkWire.ZeroPad(payload, buf);

        ReadOnlySpan<byte> textBytes = buf.Slice(1, MaxTextLength);
        int nullTerminator = textBytes.IndexOf((byte)0);
        if (nullTerminator >= 0)
        {
            textBytes = textBytes[..nullTerminator];
        }

        return new StatusTextMessage(
            Severity: (MavSeverity)buf[0],
            Text: Encoding.UTF8.GetString(textBytes),
            Id: BinaryPrimitives.ReadUInt16LittleEndian(buf[51..53]),
            ChunkSeq: buf[53]);
    }
}
