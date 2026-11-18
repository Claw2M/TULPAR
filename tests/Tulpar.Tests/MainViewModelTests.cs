using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.IO.Packaging;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Tulpar.App.Controls;
using Tulpar.App.Services;
using Tulpar.App.Themes;
using Tulpar.App.ViewModels;
using Tulpar.Core.Mavlink;
using Tulpar.Core.Missions;
using Tulpar.Core.Session;
using Tulpar.Core.Transport;
using Tulpar.Core.Vehicles;

namespace Tulpar.Tests;

public class MainViewModelTests
{
    private static byte[] EncodeHeartbeat(
        byte sysId,
        MavType type,
        MavAutopilot autopilot,
        uint customMode,
        bool armed = false,
        byte compId = 1,
        byte seq = 1)
    {
        MavModeFlag flags = MavModeFlag.CustomModeEnabled;
        if (armed)
        {
            flags |= MavModeFlag.SafetyArmed;
        }

        return MavlinkEncoder.EncodeV2(
            sysId,
            compId,
            seq,
            new HeartbeatMessage(type, autopilot, flags, customMode, MavState.Active));
    }

    private static byte[] EncodeGlobalPosition(
        byte sysId,
        double lat,
        double lon,
        double relAltMeters = 50.0,
        double amslAltMeters = 900.0,
        ushort headingCdeg = 9000,
        byte seq = 2) =>
        MavlinkEncoder.EncodeV2(
            sysId,
            1,
            seq,
            new GlobalPositionIntMessage(
                TimeBootMs: 1000u,
                Lat: (int)Math.Round(lat * 1e7),
                Lon: (int)Math.Round(lon * 1e7),
                Alt: (int)Math.Round(amslAltMeters * 1000.0),
                RelativeAlt: (int)Math.Round(relAltMeters * 1000.0),
                Vx: 500,
                Vy: 0,
                Vz: 0,
                Hdg: headingCdeg));

    private static MainViewModel CreateTestViewModel(FakeTransport transport, out FakeTransportFactory factory)
    {
        factory = new FakeTransportFactory(transport);
        var options = new MainViewModelOptions
        {
            TelemetryLogDirectory = null,
            EnableUdpForwarder = false,
            HudThrottleInterval = TimeSpan.Zero,
            PanelThrottleInterval = TimeSpan.Zero,
            HeartbeatCheckInterval = TimeSpan.FromSeconds(30),
            SerialPortEnumerator = () => ["COM3", "COM7"],
            VideoDeviceEnumerator = () => ["Integrated Camera"]
        };

        return new MainViewModel(factory, new InlineUiDispatcher(), options);
    }

