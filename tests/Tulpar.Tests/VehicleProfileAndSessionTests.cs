using Tulpar.Core.Mavlink;
using Tulpar.Core.Session;
using Tulpar.Core.Vehicles;

namespace Tulpar.Tests;

public class VehicleProfileAndSessionTests
{
    private static byte[] EncodeV2(byte sysId, byte compId, byte seq, IMavlinkMessage message) =>
        MavlinkEncoder.EncodeV2(sysId, compId, seq, message);

    private static uint PackPx4CustomMode(byte mainMode, byte subMode = 0) =>
        ((uint)mainMode << 16) | ((uint)subMode << 24);

    [Fact]
    public void Factory_DetectsAllFiveVehicleCategories_AndFallbackOnUnknownMavType()
    {
        IVehicleProfile fixedWing = VehicleProfileFactory.Create(MavType.FixedWing, MavAutopilot.ArduPilotMega);
        Assert.Equal(VehicleCategory.FixedWing, fixedWing.Category);
        Assert.True(fixedWing.ShowAirspeed);
        Assert.True(fixedWing.ShowHorizon);
        Assert.True(fixedWing.ShowAltitudeTape);
        Assert.False(fixedWing.ShowVtolTransition);
        Assert.False(fixedWing.SurfaceOnlyMode);
        Assert.Contains(MavCmd.NavLoiterTurns, fixedWing.SupportedMissionCommands);
        Assert.Contains(MavCmd.NavTakeoff, fixedWing.SupportedMissionCommands);

        foreach (MavType mrType in new[]
        {
            MavType.Quadrotor,
            MavType.Coaxial,
            MavType.Helicopter,
            MavType.Hexarotor,
            MavType.Octorotor,
            MavType.Tricopter,
            MavType.Dodecarotor,
            MavType.Decarotor
        })
        {
            IVehicleProfile multirotor = VehicleProfileFactory.Create(mrType, MavAutopilot.ArduPilotMega);
            Assert.Equal(VehicleCategory.Multirotor, multirotor.Category);
            Assert.False(multirotor.ShowAirspeed);
            Assert.True(multirotor.ShowHorizon);
            Assert.True(multirotor.ShowAltitudeTape);
            Assert.False(multirotor.ShowVtolTransition);
            Assert.False(multirotor.SurfaceOnlyMode);
            Assert.Contains(MavCmd.NavSplineWaypoint, multirotor.SupportedMissionCommands);
            Assert.Contains(MavCmd.NavRoi, multirotor.SupportedMissionCommands);
        }

        foreach (MavType vtolType in new[]
        {
            MavType.VtolTailsitterDuorotor,
            MavType.VtolTailsitterQuadrotor,
            MavType.VtolTiltrotor,
            MavType.VtolFixedrotor,
            MavType.VtolTailsitter,
            MavType.VtolTilthot,
            MavType.VtolReserved5
        })
        {
            IVehicleProfile vtol = VehicleProfileFactory.Create(vtolType, MavAutopilot.Px4);
            Assert.Equal(VehicleCategory.Vtol, vtol.Category);
            Assert.True(vtol.ShowAirspeed);
            Assert.True(vtol.ShowHorizon);
            Assert.True(vtol.ShowAltitudeTape);
            Assert.True(vtol.ShowVtolTransition);
            Assert.False(vtol.SurfaceOnlyMode);
            Assert.Contains(MavCmd.DoVtolTransition, vtol.SupportedMissionCommands);
        }

        IVehicleProfile rover = VehicleProfileFactory.Create(MavType.GroundRover, MavAutopilot.ArduPilotMega);
        Assert.Equal(VehicleCategory.RoverIka, rover.Category);
        Assert.False(rover.ShowAirspeed);
        Assert.False(rover.ShowHorizon);
        Assert.False(rover.ShowAltitudeTape);
        Assert.False(rover.ShowVtolTransition);
        Assert.True(rover.SurfaceOnlyMode);
        Assert.Contains(MavCmd.NavWaypoint, rover.SupportedMissionCommands);
        Assert.DoesNotContain(MavCmd.NavTakeoff, rover.SupportedMissionCommands);

        IVehicleProfile boat = VehicleProfileFactory.Create(MavType.SurfaceBoat, MavAutopilot.ArduPilotMega);
        Assert.Equal(VehicleCategory.BoatIda, boat.Category);
        Assert.False(boat.ShowAirspeed);
        Assert.False(boat.ShowHorizon);
        Assert.False(boat.ShowAltitudeTape);
        Assert.False(boat.ShowVtolTransition);
        Assert.True(boat.SurfaceOnlyMode);
        Assert.Contains(MavCmd.NavWaypoint, boat.SupportedMissionCommands);
        Assert.DoesNotContain(MavCmd.NavTakeoff, boat.SupportedMissionCommands);

        IVehicleProfile unknown = VehicleProfileFactory.Create((MavType)250, MavAutopilot.Generic);
        Assert.NotNull(unknown);
        Assert.Equal(VehicleCategory.Unknown, unknown.Category);
        Assert.Contains("UNKNOWN", unknown.DisplayLabel, StringComparison.OrdinalIgnoreCase);
        Assert.False(unknown.SurfaceOnlyMode);
        Assert.NotEmpty(unknown.SupportedMissionCommands);
    }

