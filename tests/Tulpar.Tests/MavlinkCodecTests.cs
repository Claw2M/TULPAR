using System.Buffers.Binary;
using Tulpar.Core.Mavlink;

namespace Tulpar.Tests;

public class MavlinkCodecTests
{
    private static ushort ReferenceX25Crc(ReadOnlySpan<byte> bytes, byte crcExtra)
    {
        ushort crc = 0xFFFF;
        foreach (byte b in bytes)
        {
            byte tmp = (byte)(b ^ (crc & 0xFF));
            tmp ^= (byte)(tmp << 4);
            crc = (ushort)((crc >> 8) ^ (tmp << 8) ^ (tmp << 3) ^ (tmp >> 4));
        }

        byte extraTmp = (byte)(crcExtra ^ (crc & 0xFF));
        extraTmp ^= (byte)(extraTmp << 4);
        crc = (ushort)((crc >> 8) ^ (extraTmp << 8) ^ (extraTmp << 3) ^ (extraTmp >> 4));
        return crc;
    }

    private static byte[] BuildRawV1Frame(byte seq, byte sysId, byte compId, byte msgId, ReadOnlySpan<byte> payload, byte crcExtra)
    {
        byte[] packet = new byte[8 + payload.Length];
        packet[0] = 0xFE;
        packet[1] = (byte)payload.Length;
        packet[2] = seq;
        packet[3] = sysId;
        packet[4] = compId;
        packet[5] = msgId;
        payload.CopyTo(packet.AsSpan(6));
        ushort crc = ReferenceX25Crc(packet.AsSpan(1, 5 + payload.Length), crcExtra);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6 + payload.Length, 2), crc);
        return packet;
    }

    private static byte[] BuildRawV2Frame(
        byte seq,
        byte sysId,
        byte compId,
        uint msgId,
        ReadOnlySpan<byte> payload,
        byte crcExtra,
        byte incompatFlags = 0,
        byte compatFlags = 0,
        ReadOnlySpan<byte> signature = default)
    {
        int sigLen = (incompatFlags & 0x01) != 0 ? 13 : 0;
        byte[] packet = new byte[12 + payload.Length + sigLen];
        packet[0] = 0xFD;
        packet[1] = (byte)payload.Length;
        packet[2] = incompatFlags;
        packet[3] = compatFlags;
        packet[4] = seq;
        packet[5] = sysId;
        packet[6] = compId;
        packet[7] = (byte)(msgId & 0xFF);
        packet[8] = (byte)((msgId >> 8) & 0xFF);
        packet[9] = (byte)((msgId >> 16) & 0xFF);
        payload.CopyTo(packet.AsSpan(10));
        ushort crc = ReferenceX25Crc(packet.AsSpan(1, 9 + payload.Length), crcExtra);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(10 + payload.Length, 2), crc);
        if (sigLen > 0)
        {
            if (signature.Length >= 13)
            {
                signature[..13].CopyTo(packet.AsSpan(12 + payload.Length, 13));
            }
            else
            {
                signature.CopyTo(packet.AsSpan(12 + payload.Length));
            }
        }

        return packet;
    }

    [Fact]
    public void RoundTrip_V2_Heartbeat_And_MissionItemInt_PreservesFieldsAndCrc()
    {
        var heartbeat = new HeartbeatMessage(
            Type: MavType.Quadrotor,
            Autopilot: MavAutopilot.ArduPilotMega,
            BaseMode: MavModeFlag.CustomModeEnabled | MavModeFlag.SafetyArmed,
            CustomMode: 5u,
            SystemStatus: MavState.Active,
            MavlinkVersion: 3);

        Assert.True(MavlinkCrc.TryGetCrcExtra(HeartbeatMessage.MsgId, out byte hbCrcExtra));
        Assert.Equal((byte)50, hbCrcExtra);

        byte[] hbEncoded = MavlinkEncoder.EncodeV2(1, 1, 42, HeartbeatMessage.MsgId, heartbeat.EncodePayload(), hbCrcExtra);
        Assert.Equal(0xFD, hbEncoded[0]);
        Assert.Equal(9, hbEncoded[1]);
        Assert.Equal(42, hbEncoded[4]);
        Assert.Equal(1, hbEncoded[5]);
        Assert.Equal(1, hbEncoded[6]);
        Assert.Equal(0u, (uint)(hbEncoded[7] | (hbEncoded[8] << 8) | (hbEncoded[9] << 16)));
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(hbEncoded.AsSpan(10, 4)));
        Assert.Equal((byte)MavType.Quadrotor, hbEncoded[14]);
        Assert.Equal((byte)MavAutopilot.ArduPilotMega, hbEncoded[15]);
        Assert.Equal((byte)(MavModeFlag.CustomModeEnabled | MavModeFlag.SafetyArmed), hbEncoded[16]);
        Assert.Equal((byte)MavState.Active, hbEncoded[17]);
        Assert.Equal(3, hbEncoded[18]);

        ushort expectedHbCrc = ReferenceX25Crc(hbEncoded.AsSpan(1, 18), 50);
        Assert.Equal(expectedHbCrc, BinaryPrimitives.ReadUInt16LittleEndian(hbEncoded.AsSpan(19, 2)));

        var missionItem = new MissionItemIntMessage(
            TargetSystem: 1,
            TargetComponent: 1,
            Seq: 3,
            Frame: MavFrame.GlobalRelativeAltInt,
            Command: MavCmd.NavFencePolygonVertexInclusion,
            Current: 0,
            Autocontinue: 1,
            Param1: 4f,
            Param2: 0f,
            Param3: 0f,
            Param4: 0f,
            X: 399250180,
            Y: -328369560,
            Z: 120.5f,
            MissionType: MavMissionType.Fence);

        Assert.True(MavlinkCrc.TryGetCrcExtra(MissionItemIntMessage.MsgId, out byte miCrcExtra));
        Assert.Equal((byte)38, miCrcExtra);

        byte[] miPayload = missionItem.EncodePayload();
        Assert.Equal(38, miPayload.Length);
        Assert.Equal(399250180, BinaryPrimitives.ReadInt32LittleEndian(miPayload.AsSpan(16, 4)));
        Assert.Equal(-328369560, BinaryPrimitives.ReadInt32LittleEndian(miPayload.AsSpan(20, 4)));
        Assert.Equal(120.5f, BinaryPrimitives.ReadSingleLittleEndian(miPayload.AsSpan(24, 4)));
        Assert.Equal(3, BinaryPrimitives.ReadUInt16LittleEndian(miPayload.AsSpan(28, 2)));
        Assert.Equal((ushort)MavCmd.NavFencePolygonVertexInclusion, BinaryPrimitives.ReadUInt16LittleEndian(miPayload.AsSpan(30, 2)));
        Assert.Equal((byte)MavMissionType.Fence, miPayload[37]);

        byte[] miEncoded = MavlinkEncoder.EncodeV2(255, 190, 99, MissionItemIntMessage.MsgId, miPayload, miCrcExtra);

        var parser = new MavlinkParser();
        var frames = new List<MavlinkFrame>();
        frames.AddRange(parser.Feed(hbEncoded));
        frames.AddRange(parser.Feed(miEncoded));

        Assert.Equal(2, frames.Count);

        Assert.Equal((byte)2, frames[0].Version);
        Assert.Equal((byte)42, frames[0].Sequence);
        Assert.Equal((byte)1, frames[0].SystemId);
        Assert.Equal((byte)1, frames[0].ComponentId);
        Assert.Equal(0u, frames[0].MessageId);
        Assert.Equal(heartbeat, HeartbeatMessage.Decode(frames[0]));

        Assert.Equal((byte)2, frames[1].Version);
        Assert.Equal((byte)99, frames[1].Sequence);
        Assert.Equal((byte)255, frames[1].SystemId);
        Assert.Equal((byte)190, frames[1].ComponentId);
        Assert.Equal(73u, frames[1].MessageId);
        Assert.Equal(missionItem, MissionItemIntMessage.Decode(frames[1]));
    }

    [Fact]
    public void Decode_V1_Attitude_Packet_ComputesValidCrc()
    {
        Span<byte> rawPayload = stackalloc byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(rawPayload[0..4], 0x01020304u);
        BinaryPrimitives.WriteSingleLittleEndian(rawPayload[4..8], 0.5f);
        BinaryPrimitives.WriteSingleLittleEndian(rawPayload[8..12], -0.25f);
        BinaryPrimitives.WriteSingleLittleEndian(rawPayload[12..16], 1.5f);
        BinaryPrimitives.WriteSingleLittleEndian(rawPayload[16..20], 0.125f);
        BinaryPrimitives.WriteSingleLittleEndian(rawPayload[20..24], -0.5f);
        BinaryPrimitives.WriteSingleLittleEndian(rawPayload[24..28], 0.25f);

        byte[] rawV1 = BuildRawV1Frame(seq: 7, sysId: 1, compId: 1, msgId: 30, rawPayload, crcExtra: 39);
        Assert.Equal(36, rawV1.Length);

        var parser = new MavlinkParser();
        var warnings = new List<string>();
        parser.FrameWarning += warnings.Add;

        var frames = parser.Feed(rawV1).ToList();
        Assert.Empty(warnings);
        Assert.Single(frames);

        MavlinkFrame frame = frames[0];
        Assert.Equal((byte)1, frame.Version);
        Assert.Equal((byte)7, frame.Sequence);
        Assert.Equal((byte)1, frame.SystemId);
        Assert.Equal((byte)1, frame.ComponentId);
        Assert.Equal(30u, frame.MessageId);
        Assert.Equal(28, frame.Payload.Length);

        AttitudeMessage decoded = AttitudeMessage.Decode(frame);
        Assert.Equal(0x01020304u, decoded.TimeBootMs);
        Assert.Equal(0.5f, decoded.Roll);
        Assert.Equal(-0.25f, decoded.Pitch);
        Assert.Equal(1.5f, decoded.Yaw);
        Assert.Equal(0.125f, decoded.Rollspeed);
        Assert.Equal(-0.5f, decoded.Pitchspeed);
        Assert.Equal(0.25f, decoded.Yawspeed);

        // Mutate 1 bit in CRC and verify parser rejects frame and emits warning
        byte[] badV1 = (byte[])rawV1.Clone();
        badV1[^1] ^= 0x01;
        Assert.Empty(parser.Feed(badV1));
        Assert.Single(warnings);
    }

    [Fact]
    public void Feed_SplitAndCorruptedBytesAcrossChunks_ResyncsAndEmitsValidFrames()
    {
        var hb = new HeartbeatMessage(
            MavType.FixedWing,
            MavAutopilot.ArduPilotMega,
            MavModeFlag.AutoEnabled | MavModeFlag.CustomModeEnabled,
            10u,
            MavState.Active);

        var pos = new GlobalPositionIntMessage(
            TimeBootMs: 55000u,
            Lat: 410082376,
            Lon: 289783589,
            Alt: 150000,
            RelativeAlt: 120000,
            Vx: 1850,
            Vy: -120,
            Vz: 35,
            Hdg: 9025);

        var status = new StatusTextMessage(MavSeverity.Warning, "PREARM CHECK OK");

        byte[] frame1 = MavlinkEncoder.EncodeV2(1, 1, 1, HeartbeatMessage.MsgId, hb.EncodePayload(), 50);
        byte[] frame2 = MavlinkEncoder.EncodeV2(1, 1, 2, GlobalPositionIntMessage.MsgId, pos.EncodePayload(), 104);
        byte[] frame3 = MavlinkEncoder.EncodeV2(1, 1, 3, StatusTextMessage.MsgId, status.EncodePayload(), 83);

        byte[] garbagePrefix = [0x11, 0x22, 0xAA, 0x00, 0x55];
        byte[] truncatedV2Header = [0xFD, 0x09, 0x00, 0x00, 0x01]; // looks like 9-byte v2 header, incomplete
        byte[] corruptedV1Frame = BuildRawV1Frame(0, 1, 1, 30, new byte[28], crcExtra: 39);
        corruptedV1Frame[^1] ^= 0xFF;

        byte[] stream =
        [
            ..garbagePrefix,
            ..truncatedV2Header,
            ..frame1,
            0xFE, 0x02, 0x99, // truncated v1 stub
            ..frame2,
            ..corruptedV1Frame,
            ..frame3
        ];

        // Feed 1 byte at a time
        var parser1 = new MavlinkParser();
        var warnings1 = new List<string>();
        parser1.FrameWarning += warnings1.Add;
        var decoded1 = new List<MavlinkFrame>();
        for (int i = 0; i < stream.Length; i++)
        {
            decoded1.AddRange(parser1.Feed(stream.AsSpan(i, 1)));
        }

        Assert.Equal(3, decoded1.Count);
        Assert.NotEmpty(warnings1);
        Assert.Equal(hb, HeartbeatMessage.Decode(decoded1[0]));
        Assert.Equal(pos, GlobalPositionIntMessage.Decode(decoded1[1]));
        Assert.Equal(status, StatusTextMessage.Decode(decoded1[2]));

        // Feed 3 bytes at a time
        var parser3 = new MavlinkParser();
        var decoded3 = new List<MavlinkFrame>();
        for (int i = 0; i < stream.Length; i += 3)
        {
            int count = Math.Min(3, stream.Length - i);
            decoded3.AddRange(parser3.Feed(stream.AsSpan(i, count)));
        }

        Assert.Equal(3, decoded3.Count);
        Assert.Equal(hb, HeartbeatMessage.Decode(decoded3[0]));
        Assert.Equal(pos, GlobalPositionIntMessage.Decode(decoded3[1]));
        Assert.Equal(status, StatusTextMessage.Decode(decoded3[2]));
    }

    [Fact]
    public void Feed_V2SignedIncompatFlag_LogsWarningAndSkipsFrameWithoutThrowing()
    {
        var hbPayload = new HeartbeatMessage(
            MavType.Quadrotor,
            MavAutopilot.Px4,
            MavModeFlag.CustomModeEnabled,
            0u,
            MavState.Standby).EncodePayload();

        // Signature includes decoy 0xFD and 0xFE bytes to verify full 13-byte trailer skip
        byte[] decoySignature = [0x01, 0xFD, 0xFE, 0x09, 0x00, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07];
        byte[] signedHeartbeat = BuildRawV2Frame(
            seq: 10,
            sysId: 1,
            compId: 1,
            msgId: 0,
            payload: hbPayload,
            crcExtra: 50,
            incompatFlags: 0x01,
            compatFlags: 0,
            signature: decoySignature);

        var ack = new CommandAckMessage(MavCmd.ComponentArmDisarm, MavResult.Accepted, 100, 0, 255, 190);
        byte[] unsupportedIncompatAck = BuildRawV2Frame(
            seq: 11,
            sysId: 1,
            compId: 1,
            msgId: CommandAckMessage.MsgId,
            payload: ack.EncodePayload(),
            crcExtra: 143,
            incompatFlags: 0x04);

        var hud = new VfrHudMessage(15.5f, 18.25f, 270, 62, 105.5f, 2.5f);
        byte[] validHud = MavlinkEncoder.EncodeV2(1, 1, 12, VfrHudMessage.MsgId, hud.EncodePayload(), 20);

        byte[] combined = [..signedHeartbeat, ..unsupportedIncompatAck, ..validHud];

        var parser = new MavlinkParser();
        var warnings = new List<string>();
        parser.FrameWarning += warnings.Add;

        var frames = parser.Feed(combined).ToList();

        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.Contains("signed", StringComparison.OrdinalIgnoreCase) || w.Contains("0x01", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(warnings, w => w.Contains("incompat", StringComparison.OrdinalIgnoreCase) || w.Contains("0x04", StringComparison.OrdinalIgnoreCase));
        Assert.Single(frames);
        Assert.Equal(VfrHudMessage.MsgId, frames[0].MessageId);
        Assert.Equal(hud, VfrHudMessage.Decode(frames[0]));
    }

    [Theory]
    [InlineData(0u, (byte)50)]
    [InlineData(1u, (byte)124)]
    [InlineData(2u, (byte)137)]
    [InlineData(11u, (byte)89)]
    [InlineData(20u, (byte)214)]
    [InlineData(21u, (byte)159)]
    [InlineData(22u, (byte)220)]
    [InlineData(23u, (byte)168)]
    [InlineData(24u, (byte)24)]
    [InlineData(30u, (byte)39)]
    [InlineData(33u, (byte)104)]
    [InlineData(39u, (byte)254)]
    [InlineData(40u, (byte)230)]
    [InlineData(41u, (byte)28)]
    [InlineData(42u, (byte)28)]
    [InlineData(43u, (byte)132)]
    [InlineData(44u, (byte)221)]
    [InlineData(45u, (byte)232)]
    [InlineData(46u, (byte)11)]
    [InlineData(47u, (byte)153)]
    [InlineData(51u, (byte)196)]
    [InlineData(65u, (byte)118)]
    [InlineData(66u, (byte)148)]
    [InlineData(73u, (byte)38)]
    [InlineData(74u, (byte)20)]
    [InlineData(76u, (byte)152)]
    [InlineData(77u, (byte)143)]
    [InlineData(86u, (byte)5)]
    [InlineData(109u, (byte)185)]
    [InlineData(147u, (byte)154)]
    [InlineData(193u, (byte)71)]
    [InlineData(241u, (byte)90)]
    [InlineData(242u, (byte)104)]
    [InlineData(245u, (byte)130)]
    [InlineData(253u, (byte)83)]
    public void TryGetCrcExtra_SupportedMessageIds_ReturnsExactCommonXmlByte(uint messageId, byte expectedCrcExtra)
    {
        Assert.True(MavlinkCrc.TryGetCrcExtra(messageId, out byte actual));
        Assert.Equal(expectedCrcExtra, actual);
    }

    [Fact]
    public void EncodeV2_ZeroTruncation_LeavesAtLeastOneByteAndDecodeZeroPads()
    {
        var allZeroAck = new MissionAckMessage(0, 0, MavMissionResult.Accepted, MavMissionType.Mission, 0);
        byte[] packet = MavlinkEncoder.EncodeV2(1, 1, 0, MissionAckMessage.MsgId, allZeroAck.EncodePayload(), 153);

        // Payload of 8 zero bytes must truncate to 1 byte (minimum non-empty length)
        Assert.Equal(13, packet.Length);
        Assert.Equal(1, packet[1]);
        Assert.Equal(0, packet[10]);

        var parser = new MavlinkParser();
        var frame = Assert.Single(parser.Feed(packet));
        Assert.Equal(1, frame.Payload.Length);

        var decoded = MissionAckMessage.Decode(frame);
        Assert.Equal(allZeroAck, decoded);

        // Empty payload remains length 0
        byte[] emptyPacket = MavlinkEncoder.EncodeV2(1, 1, 0, 0, ReadOnlySpan<byte>.Empty, 50);
        Assert.Equal(12, emptyPacket.Length);
        Assert.Equal(0, emptyPacket[1]);
    }

    [Fact]
    public void RoundTrip_AllSupportedMessages_AndTruncatedV1Payloads_PreserveValues()
    {
        var messages = new IMavlinkMessage[]
        {
            new SysStatusMessage(0x01, 0x02, 0x03, 450, 16800, 1250, 82, 12, 3, 1, 2, 3, 4, 0x10, 0x20, 0x30),
            new SystemTimeMessage(1700000000123456UL, 987654u),
            new SetModeMessage(1, MavModeFlag.CustomModeEnabled, 6u),
            new GpsRawIntMessage(123456789UL, GpsFixType.Fix3D, 399250180, 328369560, 850000, 120, 180, 1500, 18000, 14, 855000, 300, 450, 50, 100, 9000),
            new GlobalPositionIntMessage(42000u, -338688200, 1512092960, 50000, 45000, 100, -200, 50, 18050),
            new AttitudeMessage(1000u, 0.1f, -0.2f, 1.1f, 0.01f, -0.02f, 0.03f),
            new RcChannelsMessage(2000u, 8, [1500, 1500, 1100, 1500, 1800, 1000, 2000, 1500, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], 95),
            new RequestDataStreamMessage(1, 1, (byte)MavDataStream.Extra1, 10, 1),
            new VfrHudMessage(21.5f, 22.0f, 180, 55, 110.2f, 1.5f),
            new CommandLongMessage(1, 1, MavCmd.ComponentArmDisarm, 0, 1f, 0f, 0f, 0f, 0f, 0f, 0f),
            new CommandAckMessage(MavCmd.ComponentArmDisarm, MavResult.Accepted, 100, 42, 255, 190),
            new SetPositionTargetGlobalIntMessage(5000u, 1, 1, MavFrame.GlobalRelativeAltInt,
                PositionTargetTypemask.VxIgnore | PositionTargetTypemask.VyIgnore | PositionTargetTypemask.VzIgnore,
                399250180, 328369560, 100f, 0f, 0f, 0f, 0f, 0f, 0f, 1.57f, 0f),
            new RadioStatusMessage(180, 175, 92, 40, 45, 3, 1),
            new BatteryStatusMessage(0, MavBatteryFunction.All, MavBatteryType.Lipo, 2850,
                [4150, 4145, 4152, 4148, 65535, 65535, 65535, 65535, 65535, 65535],
                1420, 1250, 32000, 78, 640, MavBatteryChargeState.Ok,
                [4140, 4141, 0, 0], MavBatteryMode.Unknown, MavBatteryFault.IncompatibleVoltage),
            new HomePositionMessage(399250180, 328369560, 850000, 0f, 0f, 0f, [1f, 0f, 0f, 0f], 10f, 5f, -2f, 999888777UL),
            new StatusTextMessage(MavSeverity.Critical, "BATTERY FAILSAFE RTL", 77, 1),
            new MissionCountMessage(1, 1, 5, MavMissionType.Fence, 0x12345678u),
            new MissionRequestIntMessage(1, 1, 2, MavMissionType.Mission),
            new MissionRequestMessage(1, 1, 2, MavMissionType.Fence),
            new MissionItemIntMessage(1, 1, 0, MavFrame.GlobalRelativeAltInt, MavCmd.NavWaypoint, 1, 1, 0f, 5f, 0f, 0f, 399250180, 328369560, 50f, MavMissionType.Mission),
            new MissionItemMessage(1, 1, 1, MavFrame.GlobalRelativeAlt, MavCmd.NavLoiterTurns, 0, 1, 3f, 0f, 50f, 0f, 39.925f, 32.837f, 80f, MavMissionType.Mission),
            new MissionAckMessage(1, 1, MavMissionResult.Accepted, MavMissionType.Fence, 0xABCDEF01u),
            new MissionRequestListMessage(1, 1, MavMissionType.Fence),
            new MissionClearAllMessage(1, 1, MavMissionType.Fence),
            new MissionCurrentMessage(2, 5, 3, 1, 11u, 22u, 33u),
            new MissionSetCurrentMessage(1, 1, 2),
            new MissionItemReachedMessage(4),
            new ParamRequestReadMessage(1, 1, "WPNAV_SPEED", -1),
            new ParamRequestListMessage(1, 1),
            new ParamValueMessage("SYSID_THISMAV", 1f, MavParamType.Uint8, 420, 0),
            new ParamSetMessage(1, 1, "BATT_CAPACITY", 5200f, MavParamType.Int32),
            new EkfStatusReportMessage(EkfStatusFlags.Attitude | EkfStatusFlags.VelocityHoriz | EkfStatusFlags.PosHorizAbs, 0.1f, 0.2f, 0.3f, 0.05f, 0.02f, 0.01f),
            new VibrationMessage(123456789UL, 1.25f, 2.5f, 3.75f, 1, 2, 3),
            new ExtendedSysStateMessage(MavVtolState.Fw, MavLandedState.InAir)
        };

        var parser = new MavlinkParser();
        byte seq = 0;
        foreach (IMavlinkMessage message in messages)
        {
            byte[] packet = MavlinkEncoder.EncodeV2(1, 1, seq++, message);
            var frame = Assert.Single(parser.Feed(packet));
            IMavlinkMessage decoded = frame.MessageId switch
            {
                0 => HeartbeatMessage.Decode(frame),
                1 => SysStatusMessage.Decode(frame),
                2 => SystemTimeMessage.Decode(frame),
                11 => SetModeMessage.Decode(frame),
                20 => ParamRequestReadMessage.Decode(frame),
                21 => ParamRequestListMessage.Decode(frame),
                22 => ParamValueMessage.Decode(frame),
                23 => ParamSetMessage.Decode(frame),
                24 => GpsRawIntMessage.Decode(frame),
                30 => AttitudeMessage.Decode(frame),
                33 => GlobalPositionIntMessage.Decode(frame),
                39 => MissionItemMessage.Decode(frame),
                40 => MissionRequestMessage.Decode(frame),
                41 => MissionSetCurrentMessage.Decode(frame),
                42 => MissionCurrentMessage.Decode(frame),
                43 => MissionRequestListMessage.Decode(frame),
                44 => MissionCountMessage.Decode(frame),
                45 => MissionClearAllMessage.Decode(frame),
                46 => MissionItemReachedMessage.Decode(frame),
                47 => MissionAckMessage.Decode(frame),
                51 => MissionRequestIntMessage.Decode(frame),
                65 => RcChannelsMessage.Decode(frame),
                66 => RequestDataStreamMessage.Decode(frame),
                73 => MissionItemIntMessage.Decode(frame),
                74 => VfrHudMessage.Decode(frame),
                76 => CommandLongMessage.Decode(frame),
                77 => CommandAckMessage.Decode(frame),
                86 => SetPositionTargetGlobalIntMessage.Decode(frame),
                109 => RadioStatusMessage.Decode(frame),
                147 => BatteryStatusMessage.Decode(frame),
                193 => EkfStatusReportMessage.Decode(frame),
                241 => VibrationMessage.Decode(frame),
                242 => HomePositionMessage.Decode(frame),
                245 => ExtendedSysStateMessage.Decode(frame),
                253 => StatusTextMessage.Decode(frame),
                _ => throw new InvalidOperationException($"Unexpected message ID {frame.MessageId}")
            };

            Assert.Equal(message, decoded);
        }

        // Verify v1-length truncation zero-pads MAVLink2 extension fields
        var v1ShortGps = BuildRawV1Frame(0, 1, 1, 24, new byte[30], 24);
        var gpsFrame = Assert.Single(parser.Feed(v1ShortGps));
        var gpsDecoded = GpsRawIntMessage.Decode(gpsFrame);
        Assert.Equal(0, gpsDecoded.AltEllipsoid);
        Assert.Equal(0u, gpsDecoded.HAcc);
        Assert.Equal((ushort)0, gpsDecoded.Yaw);
    }

    [Fact]
    public void WireLayout_ReorderedMessages_MatchOfficialCommonXmlOffsets()
    {
        var posTarget = new SetPositionTargetGlobalIntMessage(
            TimeBootMs: 0x11223344u,
            TargetSystem: 7,
            TargetComponent: 42,
            CoordinateFrame: MavFrame.GlobalRelativeAltInt,
            TypeMask: PositionTargetTypemask.YawIgnore | PositionTargetTypemask.VxIgnore,
            LatInt: 399250180,
            LonInt: 328369560,
            Alt: 125.5f);
        byte[] ptBytes = posTarget.EncodePayload();
        Assert.Equal(53, ptBytes.Length);
        Assert.Equal(0x11223344u, BinaryPrimitives.ReadUInt32LittleEndian(ptBytes.AsSpan(0, 4)));
        Assert.Equal(399250180, BinaryPrimitives.ReadInt32LittleEndian(ptBytes.AsSpan(4, 4)));
        Assert.Equal(328369560, BinaryPrimitives.ReadInt32LittleEndian(ptBytes.AsSpan(8, 4)));
        Assert.Equal(125.5f, BinaryPrimitives.ReadSingleLittleEndian(ptBytes.AsSpan(12, 4)));
        Assert.Equal((ushort)(PositionTargetTypemask.YawIgnore | PositionTargetTypemask.VxIgnore),
            BinaryPrimitives.ReadUInt16LittleEndian(ptBytes.AsSpan(48, 2)));
        Assert.Equal(7, ptBytes[50]);
        Assert.Equal(42, ptBytes[51]);
        Assert.Equal((byte)MavFrame.GlobalRelativeAltInt, ptBytes[52]);

        var cmd = new CommandLongMessage(3, 1, MavCmd.DoReposition, 2, Param1: 15f, Param7: 100f);
        byte[] cmdBytes = cmd.EncodePayload();
        Assert.Equal(33, cmdBytes.Length);
        Assert.Equal(15f, BinaryPrimitives.ReadSingleLittleEndian(cmdBytes.AsSpan(0, 4)));
        Assert.Equal(100f, BinaryPrimitives.ReadSingleLittleEndian(cmdBytes.AsSpan(24, 4)));
        Assert.Equal((ushort)MavCmd.DoReposition, BinaryPrimitives.ReadUInt16LittleEndian(cmdBytes.AsSpan(28, 2)));
        Assert.Equal(3, cmdBytes[30]);
        Assert.Equal(1, cmdBytes[31]);
        Assert.Equal(2, cmdBytes[32]);

        var bat = new BatteryStatusMessage(
            Id: 2,
            BatteryFunction: MavBatteryFunction.Propulsion,
            Type: MavBatteryType.Lipo,
            Temperature: 3120,
            Voltages: [4200, 4195, 0, 0, 0, 0, 0, 0, 0, 0],
            CurrentBattery: 1850,
            CurrentConsumed: 4500,
            EnergyConsumed: 98000,
            BatteryRemaining: 64,
            TimeRemaining: 900,
            ChargeState: MavBatteryChargeState.Low,
            VoltagesExt: [4180, 0, 0, 0],
            Mode: MavBatteryMode.AutoDischarging,
            FaultBitmask: MavBatteryFault.OverCurrent);
        byte[] batBytes = bat.EncodePayload();
        Assert.Equal(54, batBytes.Length);
        Assert.Equal(4500, BinaryPrimitives.ReadInt32LittleEndian(batBytes.AsSpan(0, 4)));
        Assert.Equal(98000, BinaryPrimitives.ReadInt32LittleEndian(batBytes.AsSpan(4, 4)));
        Assert.Equal((short)3120, BinaryPrimitives.ReadInt16LittleEndian(batBytes.AsSpan(8, 2)));
        Assert.Equal((ushort)4200, BinaryPrimitives.ReadUInt16LittleEndian(batBytes.AsSpan(10, 2)));
        Assert.Equal((short)1850, BinaryPrimitives.ReadInt16LittleEndian(batBytes.AsSpan(30, 2)));
        Assert.Equal(2, batBytes[32]);
        Assert.Equal((byte)MavBatteryFunction.Propulsion, batBytes[33]);
        Assert.Equal((byte)MavBatteryType.Lipo, batBytes[34]);
        Assert.Equal((sbyte)64, unchecked((sbyte)batBytes[35]));
        Assert.Equal(900, BinaryPrimitives.ReadInt32LittleEndian(batBytes.AsSpan(36, 4)));
        Assert.Equal((byte)MavBatteryChargeState.Low, batBytes[40]);
        Assert.Equal((ushort)4180, BinaryPrimitives.ReadUInt16LittleEndian(batBytes.AsSpan(41, 2)));
        Assert.Equal((byte)MavBatteryMode.AutoDischarging, batBytes[49]);
        Assert.Equal((uint)MavBatteryFault.OverCurrent, BinaryPrimitives.ReadUInt32LittleEndian(batBytes.AsSpan(50, 4)));

        // Exactly 50 ASCII chars in STATUSTEXT (no null terminator room) must decode cleanly
        string full50 = new('X', 50);
        var fullStatus = new StatusTextMessage(MavSeverity.Notice, full50, 1234, 9);
        byte[] statusBytes = fullStatus.EncodePayload();
        Assert.Equal(54, statusBytes.Length);
        Assert.Equal((byte)MavSeverity.Notice, statusBytes[0]);
        Assert.Equal((byte)'X', statusBytes[50]);
        Assert.Equal((ushort)1234, BinaryPrimitives.ReadUInt16LittleEndian(statusBytes.AsSpan(51, 2)));
        Assert.Equal(9, statusBytes[53]);
        Assert.Equal(fullStatus, StatusTextMessage.Decode(statusBytes));
    }

    [Fact]
    public void Encoder_Parser_And_Decode_HandleValidationAndUnknownMessageIds()
    {
        Assert.False(MavlinkCrc.TryGetCrcExtra(99999u, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => MavlinkEncoder.EncodeV2(1, 1, 0, 0, new byte[256], 50));
        Assert.Throws<ArgumentOutOfRangeException>(() => MavlinkEncoder.EncodeV2(1, 1, 0, 0x0100_0000u, new byte[1], 50));

        var hb = new HeartbeatMessage(MavType.Quadrotor, MavAutopilot.ArduPilotMega, MavModeFlag.None, 0, MavState.Standby);
        byte[] validPacket = MavlinkEncoder.EncodeV2(1, 1, 1, hb);
        var hbFrame = Assert.Single(new MavlinkParser().Feed(validPacket));
        Assert.Throws<ArgumentException>(() => AttitudeMessage.Decode(hbFrame));

        // Unknown message ID in stream triggers warning and cleanly resyncs onto valid frame
        byte[] unknownFrame = BuildRawV2Frame(0, 1, 1, 99999u, [0x01, 0x02], 0);
        var parser = new MavlinkParser();
        var warnings = new List<string>();
        parser.FrameWarning += warnings.Add;

        var decoded = parser.Feed([..unknownFrame, ..validPacket]).ToList();
        Assert.Single(decoded);
        Assert.NotEmpty(warnings);
        Assert.Equal(hb, HeartbeatMessage.Decode(decoded[0]));

        // Reset discards partial buffered bytes so next valid packet parses cleanly
        Assert.Empty(parser.Feed(validPacket.AsSpan(0, 5)));
        parser.Reset();
        var afterReset = Assert.Single(parser.Feed(validPacket));
        Assert.Equal(hb, HeartbeatMessage.Decode(afterReset));
    }

    [Fact]
    public void WireLayout_ParamEkfVibrationExtendedSysState_MatchOfficialOffsets()
    {
        var reqRead = new ParamRequestReadMessage(7, 42, "1234567890ABCDEF", -1);
        byte[] rrBytes = reqRead.EncodePayload();
        Assert.Equal(20, rrBytes.Length);
        Assert.Equal((short)-1, BinaryPrimitives.ReadInt16LittleEndian(rrBytes.AsSpan(0, 2)));
        Assert.Equal(7, rrBytes[2]);
        Assert.Equal(42, rrBytes[3]);
        Assert.Equal((byte)'1', rrBytes[4]);
        Assert.Equal((byte)'F', rrBytes[19]);
        Assert.Equal(reqRead, ParamRequestReadMessage.Decode(rrBytes));

        var paramVal = new ParamValueMessage("WPNAV_SPEED", 1250.5f, MavParamType.Real32, 850, 12);
        byte[] pvBytes = paramVal.EncodePayload();
        Assert.Equal(25, pvBytes.Length);
        Assert.Equal(1250.5f, BinaryPrimitives.ReadSingleLittleEndian(pvBytes.AsSpan(0, 4)));
        Assert.Equal((ushort)850, BinaryPrimitives.ReadUInt16LittleEndian(pvBytes.AsSpan(4, 2)));
        Assert.Equal((ushort)12, BinaryPrimitives.ReadUInt16LittleEndian(pvBytes.AsSpan(6, 2)));
        Assert.Equal((byte)'W', pvBytes[8]);
        Assert.Equal((byte)MavParamType.Real32, pvBytes[24]);
        Assert.Equal(paramVal, ParamValueMessage.Decode(pvBytes));

        var paramSet = new ParamSetMessage(1, 1, "ARMING_CHECK", 1f, MavParamType.Int32);
        byte[] psBytes = paramSet.EncodePayload();
        Assert.Equal(23, psBytes.Length);
        Assert.Equal(1f, BinaryPrimitives.ReadSingleLittleEndian(psBytes.AsSpan(0, 4)));
        Assert.Equal(1, psBytes[4]);
        Assert.Equal(1, psBytes[5]);
        Assert.Equal((byte)'A', psBytes[6]);
        Assert.Equal((byte)MavParamType.Int32, psBytes[22]);
        Assert.Equal(paramSet, ParamSetMessage.Decode(psBytes));

        var ekf = new EkfStatusReportMessage(
            EkfStatusFlags.Attitude | EkfStatusFlags.PosHorizAbs,
            0.12f, 0.23f, 0.34f, 0.45f, 0.56f, 0.67f);
        byte[] ekfBytes = ekf.EncodePayload();
        Assert.Equal(26, ekfBytes.Length);
        Assert.Equal(0.12f, BinaryPrimitives.ReadSingleLittleEndian(ekfBytes.AsSpan(0, 4)));
        Assert.Equal(0.56f, BinaryPrimitives.ReadSingleLittleEndian(ekfBytes.AsSpan(16, 4)));
        Assert.Equal((ushort)(EkfStatusFlags.Attitude | EkfStatusFlags.PosHorizAbs),
            BinaryPrimitives.ReadUInt16LittleEndian(ekfBytes.AsSpan(20, 2)));
        Assert.Equal(0.67f, BinaryPrimitives.ReadSingleLittleEndian(ekfBytes.AsSpan(22, 4)));
        Assert.Equal(ekf, EkfStatusReportMessage.Decode(ekfBytes));

        var vib = new VibrationMessage(987654321UL, 1.1f, 2.2f, 3.3f, 10, 20, 30);
        byte[] vibBytes = vib.EncodePayload();
        Assert.Equal(32, vibBytes.Length);
        Assert.Equal(987654321UL, BinaryPrimitives.ReadUInt64LittleEndian(vibBytes.AsSpan(0, 8)));
        Assert.Equal(1.1f, BinaryPrimitives.ReadSingleLittleEndian(vibBytes.AsSpan(8, 4)));
        Assert.Equal(2.2f, BinaryPrimitives.ReadSingleLittleEndian(vibBytes.AsSpan(12, 4)));
        Assert.Equal(3.3f, BinaryPrimitives.ReadSingleLittleEndian(vibBytes.AsSpan(16, 4)));
        Assert.Equal(10u, BinaryPrimitives.ReadUInt32LittleEndian(vibBytes.AsSpan(20, 4)));
        Assert.Equal(20u, BinaryPrimitives.ReadUInt32LittleEndian(vibBytes.AsSpan(24, 4)));
        Assert.Equal(30u, BinaryPrimitives.ReadUInt32LittleEndian(vibBytes.AsSpan(28, 4)));
        Assert.Equal(vib, VibrationMessage.Decode(vibBytes));

        var extState = new ExtendedSysStateMessage(MavVtolState.TransitionToFw, MavLandedState.Takeoff);
        byte[] extBytes = extState.EncodePayload();
        Assert.Equal(2, extBytes.Length);
        Assert.Equal((byte)MavVtolState.TransitionToFw, extBytes[0]);
        Assert.Equal((byte)MavLandedState.Takeoff, extBytes[1]);
        Assert.Equal(extState, ExtendedSysStateMessage.Decode(extBytes));
    }

    [Fact]
    public void Parser_CapturesExactPacketAlignedRawBytes_ForV1AndV2ConcatenatedStream()
    {
        var hb = new HeartbeatMessage(MavType.Quadrotor, MavAutopilot.ArduPilotMega, MavModeFlag.CustomModeEnabled, 4u, MavState.Active);
        byte[] v2Packet = MavlinkEncoder.EncodeV2(1, 1, 5, hb);

        var ext = new ExtendedSysStateMessage(MavVtolState.Mc, MavLandedState.OnGround);
        byte[] v1Packet = BuildRawV1Frame(6, 1, 1, (byte)ExtendedSysStateMessage.MsgId, ext.EncodePayload(), ExtendedSysStateMessage.MsgCrcExtra);

        byte[] combined = [0x99, 0x88, ..v2Packet, ..v1Packet];

        var parser = new MavlinkParser();
        var eventRecords = new List<(MavlinkFrame Frame, byte[] Raw)>();
        parser.FrameWithRawBytesReceived += (frame, raw) => eventRecords.Add((frame, raw.ToArray()));

        List<MavlinkFrame> frames = parser.Feed(combined.AsSpan(0, 11)).ToList();
        frames.AddRange(parser.Feed(combined.AsSpan(11)));

        Assert.Equal(2, frames.Count);
        Assert.Equal(2, eventRecords.Count);

        Assert.Equal(v2Packet, frames[0].RawBytes.ToArray());
        Assert.Equal(v2Packet, frames[0].RawPacketBytes.ToArray());
        Assert.Equal(v2Packet, eventRecords[0].Raw);

        Assert.Equal(v1Packet, frames[1].RawBytes.ToArray());
        Assert.Equal(v1Packet, frames[1].RawPacketBytes.ToArray());
        Assert.Equal(v1Packet, eventRecords[1].Raw);
    }
}