    [Fact]
    public async Task SafetyGuards_ArmRtlAndGuidedFlyHere_RequireConfirmationBeforeSendingCommand()
    {
        var transport = new FakeTransport();
        await using MainViewModel vm = CreateTestViewModel(transport, out _);

        vm.SelectedConnectionKind = ConnectionKind.Udp;
        await vm.ConnectAsync();
        Assert.True(vm.IsTransportConnected);

        // Identify active MultiRotor ArduPilot vehicle
        transport.EmitIncoming(EncodeHeartbeat(1, MavType.Quadrotor, MavAutopilot.ArduPilotMega, customMode: 4u, armed: false));
        transport.ClearSent();

        // 1. RequestArmOrDisarm -> MUST NOT send COMMAND_LONG until confirmed
        vm.RequestArmOrDisarm();
        Assert.True(vm.HasPendingSafetyAction);
        Assert.NotNull(vm.PendingSafetyAction);
        Assert.Equal(SafetyActionKind.Arm, vm.PendingSafetyAction!.Kind);
        Assert.Empty(transport.GetSentFramesExcludingHeartbeat());

        // Cancel clears pending action without sending
        vm.CancelSafetyAction();
        Assert.False(vm.HasPendingSafetyAction);
        Assert.Null(vm.PendingSafetyAction);
        Assert.Empty(transport.GetSentFramesExcludingHeartbeat());

        // Stage ARM again, verify zero commands sent, then confirm
        vm.RequestArmOrDisarm();
        Assert.True(vm.HasPendingSafetyAction);
        Assert.Empty(transport.GetSentFramesExcludingHeartbeat());

        await vm.ConfirmSafetyActionAsync();
        Assert.False(vm.HasPendingSafetyAction);
        MavlinkFrame armFrame = Assert.Single(transport.GetSentFramesExcludingHeartbeat());
        Assert.Equal(CommandLongMessage.MsgId, armFrame.MessageId);
        CommandLongMessage armCmd = CommandLongMessage.Decode(armFrame);
        Assert.Equal(MavCmd.ComponentArmDisarm, armCmd.Command);
        Assert.Equal(1f, armCmd.Param1);

        // Armed telemetry switches RequestArmOrDisarm to Disarm (also confirmation-guarded)
        transport.EmitIncoming(EncodeHeartbeat(1, MavType.Quadrotor, MavAutopilot.ArduPilotMega, customMode: 4u, armed: true));
        transport.ClearSent();

        vm.RequestArmOrDisarm();
        Assert.Equal(SafetyActionKind.Disarm, vm.PendingSafetyAction!.Kind);
        Assert.Empty(transport.GetSentFramesExcludingHeartbeat());

        await vm.ConfirmSafetyActionAsync();
        MavlinkFrame disarmFrame = Assert.Single(transport.GetSentFramesExcludingHeartbeat());
        CommandLongMessage disarmCmd = CommandLongMessage.Decode(disarmFrame);
        Assert.Equal(MavCmd.ComponentArmDisarm, disarmCmd.Command);
        Assert.Equal(0f, disarmCmd.Param1);

        // 2. RequestRtl -> MUST NOT send COMMAND_LONG until confirmed
        transport.ClearSent();
        vm.RequestRtl();
        Assert.True(vm.HasPendingSafetyAction);
        Assert.Equal(SafetyActionKind.Rtl, vm.PendingSafetyAction!.Kind);
        Assert.Empty(transport.GetSentFramesExcludingHeartbeat());

        await vm.ConfirmSafetyActionAsync();
        Assert.False(vm.HasPendingSafetyAction);
        MavlinkFrame rtlFrame = Assert.Single(transport.GetSentFramesExcludingHeartbeat());
        Assert.Equal(CommandLongMessage.MsgId, rtlFrame.MessageId);
        Assert.Equal(MavCmd.NavReturnToLaunch, CommandLongMessage.Decode(rtlFrame).Command);

        // 3. RequestGuidedFlyHere -> MUST NOT send target until confirmed
        transport.ClearSent();
        const double targetLat = 39.9250180;
        const double targetLon = 32.8369560;
        const float targetAlt = 120f;
        vm.RequestGuidedFlyHere(targetLat, targetLon, targetAlt);
        Assert.True(vm.HasPendingSafetyAction);
        Assert.Equal(SafetyActionKind.GuidedFlyHere, vm.PendingSafetyAction!.Kind);
        Assert.Empty(transport.GetSentFramesExcludingHeartbeat());

        await vm.ConfirmSafetyActionAsync();
        Assert.False(vm.HasPendingSafetyAction);
        MavlinkFrame guidedFrame = Assert.Single(transport.GetSentFramesExcludingHeartbeat());
        Assert.Equal(SetPositionTargetGlobalIntMessage.MsgId, guidedFrame.MessageId);
        SetPositionTargetGlobalIntMessage targetMsg = SetPositionTargetGlobalIntMessage.Decode(guidedFrame);
        Assert.Equal(399250180, targetMsg.LatInt);
        Assert.Equal(328369560, targetMsg.LonInt);
        Assert.Equal(targetAlt, targetMsg.Alt);

        // 4. RequestTakeoff in STABILIZE -> confirmation-guarded, then switches to GUIDED before NavTakeoff
        transport.EmitIncoming(EncodeHeartbeat(1, MavType.Quadrotor, MavAutopilot.ArduPilotMega, customMode: 0u, armed: true));
        transport.ClearSent();
        vm.TakeoffAltitudeMeters = 12.0;
        vm.RequestTakeoff();
        Assert.True(vm.HasPendingSafetyAction);
        Assert.Equal(SafetyActionKind.Takeoff, vm.PendingSafetyAction!.Kind);
        Assert.Equal(12f, vm.PendingSafetyAction!.AltitudeMeters);
        Assert.Contains("GUIDED", vm.PendingSafetyAction!.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(transport.GetSentFramesExcludingHeartbeat());

        await vm.ConfirmSafetyActionAsync();
        Assert.False(vm.HasPendingSafetyAction);
        IReadOnlyList<MavlinkFrame> takeoffFrames = transport.GetSentFramesExcludingHeartbeat();
        Assert.Equal(2, takeoffFrames.Count);
        Assert.Equal(SetModeMessage.MsgId, takeoffFrames[0].MessageId);
        SetModeMessage guidedSwitch = SetModeMessage.Decode(takeoffFrames[0]);
        Assert.Equal(4u, guidedSwitch.CustomMode);
        Assert.Equal(CommandLongMessage.MsgId, takeoffFrames[1].MessageId);
        CommandLongMessage takeoffCmd = CommandLongMessage.Decode(takeoffFrames[1]);
        Assert.Equal(MavCmd.NavTakeoff, takeoffCmd.Command);
        Assert.Equal(12f, takeoffCmd.Param7);

        // 5. RequestStartMission in STABILIZE -> confirmation-guarded, then switches to AUTO and sends MissionStart
        transport.EmitIncoming(EncodeHeartbeat(1, MavType.Quadrotor, MavAutopilot.ArduPilotMega, customMode: 0u, armed: true));
        transport.ClearSent();
        vm.RequestStartMission();
        Assert.True(vm.HasPendingSafetyAction);
        Assert.Equal(SafetyActionKind.StartMission, vm.PendingSafetyAction!.Kind);
        Assert.Empty(transport.GetSentFramesExcludingHeartbeat());

        await vm.ConfirmSafetyActionAsync();
        Assert.False(vm.HasPendingSafetyAction);
        IReadOnlyList<MavlinkFrame> missionStartFrames = transport.GetSentFramesExcludingHeartbeat();
        Assert.Equal(2, missionStartFrames.Count);
        Assert.Equal(SetModeMessage.MsgId, missionStartFrames[0].MessageId);
        SetModeMessage autoSwitch = SetModeMessage.Decode(missionStartFrames[0]);
        Assert.Equal(3u, autoSwitch.CustomMode);
        Assert.Equal(CommandLongMessage.MsgId, missionStartFrames[1].MessageId);
        CommandLongMessage startCmd = CommandLongMessage.Decode(missionStartFrames[1]);
        Assert.Equal(MavCmd.MissionStart, startCmd.Command);

        // 6. ApplyCruiseSpeedAsync -> sends MAV_CMD_DO_CHANGE_SPEED with requested m/s
        transport.ClearSent();
        vm.CruiseSpeedMps = 8.5;
        await vm.ApplyCruiseSpeedCommand.ExecuteAsync();
        MavlinkFrame speedFrame = Assert.Single(transport.GetSentFramesExcludingHeartbeat());
        Assert.Equal(CommandLongMessage.MsgId, speedFrame.MessageId);
        CommandLongMessage speedCmd = CommandLongMessage.Decode(speedFrame);
        Assert.Equal(MavCmd.DoChangeSpeed, speedCmd.Command);
        Assert.Equal(1f, speedCmd.Param1);
        Assert.Equal(8.5f, speedCmd.Param2);
        Assert.Equal(-1f, speedCmd.Param3);
    }

    [Fact]
    public async Task LocalGeofenceBreachCheck_TriggersAlertWhenVehicleLeavesInclusionPolygon()
    {
        // Pure ray-casting unit verification first
        var square = new GeoPoint[]
        {
            new(39.90, 32.80),
            new(39.90, 32.90),
            new(40.00, 32.90),
            new(40.00, 32.80)
        };
        Assert.True(GeofenceEvaluator.IsPointInsidePolygon(39.95, 32.85, square));
        Assert.False(GeofenceEvaluator.IsPointInsidePolygon(40.05, 32.85, square));
        Assert.False(GeofenceEvaluator.IsPointInsidePolygon(39.95, 32.75, square));

        var transport = new FakeTransport();
        await using MainViewModel vm = CreateTestViewModel(transport, out _);
        await vm.ConnectAsync();

        transport.EmitIncoming(EncodeHeartbeat(1, MavType.FixedWing, MavAutopilot.ArduPilotMega, customMode: 15u, armed: true));

        // Define inclusion polygon around Ankara
        foreach (GeoPoint pt in square)
        {
            vm.AddFencePoint(pt.Latitude, pt.Longitude);
        }

        // Vehicle inside polygon -> no breach
        transport.EmitIncoming(EncodeGlobalPosition(1, 39.95, 32.85, relAltMeters: 100.0));
        Assert.False(vm.IsGeofenceBreached);
        Assert.True(string.IsNullOrEmpty(vm.GeofenceAlertMessage));

        // Vehicle exits inclusion polygon -> triggers breach + alert + log entry
        transport.EmitIncoming(EncodeGlobalPosition(1, 40.06, 32.95, relAltMeters: 100.0));
        Assert.True(vm.IsGeofenceBreached);
        Assert.Contains("GEOFENCE", vm.GeofenceAlertMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(vm.Logs, entry => entry.Message.Contains("GEOFENCE", StringComparison.OrdinalIgnoreCase));

        // Vehicle returns inside inclusion polygon -> breach clears
        transport.EmitIncoming(EncodeGlobalPosition(1, 39.94, 32.84, relAltMeters: 100.0));
        Assert.False(vm.IsGeofenceBreached);

        // Breach while exiting, then clearing fence also clears breach
        transport.EmitIncoming(EncodeGlobalPosition(1, 40.08, 32.96, relAltMeters: 100.0));
        Assert.True(vm.IsGeofenceBreached);
        vm.ClearFencePoints();
        Assert.False(vm.IsGeofenceBreached);
    }

    [Fact]
    public async Task SwapMapAndCamera_TogglesPrimarySurfaceStateWithoutDroppingVideoOrTelemetry()
    {
        var transport = new FakeTransport();
        await using MainViewModel vm = CreateTestViewModel(transport, out _);
        await vm.ConnectAsync();

        transport.EmitIncoming(EncodeHeartbeat(1, MavType.Quadrotor, MavAutopilot.ArduPilotMega, customMode: 5u, armed: true));
        transport.EmitIncoming(EncodeGlobalPosition(1, 39.9334, 32.8597, relAltMeters: 42.5));

        vm.SelectedVideoSourceKind = VideoSourceKind.Rtsp;
        vm.VideoStreamInput = "rtsp://127.0.0.1:8554/tulpar";
        vm.StartVideoCommand.Execute(null);

        Assert.False(vm.IsCameraPrimary);
        Assert.True(vm.IsMapPrimary);
        Assert.True(vm.IsVideoActive);
        Assert.Equal("rtsp://127.0.0.1:8554/tulpar", vm.ActiveVideoMrl);

        // Swap Map <-> Camera (Camera becomes Primary, Map becomes PiP)
        Assert.True(vm.SwapMapAndCameraCommand.CanExecute(null));
        vm.SwapMapAndCameraCommand.Execute(null);

        Assert.True(vm.IsCameraPrimary);
        Assert.False(vm.IsMapPrimary);
        Assert.True(vm.IsVideoActive);
        Assert.Equal("rtsp://127.0.0.1:8554/tulpar", vm.ActiveVideoMrl);
        Assert.True(vm.IsTransportConnected);
        Assert.True(vm.Snapshot.IsConnected);

        // Telemetry keeps updating uninterrupted while camera is primary
        transport.EmitIncoming(EncodeGlobalPosition(1, 39.9400, 32.8700, relAltMeters: 55.0));
        Assert.Equal(39.9400, vm.Snapshot.Latitude, 4);
        Assert.Equal(55.0, vm.HudSnapshot.AltitudeRelativeMeters, 1);

        // Swap back (Map becomes Primary, Camera returns to PiP)
        vm.SwapMapAndCameraCommand.Execute(null);
        Assert.False(vm.IsCameraPrimary);
        Assert.True(vm.IsMapPrimary);
        Assert.True(vm.IsVideoActive);
        Assert.Equal("rtsp://127.0.0.1:8554/tulpar", vm.ActiveVideoMrl);
        Assert.True(vm.IsTransportConnected);
    }

    [Fact]
    public async Task VehicleTypeSwitch_UpdatesCommandBarModesAndMissionCommandPalette()
    {
        var transport = new FakeTransport();
        await using MainViewModel vm = CreateTestViewModel(transport, out _);
        await vm.ConnectAsync();

        // 1. ArduPilot Fixed-Wing
        transport.EmitIncoming(EncodeHeartbeat(1, MavType.FixedWing, MavAutopilot.ArduPilotMega, customMode: 5u));
        Assert.IsType<FixedWingProfile>(vm.ActiveProfile);
        Assert.Contains("SİHA", vm.VehicleBadgeText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ARDUPILOT", vm.VehicleBadgeText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(vm.AvailableModes, m => m.Name == "FBWA");
        Assert.Contains(vm.AvailableModes, m => m.Name == "CRUISE");
        Assert.Contains(MavCmd.NavLoiterToAlt, vm.AvailableMissionCommands);
        Assert.DoesNotContain(MavCmd.DoVtolTransition, vm.AvailableMissionCommands);
        Assert.Equal("FBWA", vm.Snapshot.ModeName);

        // 2. Dynamic switch to PX4 VTOL Tiltrotor
        uint px4MissionMode = ((uint)4 << 16) | ((uint)4 << 24);
        transport.EmitIncoming(EncodeHeartbeat(2, MavType.VtolTiltrotor, MavAutopilot.Px4, customMode: px4MissionMode));
        Assert.IsType<VtolProfile>(vm.ActiveProfile);
        Assert.Contains("VTOL", vm.VehicleBadgeText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PX4", vm.VehicleBadgeText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(vm.AvailableModes, m => m.Name == "OFFBOARD");
        Assert.Contains(vm.AvailableModes, m => m.Name == "AUTO_MISSION");
        Assert.DoesNotContain(vm.AvailableModes, m => m.Name == "FBWA");
        Assert.Contains(MavCmd.NavVtolTakeoff, vm.AvailableMissionCommands);
        Assert.Contains(MavCmd.DoVtolTransition, vm.AvailableMissionCommands);

        // 3. Dynamic switch to ArduRover (İKA)
        transport.EmitIncoming(EncodeHeartbeat(3, MavType.GroundRover, MavAutopilot.ArduPilotMega, customMode: 3u));
        Assert.IsType<RoverIkaProfile>(vm.ActiveProfile);
        Assert.Contains("İKA", vm.VehicleBadgeText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(vm.AvailableModes, m => m.Name == "STEERING");
        Assert.Contains(vm.AvailableModes, m => m.Name == "HOLD");
        Assert.Contains(MavCmd.DoSetReverse, vm.AvailableMissionCommands);
        Assert.DoesNotContain(MavCmd.NavTakeoff, vm.AvailableMissionCommands);
    }

    [Fact]
    public async Task MapWebMessages_And_Wpl110RoundTrip_UpdateMissionFenceAndGuidedSafetyGuard()
    {
        var transport = new FakeTransport();
        await using MainViewModel vm = CreateTestViewModel(transport, out _);
        await vm.ConnectAsync();
        transport.EmitIncoming(EncodeHeartbeat(1, MavType.Quadrotor, MavAutopilot.ArduPilotMega, customMode: 4u));

        // WebView2 missionAdd / missionMove
        vm.HandleMapWebMessage(JsonSerializer.Serialize(new { type = "missionAdd", lat = 39.92, lon = 32.85 }));
        vm.HandleMapWebMessage(JsonSerializer.Serialize(new { type = "missionAdd", lat = 39.93, lon = 32.86 }));
        Assert.Equal(2, vm.MissionItems.Count);
        Assert.Equal(0, vm.MissionItems[0].Seq);
        Assert.Equal(1, vm.MissionItems[1].Seq);

        vm.HandleMapWebMessage(JsonSerializer.Serialize(new { type = "missionMove", seq = 1, lat = 39.935, lon = 32.865 }));
        Assert.Equal(39.935, vm.MissionItems[1].Latitude, 6);
        Assert.Equal(32.865, vm.MissionItems[1].Longitude, 6);

        // WPL110 export/import round-trip (verify REL/AMSL preservation under tr-TR culture)
        vm.MissionItems[1].ReferenceFrameLabel = "AMSL";
        CultureInfo prevCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            string wpl = vm.ExportWpl110();
            vm.MissionItems.Clear();
            vm.ImportWpl110(wpl);
        }
        finally
        {
            CultureInfo.CurrentCulture = prevCulture;
        }

        Assert.Equal(2, vm.MissionItems.Count);
        Assert.Equal(MavFrame.GlobalRelativeAltInt, vm.MissionItems[0].Frame);
        Assert.Equal(MavFrame.GlobalInt, vm.MissionItems[1].Frame);
        Assert.Equal("AMSL", vm.MissionItems[1].ReferenceFrameLabel);

        // WebView2 fenceAdd / fenceMove / boundsSelected
        vm.HandleMapWebMessage(JsonSerializer.Serialize(new { type = "fenceAdd", lat = 39.91, lon = 32.84 }));
        vm.HandleMapWebMessage(JsonSerializer.Serialize(new { type = "fenceMove", index = 0, lat = 39.915, lon = 32.845 }));
        Assert.Single(vm.FencePoints);
        Assert.Equal(39.915, vm.FencePoints[0].Latitude, 6);

        vm.HandleMapWebMessage(JsonSerializer.Serialize(new
        {
            type = "boundsSelected",
            bounds = new { minLat = 39.8, minLon = 32.7, maxLat = 40.0, maxLon = 33.0 }
        }));
        Assert.NotNull(vm.SelectedOfflineBounds);
        Assert.Equal(39.8, vm.SelectedOfflineBounds!.Value.MinLat, 4);

        // WebView2 guidedTarget MUST go through PendingSafetyAction (never sends immediately)
        transport.ClearSent();
        vm.HandleMapWebMessage(JsonSerializer.Serialize(new { type = "guidedTarget", lat = 39.95, lon = 32.88 }));
        Assert.True(vm.HasPendingSafetyAction);
        Assert.Equal(SafetyActionKind.GuidedFlyHere, vm.PendingSafetyAction!.Kind);
        Assert.Empty(transport.GetSentFramesExcludingHeartbeat());
    }

    [Fact]
    public async Task VisualMissionPlanner_SupportsSelectionInspectionAndContextualMetadata()
    {
        var transport = new FakeTransport();
        await using MainViewModel vm = CreateTestViewModel(transport, out _);

        vm.HandleMapWebMessage(JsonSerializer.Serialize(new { type = "missionAdd", lat = 39.92, lon = 32.85 }));
        vm.HandleMapWebMessage(JsonSerializer.Serialize(new { type = "missionAdd", lat = 39.93, lon = 32.86 }));

        EditableMissionItem? inspected = null;
        vm.MissionWaypointInspectRequested += item => inspected = item;

        vm.HandleMapWebMessage(JsonSerializer.Serialize(new { type = "missionSelect", seq = 0 }));
        Assert.Same(vm.MissionItems[0], vm.SelectedMissionItem);
        Assert.Same(vm.MissionItems[0], inspected);

        vm.MissionItems[0].Command = MavCmd.NavTakeoff;
        Assert.Equal("TAKEOFF", vm.MissionItems[0].CommandShortLabel);
        Assert.Equal("↑", vm.MissionItems[0].CommandBadgeGlyph);
        Assert.Equal("MİN PİTCH (°)", vm.MissionItems[0].Param1Label);

        vm.MissionItems[1].Command = MavCmd.NavLoiterTurns;
        vm.MissionItems[1].Param1 = 3f;
        vm.MissionItems[1].Param3 = 45f;
        Assert.Equal("LOITER TUR", vm.MissionItems[1].CommandShortLabel);
        Assert.Contains("TUR: 3", vm.MissionItems[1].ParamSummaryDisplay);

        RunOnStaThread(() =>
        {
            MainViewModel panelVm = CreateTestViewModel(new FakeTransport(), out _);
            try
            {
                panelVm.HandleMapWebMessage(JsonSerializer.Serialize(new { type = "missionAdd", lat = 39.92, lon = 32.85 }));
                var panel = new MissionPlannerPanel { DataContext = panelVm };
                panel.Measure(new Size(1100, 260));
                panel.Arrange(new Rect(0, 0, 1100, 260));
                panelVm.HandleMapWebMessage(JsonSerializer.Serialize(new { type = "missionSelect", seq = 0 }));
                panel.DataContext = null;
            }
            finally
            {
                panelVm.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public async Task MissionPatternsAndLoopConnection_GenerateValidSpiralsAreaScanAndJumpLoops()
    {
        var transport = new FakeTransport();
        await using MainViewModel vm = CreateTestViewModel(transport, out _);

        // 1) Expanding spiral (küçükten büyüğe): radius grows outward
        var expanding = new MissionPatternRequest(
            Kind: MissionPatternKind.ExpandingSpiral,
            CenterLat: 39.9334,
            CenterLon: 32.8597,
            AltitudeMeters: 60f,
            Frame: MavFrame.GlobalRelativeAltInt,
            StartRadiusMeters: 20,
            EndRadiusMeters: 120,
            Turns: 2,
            PointsPerTurn: 10);
        int expCount = vm.ApplyMissionPattern(expanding, replaceExisting: true);
        Assert.Equal(21, expCount);
        double firstDist = HaversineMeters(39.9334, 32.8597, vm.MissionItems[0].Latitude, vm.MissionItems[0].Longitude);
        double lastDist = HaversineMeters(39.9334, 32.8597, vm.MissionItems[^1].Latitude, vm.MissionItems[^1].Longitude);
        Assert.InRange(firstDist, 18.0, 22.0);
        Assert.InRange(lastDist, 115.0, 125.0);

        // 2) Shrinking spiral (büyükten küçüğe): radius shrinks inward
        var shrinking = expanding with { Kind = MissionPatternKind.ShrinkingSpiral };
        int shrCount = vm.ApplyMissionPattern(shrinking, replaceExisting: true);
        Assert.Equal(21, shrCount);
        double shrFirst = HaversineMeters(39.9334, 32.8597, vm.MissionItems[0].Latitude, vm.MissionItems[0].Longitude);
        double shrLast = HaversineMeters(39.9334, 32.8597, vm.MissionItems[^1].Latitude, vm.MissionItems[^1].Longitude);
        Assert.True(shrFirst > shrLast + 80.0);

        // 3) Polygon interior area scan + DO_JUMP loop back to first waypoint
        vm.AddFencePoint(39.930, 32.855);
        vm.AddFencePoint(39.930, 32.860);
        vm.AddFencePoint(39.934, 32.860);
        vm.AddFencePoint(39.934, 32.855);

        var areaScan = new MissionPatternRequest(
            Kind: MissionPatternKind.PolygonGridScan,
            CenterLat: 39.932,
            CenterLon: 32.8575,
            AltitudeMeters: 45f,
            Frame: MavFrame.GlobalRelativeAltInt,
            GridSpacingMeters: 90,
            IncludeTakeoff: true,
            CloseLoopWithJump: true,
            PolygonVertices: vm.FencePoints.Select(f => (f.Latitude, f.Longitude)).ToList());
        int scanCount = vm.ApplyMissionPattern(areaScan, replaceExisting: true);
        Assert.True(scanCount >= 6);
        Assert.Equal(MavCmd.NavTakeoff, vm.MissionItems[0].Command);
        Assert.Equal(MavCmd.DoJump, vm.MissionItems[^1].Command);
        Assert.Equal(1f, vm.MissionItems[^1].Param1); // Jumps to first scan waypoint after takeoff
        Assert.Equal(-1f, vm.MissionItems[^1].Param2);

        // 4) Connect last waypoint to target #0 physically as closed loop
        bool looped = vm.ConnectLastWaypointToTarget(targetSeq: 0, repeatCount: -1, useDoJump: false);
        Assert.True(looped);
        Assert.Equal(MavCmd.NavWaypoint, vm.MissionItems[^1].Command);
        Assert.Equal(vm.MissionItems[0].Latitude, vm.MissionItems[^1].Latitude, 6);
        Assert.Equal(vm.MissionItems[0].Longitude, vm.MissionItems[^1].Longitude, 6);
    }

    [Fact]
    public async Task Phase2Integration_PreFlightParametersReplayMetricsAndAtomicImport_WorkEndToEnd()
    {
        var transport = new FakeTransport();
        await using MainViewModel vm = CreateTestViewModel(transport, out _);

        // 1) Quick SITL 5760 preset configures TCP 127.0.0.1:5760 and connects
        Assert.False(vm.IsPreFlightReady);
        await vm.QuickConnectSitlAsync();
        Assert.Equal(ConnectionKind.Tcp, vm.SelectedConnectionKind);
        Assert.Equal("127.0.0.1", vm.TcpHost);
        Assert.Equal(5760, vm.TcpPort);
        Assert.True(vm.IsTransportConnected);

        // 2) Pre-flight checks become ready when Heartbeat + GPS 3D Fix + Home + Battery + EKF healthy arrive
        transport.EmitIncoming(EncodeHeartbeat(1, MavType.Quadrotor, MavAutopilot.ArduPilotMega, customMode: 0u));
        byte[] gpsPkt = MavlinkEncoder.EncodeV2(1, 1, 10, new GpsRawIntMessage(
            TimeUsec: 1000,
            FixType: GpsFixType.Fix3D,
            Lat: 399334000,
            Lon: 328597000,
            Alt: 850000,
            Eph: 120,
            Epv: 180,
            Vel: 0,
            Cog: 0,
            SatellitesVisible: 11));
        transport.EmitIncoming(gpsPkt);
        byte[] sysPkt = MavlinkEncoder.EncodeV2(1, 1, 11, new SysStatusMessage(
            OnboardControlSensorsPresent: 0x3FFF,
            OnboardControlSensorsEnabled: 0x3FFF,
            OnboardControlSensorsHealth: 0x3FFF,
            Load: 120,
            VoltageBattery: 24800,
            CurrentBattery: 120,
            BatteryRemaining: 88));
        transport.EmitIncoming(sysPkt);
        byte[] homePkt = MavlinkEncoder.EncodeV2(1, 1, 12, new HomePositionMessage(
            Latitude: 399334000,
            Longitude: 328597000,
            Altitude: 850000,
            X: 0f,
            Y: 0f,
            Z: 0f,
            Q: null,
            ApproachX: 0f,
            ApproachY: 0f,
            ApproachZ: 0f));
        transport.EmitIncoming(homePkt);
        Assert.True(vm.PreFlightChecks.Count >= 4);
        Assert.True(vm.IsPreFlightReady);

        // 3) Measure toggle + live drag preview + midpoint insert + route metrics
        vm.ToggleMeasureMapEditCommand.Execute(null);
        Assert.True(vm.IsMeasureMapPickActive);
        vm.HandleMapWebMessage(JsonSerializer.Serialize(new { type = "missionAdd", lat = 39.920, lon = 32.850 }));
        vm.HandleMapWebMessage(JsonSerializer.Serialize(new { type = "missionAdd", lat = 39.930, lon = 32.850 }));
        Assert.True(vm.CurrentFlightMetrics.TotalDistanceMeters > 900.0);
        Assert.False(string.IsNullOrWhiteSpace(vm.MissionMetricsSummaryText));

        vm.HandleMapWebMessage(JsonSerializer.Serialize(new { type = "missionMovePreview", seq = 1, lat = 39.932, lon = 32.852 }));
        Assert.Equal(39.932, vm.MissionItems[1].Latitude, 5);
        vm.HandleMapWebMessage(JsonSerializer.Serialize(new { type = "missionInsertAfter", afterSeq = 0, lat = 39.925, lon = 32.851 }));
        Assert.Equal(3, vm.MissionItems.Count);
        Assert.Equal(39.925, vm.MissionItems[1].Latitude, 5);

        // 4) Atomic malformed JSON import preserves existing mission items
        int beforeCount = vm.MissionItems.Count;
        Assert.ThrowsAny<Exception>(() => vm.ImportMissionJson("{\"items\":[{\"seq\":0,\"command\":16,\"latitude\":\"NaN\",\"longitude\":32.8}]}"));
        Assert.Equal(beforeCount, vm.MissionItems.Count);

        // 5) Parameter protocol refresh & filter + write
        transport.OnOutgoingFrame = frame =>
        {
            if (frame.MessageId == ParamRequestListMessage.MsgId)
            {
                transport.EmitIncoming(MavlinkEncoder.EncodeV2(1, 1, 20, new ParamValueMessage(
                    ParamId: "WPNAV_SPEED",
                    ParamValue: 800f,
                    ParamType: MavParamType.Real32,
                    ParamCount: 1,
                    ParamIndex: 0)));
            }
            else if (frame.MessageId == ParamSetMessage.MsgId)
            {
                ParamSetMessage set = ParamSetMessage.Decode(frame);
                transport.EmitIncoming(MavlinkEncoder.EncodeV2(1, 1, 21, new ParamValueMessage(
                    ParamId: set.ParamId,
                    ParamValue: set.ParamValue,
                    ParamType: set.ParamType,
                    ParamCount: 1,
                    ParamIndex: 0)));
            }
        };

        await vm.RefreshParametersAsync();
        vm.ParameterSearchText = "WPNAV";
        VehicleParameterRow wpSpeed = Assert.Single(vm.FilteredParameters);
        wpSpeed.EditValue = "1000";
        await vm.ApplyParameterAsync(wpSpeed);
        Assert.Equal(1000f, wpSpeed.NumericValue, 2);

        // 6) .tlog Replay load, seek, and stop
        string tempTlog = Path.Combine(Path.GetTempPath(), $"tulpar_test_{Guid.NewGuid():N}.tlog");
        try
        {
            await using (FileStream fs = File.Create(tempTlog))
            {
                byte[] p1 = EncodeHeartbeat(1, MavType.FixedWing, MavAutopilot.ArduPilotMega, customMode: 5u, armed: true);
                byte[] p2 = EncodeGlobalPosition(1, 39.955, 32.875, relAltMeters: 145.0, amslAltMeters: 920.0, headingCdeg: 9000, seq: 5);
                Span<byte> ts = stackalloc byte[8];
                BinaryPrimitives.WriteInt64BigEndian(ts, 1_700_000_000_000_000L);
                fs.Write(ts);
                fs.Write(p1);
                BinaryPrimitives.WriteInt64BigEndian(ts, 1_700_000_001_000_000L);
                fs.Write(ts);
                fs.Write(p2);
            }

            await vm.LoadReplayFileAsync(tempTlog);
            Assert.True(vm.IsReplayMode);
            await vm.SeekReplayAsync(100);
            Assert.Equal(145.0, vm.Snapshot.AltitudeRelativeMeters, 1);
            await vm.StopReplayCommand.ExecuteAsync();
            Assert.False(vm.IsReplayMode);
        }
        finally
        {
            if (File.Exists(tempTlog))
            {
                File.Delete(tempTlog);
            }
        }
    }

    [Fact]
    public async Task P0ToP2_TelemetryHonestyHealthCenterLogGroupingSyncAndUndo_WorkCleanly()
    {
        var transport = new FakeTransport();
        await using MainViewModel vm = CreateTestViewModel(transport, out _);

        // 1) Before connection: "--" honesty instead of fake 0.0, health waiting
        Assert.False(vm.HasLiveTelemetry);
        Assert.Equal("-- M", vm.AltitudeRelativeDisplay);
        Assert.Equal("-- M/S", vm.GroundspeedDisplay);
        Assert.Equal("---°", vm.HeadingDisplay);
        Assert.Equal("--", vm.LatitudeDisplay);
        Assert.Equal("VERİ YOK", vm.DataFreshnessText);
        Assert.True(vm.SystemHealthItems.Count >= 6);

        // 2) Connect & push live Heartbeat -> live numeric 0.0 appears
        await vm.ConnectAsync();
        transport.EmitIncoming(EncodeHeartbeat(1, MavType.Quadrotor, MavAutopilot.ArduPilotMega, customMode: 0u));
        Assert.True(vm.HasLiveTelemetry);
        Assert.Equal("0.0 M", vm.AltitudeRelativeDisplay);
        Assert.NotEqual("VERİ YOK", vm.DataFreshnessText);

        // 3) Log coalescing & search/severity filtering
        int beforeLogCount = vm.Logs.Count;
        vm.AppendSystemLog(MavSeverity.Warning, "KAMERA", "Video akışı başlatılıyor: udp://@:5600");
        vm.AppendSystemLog(MavSeverity.Warning, "KAMERA", "Video akışı başlatılıyor: udp://@:5600");
        Assert.Equal(beforeLogCount + 1, vm.Logs.Count);
        Assert.Contains("(×2)", vm.Logs[0].DisplayLine);

        vm.SelectedLogSeverityFilter = "UYARI";
        Assert.Contains(vm.FilteredLogs, l => l.Source == "KAMERA" && l.RepeatCount == 2);
        vm.LogSearchText = "YOK_BOYLE_BIR_KAYIT";
        Assert.Empty(vm.FilteredLogs);
        vm.LogSearchText = string.Empty;
        vm.SelectedLogSeverityFilter = "TÜMÜ";

        // 4) Mission sync state, SelectedMissionSeqChanged, and Undo stack (Ctrl+Z)
        int? lastSelectedSeq = null;
        vm.SelectedMissionSeqChanged += seq => lastSelectedSeq = seq;

        Assert.Equal("GÖREV YOK", vm.MissionSyncStateText);
        vm.AddMissionWaypoint(39.92, 32.85);
        vm.AddMissionWaypoint(39.93, 32.86);
        Assert.True(vm.IsMissionDirty);
        Assert.Contains("TASLAK", vm.MissionSyncStateText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, lastSelectedSeq);
        Assert.True(vm.CanUndoMission);

        vm.UndoMissionCommand.Execute(null);
        Assert.Single(vm.MissionItems);
    }

    private static double HaversineMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double r = 6_371_000.0;
        double dLat = (lat2 - lat1) * Math.PI / 180.0;
        double dLon = (lon2 - lon1) * Math.PI / 180.0;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0) *
                   Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * r * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    [Fact]
    public void VideoFeedControl_DegradesGracefullyWithoutCrashingAndSupportsPresets()
    {
        RunOnStaThread(() =>
        {
            var control = new VideoFeedControl();
            control.Measure(new Size(340, 220));
            control.Arrange(new Rect(0, 0, 340, 220));

            Assert.Contains("SİNYAL BEKLENİYOR", control.OverlayMessage, StringComparison.OrdinalIgnoreCase);

            // Switch source presets cleanly (RTSP, UDP/RTP, DirectShow)
            Assert.Equal("udp://@:5600", VideoFeedControl.BuildEffectiveMrl(VideoSourceKind.UdpRtp, ""));
            Assert.Equal("rtsp://127.0.0.1:8554/stream", VideoFeedControl.BuildEffectiveMrl(VideoSourceKind.Rtsp, "rtsp://127.0.0.1:8554/stream"));
            string dshowMrl = VideoFeedControl.BuildEffectiveMrl(VideoSourceKind.UsbDirectShow, "Integrated Camera");
            Assert.Equal("dshow://", dshowMrl);
            IReadOnlyList<string> dshowOpts = VideoFeedControl.BuildMediaOptions(VideoSourceKind.UsbDirectShow, "Integrated Camera");
            Assert.Contains(dshowOpts, o => o.Contains("dshow-vdev=Integrated Camera", StringComparison.Ordinal));

            // Starting/stopping even if LibVLC is unavailable or unattached must not throw
            control.StartStream("udp://@:5600", VideoFeedControl.BuildMediaOptions(VideoSourceKind.UdpRtp, "udp://@:5600"));
            control.StopStream();
            control.Dispose();
        });
    }

    [Fact]
    public async Task MissionAndFence_UploadDownloadAndTableEdit_UpdateProtocolAndGeofenceState()
    {
        var transport = new FakeTransport();
        await using MainViewModel vm = CreateTestViewModel(transport, out _);
        await vm.ConnectAsync();

        transport.EmitIncoming(EncodeHeartbeat(1, MavType.Quadrotor, MavAutopilot.ArduPilotMega, customMode: 4u));
        transport.EmitIncoming(EncodeGlobalPosition(1, 39.95, 32.85, relAltMeters: 60.0));

        // 1. Editing waypoint & fence point properties directly fires sync events & updates geofence
        int missionSyncCount = 0;
        int fenceSyncCount = 0;
        vm.MissionPlanChanged += () => missionSyncCount++;
        vm.FencePolygonChanged += () => fenceSyncCount++;

        vm.AddMissionWaypoint(39.94, 32.84);
        vm.AddMissionWaypoint(39.96, 32.86);
        int prevMissionSync = missionSyncCount;
        vm.MissionItems[0].Latitude = 39.945;
        Assert.Equal(prevMissionSync + 1, missionSyncCount);

        vm.AddFencePoint(39.90, 32.80);
        vm.AddFencePoint(39.90, 32.90);
        vm.AddFencePoint(40.00, 32.90);
        vm.AddFencePoint(40.00, 32.80);
        Assert.False(vm.IsGeofenceBreached);

        // Shrink polygon away from vehicle -> local geofence breach triggers immediately
        int prevFenceSync = fenceSyncCount;
        vm.FencePoints[2].Latitude = 39.92;
        vm.FencePoints[3].Latitude = 39.92;
        Assert.True(fenceSyncCount > prevFenceSync);
        Assert.True(vm.IsGeofenceBreached);

        // 2. UploadFenceAsync sends NavFencePolygonVertexInclusion with Param1 == vertexCount
        var uploadedFenceItems = new List<MissionItemIntMessage>();
        byte vehicleSeq = 0;
        transport.OnOutgoingFrame = frame =>
        {
            if (frame.MessageId == MissionCountMessage.MsgId)
            {
                var countMsg = MissionCountMessage.Decode(frame);
                Assert.Equal(MavMissionType.Fence, countMsg.MissionType);
                Assert.Equal((ushort)4, countMsg.Count);
                var req0 = new MissionRequestIntMessage(255, 190, 0, MavMissionType.Fence);
                transport.EmitIncoming(MavlinkEncoder.EncodeV2(1, 1, vehicleSeq++, req0));
            }
            else if (frame.MessageId == MissionItemIntMessage.MsgId)
            {
                var item = MissionItemIntMessage.Decode(frame);
                uploadedFenceItems.Add(item);
                if (item.Seq + 1 < 4)
                {
                    var nextReq = new MissionRequestIntMessage(255, 190, (ushort)(item.Seq + 1), MavMissionType.Fence);
                    transport.EmitIncoming(MavlinkEncoder.EncodeV2(1, 1, vehicleSeq++, nextReq));
                }
                else
                {
                    var ack = new MissionAckMessage(255, 190, MavMissionResult.Accepted, MavMissionType.Fence);
                    transport.EmitIncoming(MavlinkEncoder.EncodeV2(1, 1, vehicleSeq++, ack));
                }
            }
        };

        await vm.UploadFenceAsync();
        Assert.Equal(4, uploadedFenceItems.Count);
        Assert.All(uploadedFenceItems, item =>
        {
            Assert.Equal(MavCmd.NavFencePolygonVertexInclusion, item.Command);
            Assert.Equal(MavMissionType.Fence, item.MissionType);
            Assert.Equal(4f, item.Param1);
        });
    }

    [Fact]
    public void SpaceXThemeAndControls_InstantiateAndMeasureCleanlyWithoutDropShadows()
    {
        RunOnStaThread(() =>
        {
            _ = PackUriHelper.UriSchemePack;
            var theme = new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Tulpar.App;component/Themes/SpaceXTheme.xaml", UriKind.Absolute)
            };

            var tabStyle = Assert.IsType<Style>(theme["SpaceXTabControlStyle"]);
            var gridStyle = Assert.IsType<Style>(theme["SpaceXDataGridStyle"]);
            var checkStyle = Assert.IsType<Style>(theme["SpaceXCheckBoxStyle"]);
            var progressStyle = Assert.IsType<Style>(theme["SpaceXProgressBarStyle"]);

            var tabs = new TabControl { Style = tabStyle };
            var grid = new DataGrid { Style = gridStyle };
            var check = new CheckBox { Style = checkStyle, Content = "forwarder" };
            var progress = new ProgressBar { Style = progressStyle, Value = 50 };

            Assert.Null(tabs.Effect);
            Assert.Null(grid.Effect);
            Assert.Null(check.Effect);
            Assert.Null(progress.Effect);

            var mapHost = new MapHostControl();
            mapHost.Measure(new Size(600, 400));
            mapHost.Arrange(new Rect(0, 0, 600, 400));
            mapHost.Dispose();

            var missionPanel = new MissionPlannerPanel();
            missionPanel.Measure(new Size(900, 235));
            missionPanel.Arrange(new Rect(0, 0, 900, 235));
        });
    }

    [Fact]
    public async Task LiveTelemetry_DoesNotOverwriteUserSelectedMode_UntilVehicleModeChanges()
    {
        var transport = new FakeTransport();
        await using MainViewModel vm = CreateTestViewModel(transport, out _);
        await vm.ConnectAsync();

        IVehicleProfile fwProfile = VehicleProfileFactory.Create(MavType.FixedWing, MavAutopilot.ArduPilotMega);
        IReadOnlyList<FlightModeOption> fwModes = fwProfile.GetFlightModes(MavAutopilot.ArduPilotMega);
        FlightModeOption manualMode = fwModes.First(m => string.Equals(m.Name, "MANUAL", StringComparison.OrdinalIgnoreCase));
        FlightModeOption guidedMode = fwModes.First(m => string.Equals(m.Name, "GUIDED", StringComparison.OrdinalIgnoreCase));
        FlightModeOption autoMode = fwModes.First(m => string.Equals(m.Name, "AUTO", StringComparison.OrdinalIgnoreCase));

        // Initial heartbeat sets vehicle mode to MANUAL
        transport.EmitIncoming(EncodeHeartbeat(1, MavType.FixedWing, MavAutopilot.ArduPilotMega, manualMode.CustomMode, seq: 1));
        Assert.NotNull(vm.SelectedMode);
        Assert.Equal("MANUAL", vm.SelectedMode!.Name);

        // Operator selects GUIDED from the dropdown before clicking MOD
        vm.SelectedMode = vm.AvailableModes.First(m => string.Equals(m.Name, guidedMode.Name, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("GUIDED", vm.SelectedMode!.Name);

        // Subsequent live telemetry ticks still reporting MANUAL must NOT revert operator's selection
        for (byte i = 2; i <= 6; i++)
        {
            transport.EmitIncoming(EncodeHeartbeat(1, MavType.FixedWing, MavAutopilot.ArduPilotMega, manualMode.CustomMode, seq: i));
            Assert.Equal("GUIDED", vm.SelectedMode!.Name);
        }

        // Clicking MOD applies the operator's selected mode
        transport.ClearSent();
        await vm.ApplySelectedModeCommand.ExecuteAsync();
        IReadOnlyList<MavlinkFrame> sent = transport.GetSentFramesExcludingHeartbeat();
        Assert.Contains(sent, f => f.MessageId == SetModeMessage.MsgId);

        // Actual mode change reported by vehicle updates SelectedMode
        transport.EmitIncoming(EncodeHeartbeat(1, MavType.FixedWing, MavAutopilot.ArduPilotMega, autoMode.CustomMode, seq: 10));
        Assert.Equal("AUTO", vm.SelectedMode!.Name);
    }

    [Fact]
    public async Task SpaceXStyles_EditableComboBox_And_CheckBoxFullLabel_RenderProperly()
    {
        var transport = new FakeTransport();
        await using MainViewModel vm = CreateTestViewModel(transport, out _);

        RunOnStaThread(() =>
        {
            _ = PackUriHelper.UriSchemePack;
            _ = new ComboBox();

            var theme = new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Tulpar.App;component/Themes/SpaceXTheme.xaml", UriKind.Absolute)
            };

            var comboStyle = Assert.IsType<Style>(theme["SpaceXComboBoxStyle"]);
            var checkStyle = Assert.IsType<Style>(theme["SpaceXCheckBoxStyle"]);

            vm.SelectedVideoSourceKind = VideoSourceKind.UsbDirectShow;

            var combo = new ComboBox
            {
                Style = comboStyle,
                IsEditable = true,
                IsTextSearchEnabled = false,
                ItemsSource = vm.AvailableVideoDevices
            };
            combo.SetBinding(
                ComboBox.TextProperty,
                new Binding(nameof(MainViewModel.VideoStreamInput))
                {
                    Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
                });

            var check = new CheckBox
            {
                Style = checkStyle
            };
            check.SetBinding(
                ContentControl.ContentProperty,
                new Binding(nameof(MainViewModel.UdpForwarderLabel)));

            var host = new StackPanel
            {
                Resources = theme,
                DataContext = vm
            };
            host.Children.Add(combo);
            host.Children.Add(check);

            combo.ApplyTemplate();
            check.ApplyTemplate();
            host.Measure(new Size(420, 120));
            host.Arrange(new Rect(0, 0, 420, 120));
            host.UpdateLayout();

            var editableTextBox = Assert.IsType<TextBox>(combo.Template.FindName("PART_EditableTextBox", combo));
            var contentSite = Assert.IsAssignableFrom<UIElement>(combo.Template.FindName("ContentSite", combo));
            var arrowToggle = Assert.IsAssignableFrom<UIElement>(combo.Template.FindName("ArrowToggle", combo));
            var fullAreaToggle = Assert.IsAssignableFrom<UIElement>(combo.Template.FindName("FullAreaToggle", combo));

            Assert.Equal(Visibility.Visible, editableTextBox.Visibility);
            Assert.False(editableTextBox.IsReadOnly);
            Assert.Equal(Visibility.Collapsed, contentSite.Visibility);
            Assert.Equal(Visibility.Visible, arrowToggle.Visibility);
            Assert.Equal(Visibility.Collapsed, fullAreaToggle.Visibility);

            editableTextBox.Text = "OBS Virtual Camera";
            Assert.Equal("OBS Virtual Camera", vm.VideoStreamInput);

            combo.SelectedItem = "Integrated Camera";
            Assert.Equal("Integrated Camera", vm.VideoStreamInput);

            ContentPresenter? presenter = FindVisualChild<ContentPresenter>(check);
            Assert.NotNull(presenter);
            Assert.Equal($"UDP YANSITICI ({vm.UdpForwarderEndpointText})", presenter!.Content);
        });
    }

    [Fact]
    public async Task EnumDisplayConverter_And_HomeDistanceDisplayText_FormatUppercaseLabels()
    {
        Assert.Equal("UDP SITL", EnumDisplayConverter.FormatEnum(ConnectionKind.Udp));
        Assert.Equal("USB / COM", EnumDisplayConverter.FormatEnum(ConnectionKind.Serial));
        Assert.Equal("TCP SITL", EnumDisplayConverter.FormatEnum(ConnectionKind.Tcp));
        Assert.Equal("UDP / RTP", EnumDisplayConverter.FormatEnum(VideoSourceKind.UdpRtp));
        Assert.Equal("RTSP", EnumDisplayConverter.FormatEnum(VideoSourceKind.Rtsp));
        Assert.Equal("USB KAMERA", EnumDisplayConverter.FormatEnum(VideoSourceKind.UsbDirectShow));
        Assert.Equal("NAV WAYPOINT", EnumDisplayConverter.FormatEnum(MavCmd.NavWaypoint));
        Assert.Equal("NAV RTL", EnumDisplayConverter.FormatEnum(MavCmd.NavReturnToLaunch));

        var transport = new FakeTransport();
        await using MainViewModel vm = CreateTestViewModel(transport, out _);
        Assert.Equal("-- M", vm.HomeDistanceDisplayText);
        Assert.Equal("125.4 M", TelemetryDisplayFormatter.FormatHomeDistance(new TelemetrySnapshot { HasHome = true }, 125.4));
    }

    [Fact]
    public async Task HoldCommand_And_GuidedAltitude_AreVehicleAdaptive()
    {
        var transport = new FakeTransport();
        await using MainViewModel vm = CreateTestViewModel(transport, out _);
        await vm.ConnectAsync();

        // SystemId == 0 blocks HOLD command
        Assert.Equal((byte)0, vm.Snapshot.SystemId);
        Assert.False(vm.RequestHoldCommand.CanExecute(null));
        vm.RequestHold();
        Assert.False(vm.HasPendingSafetyAction);

        // Rover HOLD mode + 0m surface Guided altitude
        transport.EmitIncoming(EncodeHeartbeat(1, MavType.GroundRover, MavAutopilot.ArduPilotMega, 0, seq: 1));
        Assert.True(vm.RequestHoldCommand.CanExecute(null));
        vm.RequestHold();
        Assert.Equal(SafetyActionKind.Hold, vm.PendingSafetyAction!.Kind);
        transport.ClearSent();
        await vm.ConfirmSafetyActionCommand.ExecuteAsync();
        Assert.Contains(transport.GetSentFramesExcludingHeartbeat(), f => f.MessageId == SetModeMessage.MsgId);

        vm.RequestGuidedFlyHere(39.92, 32.85);
        Assert.Equal(0f, vm.PendingSafetyAction!.AltitudeMeters);
        vm.CancelSafetyAction();

        // VTOL QLOITER mode + >=50m Guided floor
        transport.EmitIncoming(EncodeHeartbeat(1, MavType.VtolTiltrotor, MavAutopilot.ArduPilotMega, 0, seq: 2));
        IVehicleProfile vtolProfile = VehicleProfileFactory.Create(MavType.VtolTiltrotor, MavAutopilot.ArduPilotMega);
        FlightModeOption? vtolHold = SafetyCommandBuilder.FindPreferredHoldMode(vtolProfile, MavAutopilot.ArduPilotMega);
        Assert.Equal("QLOITER", vtolHold?.Name);
        vm.RequestGuidedFlyHere(39.92, 32.85);
        Assert.True(vm.PendingSafetyAction!.AltitudeMeters >= 50f);
        vm.CancelSafetyAction();

        // FixedWing LOITER mode + >=50m Guided floor
        transport.EmitIncoming(EncodeHeartbeat(1, MavType.FixedWing, MavAutopilot.ArduPilotMega, 0, seq: 3));
        IVehicleProfile fwProfile = VehicleProfileFactory.Create(MavType.FixedWing, MavAutopilot.ArduPilotMega);
        FlightModeOption? fwHold = SafetyCommandBuilder.FindPreferredHoldMode(fwProfile, MavAutopilot.ArduPilotMega);
        Assert.Equal("LOITER", fwHold?.Name);
        vm.RequestGuidedFlyHere(39.92, 32.85);
        Assert.True(vm.PendingSafetyAction!.AltitudeMeters >= 50f);
        vm.CancelSafetyAction();

        // Multirotor >=20m Guided floor + DoPauseContinue fallback when profile exposes no hold mode
        IVehicleProfile mrProfile = VehicleProfileFactory.Create(MavType.Quadrotor, MavAutopilot.ArduPilotMega);
        Assert.Equal(20f, SafetyCommandBuilder.ResolveSafeGuidedAltitude(mrProfile, new TelemetrySnapshot { IsConnected = true, SystemId = 1 }, null));

        var noHoldProfile = new StubNoHoldVehicleProfile();
        IMavlinkMessage fallbackHoldMsg = SafetyCommandBuilder.BuildHoldMessage(1, 1, noHoldProfile, MavAutopilot.Generic);
        var pauseCmd = Assert.IsType<CommandLongMessage>(fallbackHoldMsg);
        Assert.Equal(MavCmd.DoPauseContinue, pauseCmd.Command);
    }

    [Fact]
    public void VideoFeedControl_KeepsVideoViewCollapsedWhenIdle_And_SwapsCornerRadiusOnPrimary()
    {
        RunOnStaThread(() =>
        {
            var transport = new FakeTransport();
            MainViewModel vm = CreateTestViewModel(transport, out _);
            try
            {
                using var videoControl = new VideoFeedControl();
                videoControl.DataContext = vm;
                videoControl.Measure(new Size(340, 210));
                videoControl.Arrange(new Rect(0, 0, 340, 210));
                videoControl.UpdateLayout();

                var videoView = Assert.IsAssignableFrom<UIElement>(videoControl.FindName("VideoViewControl"));
                var idleOverlay = Assert.IsAssignableFrom<UIElement>(videoControl.FindName("WpfIdleOverlay"));
                var outerFrame = Assert.IsType<Border>(videoControl.FindName("OuterFrame"));

                Assert.Equal(Visibility.Collapsed, videoView.Visibility);
                Assert.Equal(Visibility.Visible, idleOverlay.Visibility);
                Assert.Equal(new CornerRadius(8), outerFrame.CornerRadius);

                videoControl.StartStream("rtsp://127.0.0.1:8554/live");
                Assert.Equal(Visibility.Collapsed, videoView.Visibility);

                vm.SwapMapAndCameraCommand.Execute(null);
                videoControl.UpdateLayout();
                Assert.True(vm.IsCameraPrimary);
                Assert.Equal(new CornerRadius(0), outerFrame.CornerRadius);

                var missionPanel = new MissionPlannerPanel { DataContext = vm };
                missionPanel.Measure(new Size(800, 240));
                missionPanel.Arrange(new Rect(0, 0, 800, 240));
                missionPanel.UpdateLayout();
            }
            finally
            {
                vm.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public async Task GuidedDefaultAltitudeMeters_AppliesToMapClickGuidedCommands_WhileEnforcingSafetyFloor()
    {
        var transport = new FakeTransport();
        await using MainViewModel vm = CreateTestViewModel(transport, out _);
        await vm.ConnectAsync();

        // FixedWing: operator-configured 120m applies to both RequestGuidedFlyHere and map-click guidedTarget
        transport.EmitIncoming(EncodeHeartbeat(1, MavType.FixedWing, MavAutopilot.ArduPilotMega, 0, seq: 1));
        vm.GuidedDefaultAltitudeMeters = 120.0;
        vm.RequestGuidedFlyHere(39.95, 32.88);
        Assert.Equal(120f, vm.PendingSafetyAction!.AltitudeMeters);
        vm.CancelSafetyAction();

        vm.HandleMapWebMessage(JsonSerializer.Serialize(new { type = "guidedTarget", lat = 39.95, lon = 32.88 }));
        Assert.Equal(120f, vm.PendingSafetyAction!.AltitudeMeters);
        vm.CancelSafetyAction();

        // Below FixedWing 50m floor -> clamped to 50m
        vm.GuidedDefaultAltitudeMeters = 5.0;
        vm.RequestGuidedFlyHere(39.95, 32.88);
        Assert.Equal(50f, vm.PendingSafetyAction!.AltitudeMeters);
        vm.CancelSafetyAction();

        // Multirotor below 20m floor -> clamped to 20m, or respects higher current relative altitude
        transport.EmitIncoming(EncodeHeartbeat(1, MavType.Quadrotor, MavAutopilot.ArduPilotMega, 0, seq: 2));
        vm.GuidedDefaultAltitudeMeters = 5.0;
        vm.RequestGuidedFlyHere(39.95, 32.88);
        Assert.Equal(20f, vm.PendingSafetyAction!.AltitudeMeters);
        vm.CancelSafetyAction();

        transport.EmitIncoming(EncodeGlobalPosition(1, 39.92, 32.85, relAltMeters: 80.0, seq: 3));
        vm.GuidedDefaultAltitudeMeters = 5.0;
        vm.RequestGuidedFlyHere(39.95, 32.88);
        Assert.Equal(80f, vm.PendingSafetyAction!.AltitudeMeters);
        vm.CancelSafetyAction();

        // Rover (SurfaceOnlyMode) always enforces 0m even when GuidedDefaultAltitudeMeters is 120m
        transport.EmitIncoming(EncodeHeartbeat(1, MavType.GroundRover, MavAutopilot.ArduPilotMega, 0, seq: 4));
        vm.GuidedDefaultAltitudeMeters = 120.0;
        vm.RequestGuidedFlyHere(39.95, 32.88);
        Assert.Equal(0f, vm.PendingSafetyAction!.AltitudeMeters);
        vm.CancelSafetyAction();
    }

    [Fact]
    public async Task RallyPoints_MapBridgeAndMavlinkUploadDownloadClear_WorkEndToEnd()
    {
        var transport = new FakeTransport();
        await using MainViewModel vm = CreateTestViewModel(transport, out _);
        await vm.ConnectAsync();

        transport.EmitIncoming(EncodeHeartbeat(1, MavType.Quadrotor, MavAutopilot.ArduPilotMega, customMode: 4u));

        int rallySyncCount = 0;
        vm.RallyPointsChanged += () => rallySyncCount++;

        vm.DefaultRallyAltitudeMeters = 75.0;
        vm.HandleMapWebMessage(JsonSerializer.Serialize(new { type = "rallyAdd", lat = 39.921, lon = 32.851 }));
        vm.HandleMapWebMessage(JsonSerializer.Serialize(new { type = "rallyAdd", lat = 39.931, lon = 32.861 }));
        Assert.Equal(2, vm.RallyPoints.Count);
        Assert.Equal(75f, vm.RallyPoints[0].AltitudeMeters);
        Assert.True(rallySyncCount >= 2);

        vm.HandleMapWebMessage(JsonSerializer.Serialize(new { type = "rallyMove", index = 1, lat = 39.935, lon = 32.865 }));
        Assert.Equal(39.935, vm.RallyPoints[1].Latitude, 6);
        Assert.Equal(32.865, vm.RallyPoints[1].Longitude, 6);

        var uploadedRallyItems = new List<MissionItemIntMessage>();
        byte vehicleSeq = 0;
        bool clearedOnVehicle = false;
        transport.OnOutgoingFrame = frame =>
        {
            if (frame.MessageId == MissionCountMessage.MsgId)
            {
                var countMsg = MissionCountMessage.Decode(frame);
                Assert.Equal(MavMissionType.Rally, countMsg.MissionType);
                var req0 = new MissionRequestIntMessage(255, 190, 0, MavMissionType.Rally);
                transport.EmitIncoming(MavlinkEncoder.EncodeV2(1, 1, vehicleSeq++, req0));
            }
            else if (frame.MessageId == MissionItemIntMessage.MsgId)
            {
                var item = MissionItemIntMessage.Decode(frame);
                uploadedRallyItems.Add(item);
                if (item.Seq + 1 < 2)
                {
                    var nextReq = new MissionRequestIntMessage(255, 190, (ushort)(item.Seq + 1), MavMissionType.Rally);
                    transport.EmitIncoming(MavlinkEncoder.EncodeV2(1, 1, vehicleSeq++, nextReq));
                }
                else
                {
                    var ack = new MissionAckMessage(255, 190, MavMissionResult.Accepted, MavMissionType.Rally);
                    transport.EmitIncoming(MavlinkEncoder.EncodeV2(1, 1, vehicleSeq++, ack));
                }
            }
            else if (frame.MessageId == MissionRequestListMessage.MsgId)
            {
                var reqList = MissionRequestListMessage.Decode(frame);
                Assert.Equal(MavMissionType.Rally, reqList.MissionType);
                var countMsg = new MissionCountMessage(255, 190, 1, MavMissionType.Rally);
                transport.EmitIncoming(MavlinkEncoder.EncodeV2(1, 1, vehicleSeq++, countMsg));
            }
            else if (frame.MessageId == MissionRequestIntMessage.MsgId)
            {
                var reqItem = MissionRequestIntMessage.Decode(frame);
                var rally = new MissionItemIntMessage(
                    255, 190, reqItem.Seq, MavFrame.GlobalRelativeAltInt, MavCmd.NavRallyPoint,
                    0, 1, 90f, 180f, 0f, 0f,
                    (int)Math.Round(39.955 * 1e7), (int)Math.Round(32.875 * 1e7), 85f,
                    MavMissionType.Rally);
                transport.EmitIncoming(MavlinkEncoder.EncodeV2(1, 1, vehicleSeq++, rally));
            }
            else if (frame.MessageId == MissionClearAllMessage.MsgId)
            {
                var clearMsg = MissionClearAllMessage.Decode(frame);
                Assert.Equal(MavMissionType.Rally, clearMsg.MissionType);
                clearedOnVehicle = true;
                var ack = new MissionAckMessage(255, 190, MavMissionResult.Accepted, MavMissionType.Rally);
                transport.EmitIncoming(MavlinkEncoder.EncodeV2(1, 1, vehicleSeq++, ack));
            }
        };

        await vm.UploadRallyAsync();
        Assert.Equal(2, uploadedRallyItems.Count);
        Assert.All(uploadedRallyItems, item =>
        {
            Assert.Equal(MavCmd.NavRallyPoint, item.Command);
            Assert.Equal(MavMissionType.Rally, item.MissionType);
            Assert.Equal(75f, item.Z);
        });

        await vm.DownloadRallyAsync();
        Assert.Single(vm.RallyPoints);
        Assert.Equal(39.955, vm.RallyPoints[0].Latitude, 5);
        Assert.Equal(85f, vm.RallyPoints[0].AltitudeMeters);

        await vm.ClearRallyAsync();
        Assert.True(clearedOnVehicle);
        Assert.Empty(vm.RallyPoints);
    }

    [Fact]
    public async Task FollowVehicleToggleAndSettingsPopup_WorkCleanly()
    {
        var transport = new FakeTransport();
        await using MainViewModel vm = CreateTestViewModel(transport, out _);

        var followEvents = new List<bool>();
        vm.FollowVehicleChanged += enabled => followEvents.Add(enabled);

        Assert.False(vm.IsFollowVehicleEnabled);
        vm.ToggleFollowVehicleCommand.Execute(null);
        Assert.True(vm.IsFollowVehicleEnabled);
        vm.ToggleFollowVehicleCommand.Execute(null);
        Assert.False(vm.IsFollowVehicleEnabled);
        Assert.Equal([true, false], followEvents);

        RunOnStaThread(() =>
        {
            _ = PackUriHelper.UriSchemePack;
            var popup = new SettingsPopupWindow(vm, () => { });
            popup.Measure(new Size(540, 470));
            popup.Arrange(new Rect(0, 0, 540, 470));
            popup.UpdateLayout();
            popup.Close();
        });
    }

    [Fact]
    public async Task ResponsiveFormFactor_AdaptsDesktopTabletAndPhoneLandscapeCleanly()
    {
        var transport = new FakeTransport();
        await using MainViewModel vm = CreateTestViewModel(transport, out _);

        // 1) Desktop (1480 x 900)
        vm.ApplyViewportSize(1480, 900);
        Assert.Equal(CockpitFormFactor.Desktop, vm.FormFactor);
        Assert.True(vm.IsDesktopLayout);
        Assert.True(vm.IsLeftPanelVisible);
        Assert.True(vm.IsMissionDrawerExpanded);
        Assert.True(vm.IsRightLogPanelVisible);
        Assert.Equal(328.0, vm.RightDockWidth);
        Assert.Equal(224.0, vm.RightCameraHeight);
        Assert.Equal(178.0, vm.CompactHudHeight);

        // 2) Tablet (1024 x 768)
        vm.ApplyViewportSize(1024, 768);
        Assert.Equal(CockpitFormFactor.Tablet, vm.FormFactor);
        Assert.True(vm.IsTabletLayout);
        Assert.False(vm.IsLeftPanelVisible);
        Assert.True(vm.IsMissionDrawerExpanded);
        Assert.True(vm.IsRightLogPanelVisible);
        Assert.Equal(278.0, vm.RightDockWidth);
        Assert.Equal(168.0, vm.RightCameraHeight);
        Assert.Equal(146.0, vm.CompactHudHeight);
        Assert.InRange(vm.ResolveExpandedMissionDrawerHeight(), 165.0, 240.0);

        // 3) Phone Landscape (844 x 390) -> drawer collapses automatically so map dominates; log dock hides < 460px height
        vm.ApplyViewportSize(844, 390);
        Assert.Equal(CockpitFormFactor.PhoneLandscape, vm.FormFactor);
        Assert.True(vm.IsPhoneLandscapeLayout);
        Assert.False(vm.IsLeftPanelVisible);
        Assert.False(vm.IsMissionDrawerExpanded);
        Assert.False(vm.IsRightLogPanelVisible);
        Assert.Equal(224.0, vm.RightDockWidth);
        Assert.Equal(116.0, vm.RightCameraHeight);
        Assert.Equal(112.0, vm.CompactHudHeight);
        Assert.InRange(vm.ResolveExpandedMissionDrawerHeight(), 138.0, 196.0);

        // Taller Phone Landscape (880 x 480) re-shows right log dock while staying compact
        vm.ApplyViewportSize(880, 480);
        Assert.Equal(CockpitFormFactor.PhoneLandscape, vm.FormFactor);
        Assert.True(vm.IsRightLogPanelVisible);

        // Back to Desktop restores sidebar + drawer
        vm.ApplyViewportSize(1480, 900);
        Assert.Equal(CockpitFormFactor.Desktop, vm.FormFactor);
        Assert.True(vm.IsLeftPanelVisible);
        Assert.True(vm.IsMissionDrawerExpanded);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                return match;
            }

            T? nested = FindVisualChild<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private sealed class StubNoHoldVehicleProfile : IVehicleProfile
    {
        public VehicleCategory Category => VehicleCategory.Multirotor;
        public string DisplayLabel => "STUB";
        public bool ShowAirspeed => false;
        public bool ShowHorizon => true;
        public bool ShowAltitudeTape => true;
        public bool ShowVtolTransition => false;
        public bool SurfaceOnlyMode => false;
        public IReadOnlyList<MavCmd> SupportedMissionCommands => [MavCmd.NavWaypoint];
        public IReadOnlyList<FlightModeOption> GetFlightModes(MavAutopilot autopilot) => [new FlightModeOption("STABILIZE", 0, 0)];
        public string ResolveModeName(MavAutopilot autopilot, uint customMode, byte baseMode) => "STABILIZE";
        public MavlinkFrame BuildGuidedTargetCommand(byte targetSysId, byte targetCompId, byte gcsSysId, byte gcsCompId, byte seq, MavAutopilot autopilot, double lat, double lon, float altMeters, float? loiterRadius = null) =>
            throw new NotSupportedException();
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (captured is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(captured).Throw();
        }
    }

    private sealed class FakeTransportFactory(FakeTransport transport) : ITransportFactory
    {
        public ITransport CreateSerial(string portName, int baudRate) => transport;
        public ITransport CreateUdp(int listenPort, string? remoteHost = null, int remotePort = 0) => transport;
        public ITransport CreateTcp(string host, int port) => transport;
    }

    private sealed class FakeTransport : ITransport
    {
        private readonly MavlinkParser _parser = new();
        private readonly List<byte[]> _sentPackets = [];
        private readonly Lock _lock = new();

        public bool IsConnected { get; private set; }
        public string EndpointDescription => "FAKE TRANSPORT";
        public Action<MavlinkFrame>? OnOutgoingFrame { get; set; }
        public event Action<ReadOnlyMemory<byte>>? DataReceived;
        public event Action<string>? Disconnected;

        public Task ConnectAsync(CancellationToken ct = default)
        {
            IsConnected = true;
            return Task.CompletedTask;
        }

        public Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
        {
            byte[] copy = bytes.ToArray();
            lock (_lock)
            {
                _sentPackets.Add(copy);
            }

            if (OnOutgoingFrame is { } handler)
            {
                var parser = new MavlinkParser();
                foreach (MavlinkFrame frame in parser.Feed(copy))
                {
                    handler(frame);
                }
            }

            return Task.CompletedTask;
        }

        public void EmitIncoming(byte[] bytes)
        {
            DataReceived?.Invoke(bytes);
        }

        public void RaiseDisconnected(string reason)
        {
            IsConnected = false;
            Disconnected?.Invoke(reason);
        }

        public void ClearSent()
        {
            lock (_lock)
            {
                _sentPackets.Clear();
            }
        }

        public IReadOnlyList<MavlinkFrame> GetSentFramesExcludingHeartbeat()
        {
            lock (_lock)
            {
                var parser = new MavlinkParser();
                var frames = new List<MavlinkFrame>();
                foreach (byte[] pkt in _sentPackets)
                {
                    foreach (MavlinkFrame f in parser.Feed(pkt))
                    {
                        if (f.MessageId != HeartbeatMessage.MsgId && f.MessageId != RequestDataStreamMessage.MsgId)
                        {
                            frames.Add(f);
                        }
                    }
                }

                return frames;
            }
        }

        public ValueTask DisposeAsync()
        {
            IsConnected = false;
            _ = _parser;
            return ValueTask.CompletedTask;
        }
    }
}