    [Fact]
    public void ArduPilot_And_PX4_CustomModes_ResolveAccuratelyPerPlatform()
    {
        var fixedWing = new FixedWingProfile();
        Assert.Equal("MANUAL", fixedWing.ResolveModeName(MavAutopilot.ArduPilotMega, 0, 1));
        Assert.Equal("FBWA", fixedWing.ResolveModeName(MavAutopilot.ArduPilotMega, 5, 1));
        Assert.Equal("CRUISE", fixedWing.ResolveModeName(MavAutopilot.ArduPilotMega, 7, 1));
        Assert.Equal("AUTO", fixedWing.ResolveModeName(MavAutopilot.ArduPilotMega, 10, 1));
        Assert.Equal("RTL", fixedWing.ResolveModeName(MavAutopilot.ArduPilotMega, 11, 1));
        Assert.Equal("LOITER", fixedWing.ResolveModeName(MavAutopilot.ArduPilotMega, 12, 1));
        Assert.Equal("TAKEOFF", fixedWing.ResolveModeName(MavAutopilot.ArduPilotMega, 13, 1));
        Assert.Equal("GUIDED", fixedWing.ResolveModeName(MavAutopilot.ArduPilotMega, 15, 1));
        Assert.Equal("QRTL", fixedWing.ResolveModeName(MavAutopilot.ArduPilotMega, 21, 1));

        var multirotor = new MultirotorProfile();
        Assert.Equal("STABILIZE", multirotor.ResolveModeName(MavAutopilot.ArduPilotMega, 0, 1));
        Assert.Equal("ALT_HOLD", multirotor.ResolveModeName(MavAutopilot.ArduPilotMega, 2, 1));
        Assert.Equal("AUTO", multirotor.ResolveModeName(MavAutopilot.ArduPilotMega, 3, 1));
        Assert.Equal("GUIDED", multirotor.ResolveModeName(MavAutopilot.ArduPilotMega, 4, 1));
        Assert.Equal("LOITER", multirotor.ResolveModeName(MavAutopilot.ArduPilotMega, 5, 1));
        Assert.Equal("RTL", multirotor.ResolveModeName(MavAutopilot.ArduPilotMega, 6, 1));
        Assert.Equal("POSHOLD", multirotor.ResolveModeName(MavAutopilot.ArduPilotMega, 16, 1));
        Assert.Equal("BRAKE", multirotor.ResolveModeName(MavAutopilot.ArduPilotMega, 17, 1));
        Assert.Equal("SMART_RTL", multirotor.ResolveModeName(MavAutopilot.ArduPilotMega, 21, 1));

        var vtol = new VtolProfile();
        Assert.Equal("MANUAL", vtol.ResolveModeName(MavAutopilot.ArduPilotMega, 0, 1));
        Assert.Equal("GUIDED", vtol.ResolveModeName(MavAutopilot.ArduPilotMega, 15, 1));
        Assert.Equal("QSTABILIZE", vtol.ResolveModeName(MavAutopilot.ArduPilotMega, 17, 1));
        Assert.Equal("QHOVER", vtol.ResolveModeName(MavAutopilot.ArduPilotMega, 18, 1));
        Assert.Equal("QLOITER", vtol.ResolveModeName(MavAutopilot.ArduPilotMega, 19, 1));
        Assert.Equal("QLAND", vtol.ResolveModeName(MavAutopilot.ArduPilotMega, 20, 1));
        Assert.Equal("QRTL", vtol.ResolveModeName(MavAutopilot.ArduPilotMega, 21, 1));

        var rover = new RoverIkaProfile();
        Assert.Equal("MANUAL", rover.ResolveModeName(MavAutopilot.ArduPilotMega, 0, 1));
        Assert.Equal("STEERING", rover.ResolveModeName(MavAutopilot.ArduPilotMega, 3, 1));
        Assert.Equal("HOLD", rover.ResolveModeName(MavAutopilot.ArduPilotMega, 4, 1));
        Assert.Equal("LOITER", rover.ResolveModeName(MavAutopilot.ArduPilotMega, 5, 1));
        Assert.Equal("AUTO", rover.ResolveModeName(MavAutopilot.ArduPilotMega, 10, 1));
        Assert.Equal("RTL", rover.ResolveModeName(MavAutopilot.ArduPilotMega, 11, 1));
        Assert.Equal("SMART_RTL", rover.ResolveModeName(MavAutopilot.ArduPilotMega, 12, 1));
        Assert.Equal("GUIDED", rover.ResolveModeName(MavAutopilot.ArduPilotMega, 15, 1));

        var boat = new BoatIdaProfile();
        Assert.Equal("STEERING", boat.ResolveModeName(MavAutopilot.ArduPilotMega, 3, 1));
        Assert.Equal("HOLD", boat.ResolveModeName(MavAutopilot.ArduPilotMega, 4, 1));
        Assert.Equal("GUIDED", boat.ResolveModeName(MavAutopilot.ArduPilotMega, 15, 1));

        // PX4 custom mode bitfield resolution across platforms
        Assert.Equal("MANUAL", multirotor.ResolveModeName(MavAutopilot.Px4, PackPx4CustomMode(1), 1));
        Assert.Equal("ALTCTL", multirotor.ResolveModeName(MavAutopilot.Px4, PackPx4CustomMode(2), 1));
        Assert.Equal("POSCTL", multirotor.ResolveModeName(MavAutopilot.Px4, PackPx4CustomMode(3), 1));
        Assert.Contains("READY", multirotor.ResolveModeName(MavAutopilot.Px4, PackPx4CustomMode(4, 1), 1), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TAKEOFF", multirotor.ResolveModeName(MavAutopilot.Px4, PackPx4CustomMode(4, 2), 1), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LOITER", multirotor.ResolveModeName(MavAutopilot.Px4, PackPx4CustomMode(4, 3), 1), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MISSION", multirotor.ResolveModeName(MavAutopilot.Px4, PackPx4CustomMode(4, 4), 1), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RTL", multirotor.ResolveModeName(MavAutopilot.Px4, PackPx4CustomMode(4, 5), 1), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LAND", multirotor.ResolveModeName(MavAutopilot.Px4, PackPx4CustomMode(4, 6), 1), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("ACRO", fixedWing.ResolveModeName(MavAutopilot.Px4, PackPx4CustomMode(5), 1));
        Assert.Equal("OFFBOARD", vtol.ResolveModeName(MavAutopilot.Px4, PackPx4CustomMode(6), 1));
        Assert.Equal("STABILIZED", rover.ResolveModeName(MavAutopilot.Px4, PackPx4CustomMode(7), 1));

        // Every returned FlightModeOption round-trips back to its own Name
        foreach (IVehicleProfile profile in new IVehicleProfile[] { fixedWing, multirotor, vtol, rover, boat })
        {
            foreach (MavAutopilot ap in new[] { MavAutopilot.ArduPilotMega, MavAutopilot.Px4 })
            {
                IReadOnlyList<FlightModeOption> modes = profile.GetFlightModes(ap);
                Assert.NotEmpty(modes);
                foreach (FlightModeOption mode in modes)
                {
                    Assert.Equal(mode.Name, profile.ResolveModeName(ap, mode.CustomMode, mode.BaseMode));
                }
            }
        }
    }

    [Fact]
    public void BuildGuidedTargetCommand_ProducesDeterministicPlatformSpecificFrames()
    {
        const double lat = 39.9250180;
        const double lon = 32.8369560;
        const float alt = 120.5f;

        // Multirotor ArduPilot -> SET_POSITION_TARGET_GLOBAL_INT
        var mr = new MultirotorProfile();
        MavlinkFrame mrFrame = mr.BuildGuidedTargetCommand(1, 1, 255, 190, 7, MavAutopilot.ArduPilotMega, lat, lon, alt);
        Assert.Equal((byte)255, mrFrame.SystemId);
        Assert.Equal((byte)190, mrFrame.ComponentId);
        Assert.Equal((byte)7, mrFrame.Sequence);
        Assert.Equal(SetPositionTargetGlobalIntMessage.MsgId, mrFrame.MessageId);
        var mrTarget = SetPositionTargetGlobalIntMessage.Decode(mrFrame);
        Assert.Equal(MavFrame.GlobalRelativeAltInt, mrTarget.CoordinateFrame);
        Assert.Equal(399250180, mrTarget.LatInt);
        Assert.Equal(328369560, mrTarget.LonInt);
        Assert.Equal(alt, mrTarget.Alt);
        Assert.False(mrTarget.TypeMask.HasFlag(PositionTargetTypemask.XIgnore));
        Assert.False(mrTarget.TypeMask.HasFlag(PositionTargetTypemask.YIgnore));
        Assert.False(mrTarget.TypeMask.HasFlag(PositionTargetTypemask.ZIgnore));
        Assert.True(mrTarget.TypeMask.HasFlag(PositionTargetTypemask.VxIgnore));
        Assert.True(mrTarget.TypeMask.HasFlag(PositionTargetTypemask.YawIgnore));

        // Rover/Boat ArduPilot -> SET_POSITION_TARGET_GLOBAL_INT with forced 0m surface altitude
        var boat = new BoatIdaProfile();
        MavlinkFrame boatFrame = boat.BuildGuidedTargetCommand(1, 1, 255, 190, 8, MavAutopilot.ArduPilotMega, lat, lon, 75f);
        Assert.Equal(SetPositionTargetGlobalIntMessage.MsgId, boatFrame.MessageId);
        var boatTarget = SetPositionTargetGlobalIntMessage.Decode(boatFrame);
        Assert.Equal(0f, boatTarget.Alt);

        // FixedWing/VTOL ArduPilot -> MISSION_ITEM_INT with current=2 (guided waypoint / loiter)
        var fw = new FixedWingProfile();
        MavlinkFrame fwFrame = fw.BuildGuidedTargetCommand(1, 1, 255, 190, 9, MavAutopilot.ArduPilotMega, lat, lon, alt, loiterRadius: 80f);
        Assert.Equal(MissionItemIntMessage.MsgId, fwFrame.MessageId);
        var fwItem = MissionItemIntMessage.Decode(fwFrame);
        Assert.Equal((byte)2, fwItem.Current);
        Assert.Equal(MavCmd.NavLoiterUnlim, fwItem.Command);
        Assert.Equal(80f, fwItem.Param3);
        Assert.Equal(399250180, fwItem.X);
        Assert.Equal(328369560, fwItem.Y);
        Assert.Equal(alt, fwItem.Z);

        // PX4 -> COMMAND_LONG MAV_CMD_DO_REPOSITION
        var vtol = new VtolProfile();
        MavlinkFrame px4Frame = vtol.BuildGuidedTargetCommand(1, 1, 255, 190, 10, MavAutopilot.Px4, lat, lon, alt, loiterRadius: 60f);
        Assert.Equal(CommandLongMessage.MsgId, px4Frame.MessageId);
        var cmd = CommandLongMessage.Decode(px4Frame);
        Assert.Equal(MavCmd.DoReposition, cmd.Command);
        Assert.Equal(-1f, cmd.Param1);
        Assert.Equal(1f, cmd.Param2);
        Assert.Equal(60f, cmd.Param3);
        Assert.True(float.IsNaN(cmd.Param4));
        Assert.Equal((float)lat, cmd.Param5);
        Assert.Equal((float)lon, cmd.Param6);
        Assert.Equal(alt, cmd.Param7);
    }

    [Fact]
    public void Session_DecodesTelemetryAndEvents_IgnoringGcsAndAuxHeartbeats()
    {
        var session = new VehicleSession();
        var snapshots = new List<TelemetrySnapshot>();
        var statusTexts = new List<StatusTextMessage>();
        var rawFrames = new List<MavlinkFrame>();
        var acks = new List<MavlinkFrame>();

        session.Bus.SnapshotUpdated += snapshots.Add;
        session.Bus.StatusTextReceived += statusTexts.Add;
        session.Bus.RawFrameReceived += rawFrames.Add;
        session.Bus.CommandAckReceived += acks.Add;

        // GCS heartbeat (must be ignored for vehicle identity)
        byte[] gcsHb = EncodeV2(255, 190, 1, new HeartbeatMessage(
            MavType.Gcs, MavAutopilot.Invalid, MavModeFlag.None, 0, MavState.Active));

        // Real FixedWing ArduPilot vehicle heartbeat
        byte[] vehicleHb = EncodeV2(1, 1, 2, new HeartbeatMessage(
            MavType.FixedWing,
            MavAutopilot.ArduPilotMega,
            MavModeFlag.CustomModeEnabled | MavModeFlag.SafetyArmed,
            15u, // GUIDED
            MavState.Active));

        // Companion camera heartbeat on same sysId (must NOT clobber FixedWing profile)
        byte[] camHb = EncodeV2(1, 100, 3, new HeartbeatMessage(
            MavType.Camera, MavAutopilot.Invalid, MavModeFlag.None, 0, MavState.Active));

        byte[] sysStatus = EncodeV2(1, 1, 4, new SysStatusMessage(0, 0, 0, 300, 22400, 1550, 74));
        byte[] gps = EncodeV2(1, 1, 5, new GpsRawIntMessage(1000UL, GpsFixType.RtkFixed, 399250180, 328369560, 850000, 85, 110, 1800, 9000, 16));
        byte[] attitude = EncodeV2(1, 1, 6, new AttitudeMessage(500u, (float)(Math.PI / 6.0), (float)(-Math.PI / 12.0), (float)(Math.PI / 2.0)));
        byte[] pos = EncodeV2(1, 1, 7, new GlobalPositionIntMessage(500u, 399250180, 328369560, 925500, 125250, 1500, 0, -250, 18050));
        byte[] hud = EncodeV2(1, 1, 8, new VfrHudMessage(24.5f, 21.0f, 181, 68, 925.5f, 2.5f));
        byte[] radio = EncodeV2(1, 1, 9, new RadioStatusMessage(190, 182, 80, 30, 35, 1, 0));
        byte[] home = EncodeV2(1, 1, 10, new HomePositionMessage(399200000, 328300000, 800000));
        byte[] missionCur = EncodeV2(1, 1, 11, new MissionCurrentMessage(3));
        byte[] status = EncodeV2(1, 1, 12, new StatusTextMessage(MavSeverity.Info, "GUIDED MODE ENGAGED"));
        byte[] ack = EncodeV2(1, 1, 13, new CommandAckMessage(MavCmd.ComponentArmDisarm, MavResult.Accepted));

        byte[] stream = [..gcsHb, ..vehicleHb, ..camHb, ..sysStatus, ..gps, ..attitude, ..pos, ..hud, ..radio, ..home, ..missionCur, ..status, ..ack];

        // Feed in split chunks to confirm streaming parser integration
        session.ProcessIncomingBytes(stream.AsSpan(0, 27));
        session.ProcessIncomingBytes(stream.AsSpan(27));

        Assert.Equal(13, rawFrames.Count);
        Assert.Single(statusTexts);
        Assert.Equal("GUIDED MODE ENGAGED", statusTexts[0].Text);
        Assert.Single(acks);
        Assert.Equal(MavCmd.ComponentArmDisarm, CommandAckMessage.Decode(acks[0]).Command);

        TelemetrySnapshot cur = session.Current;
        Assert.True(cur.IsConnected);
        Assert.False(cur.LinkLost);
        Assert.Equal((byte)1, cur.SystemId);
        Assert.Equal((byte)1, cur.ComponentId);
        Assert.Equal(MavType.FixedWing, cur.VehicleType);
        Assert.Equal(MavAutopilot.ArduPilotMega, cur.Autopilot);
        Assert.Equal(VehicleCategory.FixedWing, cur.VehicleCategory);
        Assert.NotNull(session.ActiveProfile);
        Assert.Equal(VehicleCategory.FixedWing, session.ActiveProfile!.Category);
        Assert.Equal("GUIDED", cur.ModeName);
        Assert.True(cur.IsArmed);

        Assert.Equal(22.4, cur.BatteryVoltageVolts, 3);
        Assert.Equal(15.5, cur.BatteryCurrentAmps, 3);
        Assert.Equal(74, cur.BatteryRemainingPercent);

        Assert.Equal(GpsFixType.RtkFixed, cur.GpsFixType);
        Assert.Equal(16, cur.SatellitesVisible);
        Assert.Equal(0.85, cur.Hdop, 3);

        Assert.Equal(30.0, cur.RollDeg, 2);
        Assert.Equal(-15.0, cur.PitchDeg, 2);
        Assert.Equal(90.0, cur.YawDeg, 2);

        Assert.Equal(39.9250180, cur.Latitude, 7);
        Assert.Equal(32.8369560, cur.Longitude, 7);
        Assert.Equal(925.5, cur.AltitudeAmslMeters, 3);
        Assert.Equal(125.25, cur.AltitudeRelativeMeters, 3);

        Assert.Equal(24.5, cur.AirspeedMps, 2);
        Assert.Equal(21.0, cur.GroundspeedMps, 2);
        Assert.Equal(2.5, cur.VerticalSpeedMps, 2);
        Assert.Equal(181.0, cur.HeadingDeg, 2);
        Assert.Equal(68, cur.ThrottlePercent);

        Assert.Equal(190, cur.RadioRssi);
        Assert.Equal(182, cur.RemoteRssi);

        Assert.True(cur.HasHome);
        Assert.Equal(39.9200000, cur.HomeLatitude, 7);
        Assert.Equal(32.8300000, cur.HomeLongitude, 7);
        Assert.Equal(800.0, cur.HomeAltitudeMeters, 3);
        Assert.Equal(3, cur.ActiveWaypointSeq);
        Assert.NotEmpty(snapshots);
    }

    [Fact]
    public void Session_ReconnectWithDifferentVehicle_ResetsStalePositionHomeAndUpdatesProfile()
    {
        var session = new VehicleSession();

        byte[] vehicle1Frames =
        [
            ..EncodeV2(1, 1, 1, new HeartbeatMessage(MavType.Quadrotor, MavAutopilot.ArduPilotMega, MavModeFlag.CustomModeEnabled | MavModeFlag.SafetyArmed, 5u, MavState.Active)),
            ..EncodeV2(1, 1, 2, new GlobalPositionIntMessage(100u, 399250180, 328369560, 900000, 50000, 100, 200, -50, 12000)),
            ..EncodeV2(1, 1, 3, new HomePositionMessage(399200000, 328300000, 850000)),
            ..EncodeV2(1, 1, 4, new AttitudeMessage(100u, 0.4f, -0.2f, 1.0f)),
            ..EncodeV2(1, 1, 5, new SysStatusMessage(0, 0, 0, 200, 16800, 1200, 88)),
            ..EncodeV2(1, 1, 6, new GpsRawIntMessage(100UL, GpsFixType.Fix3D, 399250180, 328369560, 900000, 110, 150, 100, 12000, 12)),
            ..EncodeV2(1, 1, 7, new VfrHudMessage(0f, 5.5f, 120, 45, 900f, 1.2f)),
            ..EncodeV2(1, 1, 8, new RadioStatusMessage(170, 165, 90, 20, 25, 0, 0)),
            ..EncodeV2(1, 1, 9, new MissionCurrentMessage(4))
        ];

        session.ProcessIncomingBytes(vehicle1Frames);
        Assert.Equal(VehicleCategory.Multirotor, session.Current.VehicleCategory);
        Assert.True(session.Current.HasHome);
        Assert.Equal(4, session.Current.ActiveWaypointSeq);
        Assert.Equal(16.8, session.Current.BatteryVoltageVolts, 3);

        // New vehicle connects on sysId=2 as PX4 FixedWing without disconnecting transport
        byte[] vehicle2Hb = EncodeV2(2, 1, 1, new HeartbeatMessage(
            MavType.FixedWing,
            MavAutopilot.Px4,
            MavModeFlag.CustomModeEnabled,
            PackPx4CustomMode(4, 4), // AUTO_MISSION
            MavState.Standby));

        session.ProcessIncomingBytes(vehicle2Hb);

        TelemetrySnapshot afterSwap = session.Current;
        Assert.True(afterSwap.IsConnected);
        Assert.Equal((byte)2, afterSwap.SystemId);
        Assert.Equal(MavType.FixedWing, afterSwap.VehicleType);
        Assert.Equal(MavAutopilot.Px4, afterSwap.Autopilot);
        Assert.Equal(VehicleCategory.FixedWing, afterSwap.VehicleCategory);
        Assert.Equal(VehicleCategory.FixedWing, session.ActiveProfile!.Category);
        Assert.Contains("MISSION", afterSwap.ModeName, StringComparison.OrdinalIgnoreCase);
        Assert.False(afterSwap.IsArmed);

        // All stale state from vehicle 1 must be wiped
        Assert.False(afterSwap.HasHome);
        Assert.Equal(0.0, afterSwap.HomeLatitude);
        Assert.Equal(0.0, afterSwap.HomeLongitude);
        Assert.Equal(0.0, afterSwap.HomeAltitudeMeters);
        Assert.Equal(0.0, afterSwap.Latitude);
        Assert.Equal(0.0, afterSwap.Longitude);
        Assert.Equal(0.0, afterSwap.AltitudeRelativeMeters);
        Assert.Equal(0.0, afterSwap.AltitudeAmslMeters);
        Assert.Equal(0.0, afterSwap.RollDeg);
        Assert.Equal(0.0, afterSwap.PitchDeg);
        Assert.Equal(0.0, afterSwap.YawDeg);
        Assert.Equal(0.0, afterSwap.BatteryVoltageVolts);
        Assert.Equal(0.0, afterSwap.BatteryCurrentAmps);
        Assert.Equal(-1, afterSwap.BatteryRemainingPercent);
        Assert.Equal(GpsFixType.NoGps, afterSwap.GpsFixType);
        Assert.Equal(0, afterSwap.SatellitesVisible);
        Assert.Equal(0.0, afterSwap.AirspeedMps);
        Assert.Equal(0.0, afterSwap.GroundspeedMps);
        Assert.Equal(0, afterSwap.ThrottlePercent);
        Assert.Equal(0, afterSwap.RadioRssi);
        Assert.Equal(0, afterSwap.RemoteRssi);
        Assert.Equal(0, afterSwap.ActiveWaypointSeq);

        // Even if same sysId=2 reconnects as a SurfaceBoat, state resets and profile adapts
        session.ProcessIncomingBytes(EncodeV2(2, 1, 2, new MissionCurrentMessage(9)));
        Assert.Equal(9, session.Current.ActiveWaypointSeq);

        byte[] sameSysIdBoatHb = EncodeV2(2, 1, 3, new HeartbeatMessage(
            MavType.SurfaceBoat, MavAutopilot.ArduPilotMega, MavModeFlag.CustomModeEnabled, 3u, MavState.Active));
        session.ProcessIncomingBytes(sameSysIdBoatHb);

        Assert.Equal(VehicleCategory.BoatIda, session.Current.VehicleCategory);
        Assert.Equal("STEERING", session.Current.ModeName);
        Assert.Equal(0, session.Current.ActiveWaypointSeq);
    }

    [Fact]
    public void Session_HeartbeatTimeoutOverThreeSeconds_MarksLinkLost()
    {
        var session = new VehicleSession();
        var emitted = new List<TelemetrySnapshot>();
        session.Bus.SnapshotUpdated += emitted.Add;

        // Before any heartbeat, timeout check does nothing
        session.CheckHeartbeatTimeout(DateTimeOffset.UtcNow.AddSeconds(10));
        Assert.False(session.Current.LinkLost);
        Assert.Empty(emitted);

        byte[] hb = EncodeV2(1, 1, 1, new HeartbeatMessage(
            MavType.Quadrotor, MavAutopilot.ArduPilotMega, MavModeFlag.CustomModeEnabled, 5u, MavState.Active));
        session.ProcessIncomingBytes(hb);

        DateTimeOffset lastHb = session.Current.LastHeartbeatUtc;
        Assert.NotEqual(default, lastHb);
        Assert.False(session.Current.LinkLost);
        emitted.Clear();

        // <= 3.0s must not trigger LinkLost
        session.CheckHeartbeatTimeout(lastHb.AddSeconds(3.0));
        Assert.False(session.Current.LinkLost);
        Assert.Empty(emitted);

        // > 3.0s must mark LinkLost = true and emit a snapshot update
        session.CheckHeartbeatTimeout(lastHb.AddSeconds(3.05));
        Assert.True(session.Current.LinkLost);
        Assert.Single(emitted);
        Assert.True(emitted[0].LinkLost);

        // Repeated checks while already LinkLost must not spam duplicate updates
        session.CheckHeartbeatTimeout(lastHb.AddSeconds(5.0));
        Assert.Single(emitted);

        // Fresh heartbeat clears LinkLost
        session.ProcessIncomingBytes(hb);
        Assert.False(session.Current.LinkLost);
        Assert.Equal(2, emitted.Count);

        // ResetOnDisconnect clears connection, profile, and telemetry cleanly
        session.ResetOnDisconnect();
        Assert.False(session.Current.IsConnected);
        Assert.False(session.Current.LinkLost);
        Assert.Null(session.ActiveProfile);
        Assert.Equal((byte)0, session.Current.SystemId);
        Assert.Equal(VehicleCategory.Unknown, session.Current.VehicleCategory);
        Assert.Equal(default, session.Current.LastHeartbeatUtc);
    }

    [Fact]
    public void Session_DecodesBatteryStatus_DerivesGlobalPositionVelocity_AndFiltersForeignTelemetry()
    {
        var session = new VehicleSession();

        byte[] hb = EncodeV2(1, 1, 1, new HeartbeatMessage(
            MavType.Quadrotor, MavAutopilot.ArduPilotMega, MavModeFlag.CustomModeEnabled, 4u, MavState.Active));

        // Before VFR_HUD arrives, GlobalPositionInt Vx/Vy/Vz derives GroundspeedMps and VerticalSpeedMps (NED -Vz)
        byte[] pos = EncodeV2(1, 1, 2, new GlobalPositionIntMessage(
            TimeBootMs: 100u,
            Lat: 410082376,
            Lon: 289783589,
            Alt: 150000,
            RelativeAlt: 50000,
            Vx: 300,
            Vy: 400,
            Vz: -250,
            Hdg: 9000));

        // Smart battery with 6S cells across Voltages + VoltagesExt (4150mV x 6 = 24.90V)
        byte[] bat = EncodeV2(1, 1, 3, new BatteryStatusMessage(
            Id: 0,
            BatteryFunction: MavBatteryFunction.Propulsion,
            Type: MavBatteryType.Lipo,
            Temperature: 2500,
            Voltages: [4150, 4150, 4150, 4150, 4150, 65535, 65535, 65535, 65535, 65535],
            CurrentBattery: 2250,
            CurrentConsumed: 1200,
            EnergyConsumed: 25000,
            BatteryRemaining: 85,
            TimeRemaining: 600,
            ChargeState: MavBatteryChargeState.Ok,
            VoltagesExt: [4150, 0, 0, 0]));

        // Telemetry from another system ID (sysId=9) without heartbeat must NOT overwrite sysId=1 telemetry
        byte[] foreignPos = EncodeV2(9, 1, 1, new GlobalPositionIntMessage(
            TimeBootMs: 200u,
            Lat: -100000000,
            Lon: -100000000,
            Alt: 999000,
            RelativeAlt: 888000,
            Vx: 0,
            Vy: 0,
            Vz: 0,
            Hdg: 0));

        session.ProcessIncomingBytes([..hb, ..pos, ..bat, ..foreignPos]);

        TelemetrySnapshot cur = session.Current;
        Assert.Equal(5.0, cur.GroundspeedMps, 3);
        Assert.Equal(2.5, cur.VerticalSpeedMps, 3);
        Assert.Equal(24.9, cur.BatteryVoltageVolts, 3);
        Assert.Equal(22.5, cur.BatteryCurrentAmps, 3);
        Assert.Equal(85, cur.BatteryRemainingPercent);
        Assert.Equal(41.0082376, cur.Latitude, 7);
        Assert.Equal(28.9783589, cur.Longitude, 7);

        // Validate BuildGuidedTargetCommand rejects non-finite or out-of-range coordinates
        IVehicleProfile profile = session.ActiveProfile!;
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            profile.BuildGuidedTargetCommand(1, 1, 255, 190, 1, MavAutopilot.ArduPilotMega, 95.0, 28.0, 50f));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            profile.BuildGuidedTargetCommand(1, 1, 255, 190, 1, MavAutopilot.ArduPilotMega, 41.0, double.NaN, 50f));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            profile.BuildGuidedTargetCommand(1, 1, 255, 190, 1, MavAutopilot.ArduPilotMega, 41.0, 28.0, float.PositiveInfinity));
    }

    [Fact]
    public void Session_IgnoresPreHeartbeatTelemetryAndZeroSystemIdHeartbeat()
    {
        var session = new VehicleSession();
        var snapshots = new List<TelemetrySnapshot>();
        var statuses = new List<StatusTextMessage>();
        var acks = new List<MavlinkFrame>();

        session.Bus.SnapshotUpdated += snapshots.Add;
        session.Bus.StatusTextReceived += statuses.Add;
        session.Bus.CommandAckReceived += acks.Add;

        // Pre-heartbeat telemetry from sysId=2 and malformed sysId=0 heartbeat must be ignored
        byte[] preHeartbeatStream =
        [
            ..EncodeV2(2, 1, 1, new GlobalPositionIntMessage(100u, 399250180, 328369560, 900000, 50000, 500, 0, -100, 9000)),
            ..EncodeV2(2, 1, 2, new HomePositionMessage(399200000, 328300000, 850000)),
            ..EncodeV2(2, 1, 3, new AttitudeMessage(100u, 0.5f, 0.25f, 1.0f)),
            ..EncodeV2(2, 1, 4, new SysStatusMessage(0, 0, 0, 100, 16800, 1200, 80)),
            ..EncodeV2(2, 1, 5, new VfrHudMessage(20f, 21f, 90, 50, 900f, 1f)),
            ..EncodeV2(2, 1, 6, new StatusTextMessage(MavSeverity.Warning, "STALE STATUS")),
            ..EncodeV2(2, 1, 7, new CommandAckMessage(MavCmd.ComponentArmDisarm, MavResult.Accepted)),
            ..EncodeV2(0, 1, 8, new HeartbeatMessage(MavType.Quadrotor, MavAutopilot.ArduPilotMega, MavModeFlag.CustomModeEnabled, 4u, MavState.Active))
        ];

        session.ProcessIncomingBytes(preHeartbeatStream);

        Assert.False(session.Current.IsConnected);
        Assert.Equal((byte)0, session.Current.SystemId);
        Assert.Null(session.ActiveProfile);
        Assert.Empty(snapshots);
        Assert.Empty(statuses);
        Assert.Empty(acks);

        // First real vehicle heartbeat on sysId=1 must start completely clean
        byte[] vehicle1Hb = EncodeV2(1, 1, 1, new HeartbeatMessage(
            MavType.FixedWing, MavAutopilot.ArduPilotMega, MavModeFlag.CustomModeEnabled, 15u, MavState.Active));
        session.ProcessIncomingBytes(vehicle1Hb);

        TelemetrySnapshot cur = session.Current;
        Assert.True(cur.IsConnected);
        Assert.Equal((byte)1, cur.SystemId);
        Assert.Equal(VehicleCategory.FixedWing, cur.VehicleCategory);
        Assert.False(cur.HasHome);
        Assert.Equal(0.0, cur.Latitude);
        Assert.Equal(0.0, cur.Longitude);
        Assert.Equal(0.0, cur.RollDeg);
        Assert.Equal(0.0, cur.BatteryVoltageVolts);
        Assert.Equal(0.0, cur.AirspeedMps);
        Assert.Single(snapshots);
    }

    [Fact]
    public void Session_IgnoresAuxiliaryAndNonAutopilotHeartbeatsEvenWithValidAutopilot()
    {
        var session = new VehicleSession();

        // Auxiliary and invalid-autopilot tracker heartbeats prior to aircraft must not claim initial lock
        byte[] preLockAux =
        [
            ..EncodeV2(1, 100, 1, new HeartbeatMessage(MavType.OnboardController, MavAutopilot.Generic, MavModeFlag.None, 0u, MavState.Active)),
            ..EncodeV2(3, 1, 1, new HeartbeatMessage(MavType.AntennaTracker, MavAutopilot.Invalid, MavModeFlag.None, 0u, MavState.Active))
        ];
        session.ProcessIncomingBytes(preLockAux);
        Assert.False(session.Current.IsConnected);
        Assert.Equal((byte)0, session.Current.SystemId);

        // Real flight controller connects and reports position + home
        byte[] aircraftInit =
        [
            ..EncodeV2(1, 1, 1, new HeartbeatMessage(MavType.Quadrotor, MavAutopilot.ArduPilotMega, MavModeFlag.CustomModeEnabled | MavModeFlag.SafetyArmed, 4u, MavState.Active)),
            ..EncodeV2(1, 1, 2, new GlobalPositionIntMessage(100u, 410082376, 289783589, 120000, 40000)),
            ..EncodeV2(1, 1, 3, new HomePositionMessage(410080000, 289780000, 80000))
        ];
        session.ProcessIncomingBytes(aircraftInit);

        // Companion OnboardController (Generic autopilot), Camera (Generic autopilot), non-Autopilot1 component on sysId=1,
        // and invalid-autopilot AntennaTracker must NOT trigger a vehicle swap or wipe telemetry
        byte[] postLockAux =
        [
            ..EncodeV2(1, 100, 2, new HeartbeatMessage(MavType.OnboardController, MavAutopilot.Generic, MavModeFlag.None, 0u, MavState.Active)),
            ..EncodeV2(1, 154, 3, new HeartbeatMessage(MavType.Camera, MavAutopilot.Generic, MavModeFlag.None, 0u, MavState.Active)),
            ..EncodeV2(1, 191, 4, new HeartbeatMessage(MavType.Generic, MavAutopilot.Generic, MavModeFlag.None, 0u, MavState.Active)),
            ..EncodeV2(3, 1, 2, new HeartbeatMessage(MavType.AntennaTracker, MavAutopilot.Invalid, MavModeFlag.None, 0u, MavState.Active))
        ];
        session.ProcessIncomingBytes(postLockAux);

        TelemetrySnapshot cur = session.Current;
        Assert.Equal((byte)1, cur.SystemId);
        Assert.Equal((byte)1, cur.ComponentId);
        Assert.Equal(MavType.Quadrotor, cur.VehicleType);
        Assert.Equal(MavAutopilot.ArduPilotMega, cur.Autopilot);
        Assert.Equal(VehicleCategory.Multirotor, cur.VehicleCategory);
        Assert.Equal("GUIDED", cur.ModeName);
        Assert.True(cur.IsArmed);
        Assert.True(cur.HasHome);
        Assert.Equal(41.0082376, cur.Latitude, 7);
    }

    [Fact]
    public void Session_IgnoresForeignStatusTextAndCommandAckFromOtherSystemIds()
    {
        var session = new VehicleSession();
        var statuses = new List<StatusTextMessage>();
        var acks = new List<MavlinkFrame>();

        session.Bus.StatusTextReceived += statuses.Add;
        session.Bus.CommandAckReceived += acks.Add;

        byte[] hb = EncodeV2(1, 1, 1, new HeartbeatMessage(
            MavType.Quadrotor, MavAutopilot.ArduPilotMega, MavModeFlag.CustomModeEnabled, 5u, MavState.Active));

        // Foreign system (sysId=2) STATUSTEXT and COMMAND_ACK must be filtered out
        byte[] foreignStatus = EncodeV2(2, 1, 1, new StatusTextMessage(MavSeverity.Critical, "FOREIGN FAILSAFE"));
        byte[] foreignAck = EncodeV2(2, 1, 2, new CommandAckMessage(MavCmd.NavReturnToLaunch, MavResult.Denied));

        // Active vehicle (sysId=1) companion STATUSTEXT and autopilot COMMAND_ACK must be delivered
        byte[] localCompanionStatus = EncodeV2(1, 100, 1, new StatusTextMessage(MavSeverity.Info, "COMPANION READY"));
        byte[] localAck = EncodeV2(1, 1, 2, new CommandAckMessage(MavCmd.ComponentArmDisarm, MavResult.Accepted));

        session.ProcessIncomingBytes([..hb, ..foreignStatus, ..foreignAck, ..localCompanionStatus, ..localAck]);

        StatusTextMessage receivedStatus = Assert.Single(statuses);
        Assert.Equal("COMPANION READY", receivedStatus.Text);

        MavlinkFrame receivedAck = Assert.Single(acks);
        Assert.Equal((byte)1, receivedAck.SystemId);
        Assert.Equal(MavCmd.ComponentArmDisarm, CommandAckMessage.Decode(receivedAck).Command);
    }
}
