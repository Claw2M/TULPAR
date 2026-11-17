# Tulpar Mission Planner Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build **Tulpar Mission Planner**, a Windows desktop Ground Control Station (GCS) for Pixhawk (ArduPilot & PX4) supporting real USB/Telemetry COM and SITL UDP/TCP flights, automatic MAVLink vehicle adaptation (Fixed-Wing, Multirotor, VTOL, plus Rover/İKA and Boat/İDA), offline-capable MapLibre mapping, live RTSP/UDP/USB camera streaming, and SpaceX-inspired aerospace UI.

**Architecture:** A 4-project `.NET 10` solution (`Tulpar.sln`): `Tulpar.Core` (MAVLink v1/v2 codec, transports, adaptive `IVehicleProfile` engine, mission/geofence protocol state machine, `.tlog`/`.csv` recorder, UDP forwarder), `Tulpar.MapHost` (local HTTP server, SQLite tile cache, `.mbtiles` reader, rate-limited offline region downloader, bundled MapLibre GL JS assets), `Tulpar.App` (WPF shell styled per `DESIGN-spacex.md`, 60Hz vector `HudControl`, WebView2 map bridge, `LibVLCSharp` video feed with Map/Video swap), and `Tulpar.Tests` (xUnit test suite).

**Tech Stack:** C# / .NET 10 (`net10.0` & `net10.0-windows` WPF), `System.IO.Ports`, `Microsoft.Web.WebView2`, `LibVLCSharp.WPF` + `VideoLAN.LibVLC.Windows`, `Microsoft.Data.Sqlite`, MapLibre GL JS (local bundle), xUnit.

**Spec:** [2026-11-17-tulpar-gcs-design.md](file:///c:/Users/Yi%C4%9Fit%20Tarhan/Desktop/Tulpar%20Mission%20Planner/docs/superpowers/specs/2026-11-17-tulpar-gcs-design.md) & Visual Design Reference [DESIGN-spacex.md](file:///c:/Users/Yi%C4%9Fit%20Tarhan/Downloads/DESIGN-spacex.md)

## Global Constraints

- Target Windows 10/11 (`net10.0-windows`) using `.NET 10.0.401`; publish as a self-contained/portable Windows folder (`Tulpar.exe` + native `libvlc`, `WebView2Loader.dll`, `e_sqlite3.dll`) so LibVLC disk plugin discovery works without breaking.
- Visual chrome strictly follows `DESIGN-spacex.md`: `#000000` (`canvas-night`), `#0a0a0a` (`canvas-night-soft`), `#3a3a3f` 1px borders (`hairline-on-dark`), `#ffffff` / `#f0f0fa` text, `D-DIN` / `D-DIN-Bold` bundled fonts (fallback `Arial Narrow, Arial, Verdana`), uppercase display/eyebrows/buttons (`tracking 0.96–1.6px`), `32px` pill ghost buttons (`button-ghost-on-dark`), `4px`/`8px` input/media corners, zero drop shadows/blurs/gradients. Only critical safety alerts (`ARMED`, critical battery, link loss, `RTL`) may use `#FF3B30` / `#FFCC00`.
- Single active vehicle cockpit; never hardcode vehicle types or autopilot mode numbers in UI code (always resolve via `IVehicleProfile`).
- Never block the WPF UI thread on serial/UDP I/O, tile HTTP/SQLite I/O, or video decoding.
- Zero runtime CDN dependency when offline: MapLibre GL JS assets and cached/imported `.mbtiles` tiles must serve locally from `127.0.0.1`.
- MAVLink2 `incompat_flags` framing compliance only (reject unsupported flags such as `MAVLINK_IFLAG_SIGNED = 0x01` safely with warning log); cryptographic packet signing and DEM terrain-following are out of scope for v1.

## Review Focus

- **Split/corrupted MAVLink byte streams across transport reads:** Serial/UDP reads frequently split frames or inject noise; `MavlinkParser` must resynchronize cleanly on the next valid `0xFE`/`0xFD` frame without throwing or dropping subsequent valid packets (pinned in Task 2).
- **Lossy telemetry radios & legacy `MISSION_REQUEST`:** Pixhawk/radios may request out-of-order/duplicate `seq` numbers or send legacy `MISSION_REQUEST` instead of `MISSION_REQUEST_INT`; `MissionProtocolManager` must serve the exact requested `seq` for both `MISSION` and `FENCE` and retry cleanly up to 5×1500ms (pinned in Task 4).
- **Unknown `MAV_TYPE` or mid-session vehicle swap on reconnect:** Connecting a new platform or an unrecognized `MAV_TYPE` must reset stale telemetry/home/mission state and select a safe fallback profile without crashing (pinned in Task 3).
- **Offline map failure & TMS Y-flip in `.mbtiles`:** When offline or upstream throttles (403/429), `Tulpar.MapHost` must serve cached/imported `.mbtiles` (converting TMS `y` to XYZ `y` via `(1 << z) - 1 - y`) and return HTTP `204` instead of hanging WebView2 (pinned in Task 6).
- **Sudden USB COM unplug & accidental mid-flight safety clicks:** Disconnecting USB mid-stream must tear down `SerialTransport` without unhandled exceptions, and `Arm`, `RTL`, and map right-click `Guided / Fly Here` commands must require explicit confirmation state before emitting MAVLink packets (pinned in Task 5 & Task 8).

---

### Task 1: Repository Init, Solution Scaffolding & Dependencies

**Files:**
- Create: `.gitignore`
- Create: `Tulpar.sln`
- Create: `src/Tulpar.Core/Tulpar.Core.csproj`
- Create: `src/Tulpar.MapHost/Tulpar.MapHost.csproj`
- Create: `src/Tulpar.App/Tulpar.App.csproj`
- Create: `tests/Tulpar.Tests/Tulpar.Tests.csproj`

**Interfaces:**
- Produces: Compilable `.NET 10` solution structure with project references (`App -> Core, MapHost`; `Tests (net10.0-windows) -> Core, MapHost, App`) and NuGet packages (`System.IO.Ports`, `Microsoft.Data.Sqlite`, `Microsoft.Web.WebView2`, `LibVLCSharp.WPF`, `VideoLAN.LibVLC.Windows`, `xunit`).

- [ ] **Step 1: Initialize git repo and `.gitignore`**
  Create `.gitignore` covering `bin/`, `obj/`, `.vs/`, `*.user`, `publish/`, and run `git init`.

- [ ] **Step 2: Create solution and 4 projects via `dotnet` CLI**
  - `dotnet new sln -n Tulpar`
  - `dotnet new classlib -n Tulpar.Core -o src/Tulpar.Core -f net10.0`
  - `dotnet new classlib -n Tulpar.MapHost -o src/Tulpar.MapHost -f net10.0`
  - `dotnet new wpf -n Tulpar.App -o src/Tulpar.App -f net10.0-windows`
  - `dotnet new xunit -n Tulpar.Tests -o tests/Tulpar.Tests -f net10.0-windows`
  - Add projects to `Tulpar.sln`, configure project references, and add required NuGet packages.

- [ ] **Step 3: Verify empty baseline builds and passes tests**
  Run: `dotnet test Tulpar.sln`
  Expected: Build succeeds with 0 errors and baseline xUnit test passes.

- [ ] **Step 4: Commit**
  `git add .gitignore Tulpar.sln src/ tests/ && git commit -m "chore: scaffold Tulpar .NET 10 solution"`

---

### Task 2: MAVLink v1 & v2 Binary Codec (`Tulpar.Core`)

**Files:**
- Create: `src/Tulpar.Core/Mavlink/MavlinkCrc.cs`
- Create: `src/Tulpar.Core/Mavlink/MavlinkFrame.cs`
- Create: `src/Tulpar.Core/Mavlink/MavlinkEnums.cs`
- Create: `src/Tulpar.Core/Mavlink/MavlinkMessages.cs`
- Create: `src/Tulpar.Core/Mavlink/MavlinkParser.cs`
- Create: `src/Tulpar.Core/Mavlink/MavlinkEncoder.cs`
- Test: `tests/Tulpar.Tests/MavlinkCodecTests.cs`

**Interfaces:**
- Consumes: Standard .NET BCL (`System.Buffers.Binary`).
- Produces:
  - `public sealed record MavlinkFrame(byte Version, byte Sequence, byte SystemId, byte ComponentId, uint MessageId, ReadOnlyMemory<byte> Payload, byte IncompatFlags = 0, byte CompatFlags = 0)`
  - `public sealed class MavlinkParser`: `IEnumerable<MavlinkFrame> Feed(ReadOnlySpan<byte> bytes)`, `event Action<string>? FrameWarning`
  - `public static class MavlinkEncoder`: `byte[] EncodeV2(byte sysId, byte compId, byte seq, uint messageId, ReadOnlySpan<byte> payload, byte crcExtra)`
  - `public static class MavlinkCrc`: `ushort Compute(ReadOnlySpan<byte> data, byte crcExtra)` and `bool TryGetCrcExtra(uint messageId, out byte crcExtra)`
  - Strongly-typed message structs/codecs (`HeartbeatMessage`, `SysStatusMessage`, `GpsRawIntMessage`, `GlobalPositionIntMessage`, `AttitudeMessage`, `VfrHudMessage`, `BatteryStatusMessage`, `RadioStatusMessage`, `StatusTextMessage`, `CommandLongMessage`, `CommandAckMessage`, `SetModeMessage`, `RequestDataStreamMessage`, `SetPositionTargetGlobalIntMessage`, `MissionCountMessage`, `MissionRequestIntMessage`, `MissionItemIntMessage`, `MissionAckMessage`, `MissionRequestListMessage`, `MissionClearAllMessage`, `MissionCurrentMessage`, `HomePositionMessage`).

- [ ] **Step 1: Write failing tests in `tests/Tulpar.Tests/MavlinkCodecTests.cs`**
  - `RoundTrip_V2_Heartbeat_And_MissionItemInt_PreservesFieldsAndCrc()`
  - `Decode_V1_Attitude_Packet_ComputesValidCrc()`
  - `Feed_SplitAndCorruptedBytesAcrossChunks_ResyncsAndEmitsValidFrames()` (Review Focus #1: feed random garbage + partial v2 header + split valid frames byte-by-byte and assert exact decoded frames).
  - `Feed_V2SignedIncompatFlag_LogsWarningAndSkipsFrameWithoutThrowing()` (assert `incompat_flags = 0x01` triggers `FrameWarning` and does not crash).

- [ ] **Step 2: Run test to verify it fails**
  Run: `dotnet test tests/Tulpar.Tests --filter MavlinkCodecTests`
  Expected: FAIL (types not defined).

- [ ] **Step 3: Implement `MavlinkCrc`, `MavlinkEnums`, `MavlinkFrame`, `MavlinkMessages`, `MavlinkParser`, `MavlinkEncoder`**
  Include exact MAVLink CRC_EXTRA constants for every supported message ID (`0, 1, 2, 24, 30, 33, 39, 40, 44, 45, 46, 47, 51, 66, 73, 74, 76, 77, 86, 109, 147, 242, 253` + `SET_MODE=11`, `RC_CHANNELS=65`, `MISSION_SET_CURRENT=41`, `MISSION_CURRENT=42`), v2 zero-truncation on encode, and v2 payload zero-padding on decode.

- [ ] **Step 4: Run tests and verify pass**
  Run: `dotnet test tests/Tulpar.Tests --filter MavlinkCodecTests`
  Expected: PASS (all 4 tests green).

- [ ] **Step 5: Commit**
  `git add src/Tulpar.Core/Mavlink tests/Tulpar.Tests/MavlinkCodecTests.cs && git commit -m "feat(core): add MAVLink v1/v2 codec and CRC parser"`

---

### Task 3: Adaptive Vehicle Profile Engine & Telemetry Session (`Tulpar.Core`)

**Files:**
- Create: `src/Tulpar.Core/Vehicles/IVehicleProfile.cs`
- Create: `src/Tulpar.Core/Vehicles/FixedWingProfile.cs`
- Create: `src/Tulpar.Core/Vehicles/MultirotorProfile.cs`
- Create: `src/Tulpar.Core/Vehicles/VtolProfile.cs`
- Create: `src/Tulpar.Core/Vehicles/RoverIkaProfile.cs`
- Create: `src/Tulpar.Core/Vehicles/BoatIdaProfile.cs`
- Create: `src/Tulpar.Core/Vehicles/VehicleProfileFactory.cs`
- Create: `src/Tulpar.Core/Session/TelemetrySnapshot.cs`
- Create: `src/Tulpar.Core/Session/TelemetryBus.cs`
- Create: `src/Tulpar.Core/Session/VehicleSession.cs`
- Test: `tests/Tulpar.Tests/VehicleProfileAndSessionTests.cs`

**Interfaces:**
- Consumes: `MavlinkFrame`, `MavlinkEnums`, `MavlinkMessages`, `MavlinkEncoder`.
- Produces:
  - `public interface IVehicleProfile`: `VehicleCategory Category`, `string DisplayLabel`, `bool ShowAirspeed`, `bool ShowHorizon`, `bool ShowAltitudeTape`, `bool ShowVtolTransition`, `bool SurfaceOnlyMode`, `IReadOnlyList<FlightModeOption> GetFlightModes(MavAutopilot autopilot)`, `string ResolveModeName(MavAutopilot autopilot, uint customMode, byte baseMode)`, `IReadOnlyList<MavCmd> SupportedMissionCommands`, `MavlinkFrame BuildGuidedTargetCommand(byte sysId, byte compId, byte seq, MavAutopilot autopilot, double lat, double lon, float altMeters, float? loiterRadius = null)`
  - `public static class VehicleProfileFactory`: `IVehicleProfile Create(MavType type, MavAutopilot autopilot)`
  - `public sealed class TelemetryBus`: `event Action<TelemetrySnapshot>? SnapshotUpdated`, `event Action<StatusTextMessage>? StatusTextReceived`, `event Action<MavlinkFrame>? RawFrameReceived`
  - `public sealed class VehicleSession`: `TelemetrySnapshot Current { get; }`, `IVehicleProfile? ActiveProfile { get; }`, `void ProcessIncomingBytes(ReadOnlySpan<byte> data)`, `void CheckHeartbeatTimeout(DateTimeOffset now)`, `void ResetOnDisconnect()`

- [ ] **Step 1: Write failing tests in `tests/Tulpar.Tests/VehicleProfileAndSessionTests.cs`**
  - `Factory_DetectsAllFiveVehicleCategories_AndFallbackOnUnknownMavType()` (Review Focus #3: verify `FIXED_WING`, `QUADROTOR`, `VTOL_TILTROTOR`, `GROUND_ROVER`, `SURFACE_BOAT`, plus unknown `MavType=250` returning safe fallback profile without throwing).
  - `ArduPilot_And_PX4_CustomModes_ResolveAccuratelyPerPlatform()`
  - `Session_ReconnectWithDifferentVehicle_ResetsStalePositionHomeAndUpdatesProfile()` (Review Focus #3).
  - `Session_HeartbeatTimeoutOverThreeSeconds_MarksLinkLost()`

- [ ] **Step 2: Run test to verify it fails**
  Run: `dotnet test tests/Tulpar.Tests --filter VehicleProfileAndSessionTests`
  Expected: FAIL.

- [ ] **Step 3: Implement profiles, `VehicleProfileFactory`, `TelemetrySnapshot`, `TelemetryBus`, and `VehicleSession`**
  Ensure `FixedWingProfile` enables `ShowAirspeed=true`, `LOITER_TURNS`, `TAKEOFF` pitch; `MultirotorProfile` enables `SPLINE_WAYPOINT`, `ROI`; `VtolProfile` enables `DO_VTOL_TRANSITION`; `RoverIkaProfile` & `BoatIdaProfile` set `SurfaceOnlyMode=true`, `ShowHorizon=false`, `ShowAltitudeTape=false`, and surface `WAYPOINT` (alt `0m`).

- [ ] **Step 4: Run tests and verify pass**
  Run: `dotnet test tests/Tulpar.Tests --filter VehicleProfileAndSessionTests`
  Expected: PASS.

- [ ] **Step 5: Commit**
  `git add src/Tulpar.Core/Vehicles src/Tulpar.Core/Session tests/Tulpar.Tests/VehicleProfileAndSessionTests.cs && git commit -m "feat(core): add adaptive vehicle profiles and telemetry session"`

---

### Task 4: Mission & Geofence Protocol Manager + File Codecs (`Tulpar.Core`)

**Files:**
- Create: `src/Tulpar.Core/Missions/MissionItem.cs`
- Create: `src/Tulpar.Core/Missions/MissionFileCodec.cs`
- Create: `src/Tulpar.Core/Missions/MissionProtocolManager.cs`
- Test: `tests/Tulpar.Tests/MissionProtocolTests.cs`

**Interfaces:**
- Consumes: `MavlinkFrame`, `MavlinkMessages`, `MavlinkEncoder`.
- Produces:
  - `public sealed record MissionItem(int Seq, MavFrame Frame, MavCmd Command, float Param1, float Param2, float Param3, float Param4, double Latitude, double Longitude, float Altitude, bool Autocontinue = true)`
  - `public static class MissionFileCodec`: `string ExportQgcWpl110(IReadOnlyList<MissionItem> items)`, `IReadOnlyList<MissionItem> ImportQgcWpl110(string content)`, `string ExportJson(...)`, `MissionDocument ImportJson(string json)`
  - `public sealed class MissionProtocolManager`:
    - `Task UploadAsync(MavMissionType missionType, IReadOnlyList<MissionItem> items, Func<byte[], CancellationToken, Task> sendAsync, IProgress<MissionTransferProgress>? progress, CancellationToken ct)`
    - `Task<IReadOnlyList<MissionItem>> DownloadAsync(MavMissionType missionType, Func<byte[], CancellationToken, Task> sendAsync, IProgress<MissionTransferProgress>? progress, CancellationToken ct)`
    - `Task ClearAllAsync(MavMissionType missionType, Func<byte[], CancellationToken, Task> sendAsync, CancellationToken ct)`
    - `void HandleIncomingFrame(MavlinkFrame frame)`

- [ ] **Step 1: Write failing tests in `tests/Tulpar.Tests/MissionProtocolTests.cs`**
  - `UploadMission_HandlesLegacyMissionRequestAndDuplicateOutOfOrderSeq_AndCompletesOnAck()` (Review Focus #2).
  - `UploadFence_UsesMissionTypeFence_AndRetriesAfterTimeout()` (Review Focus #2: drop 1st `MISSION_COUNT` response, verify retry after timeout, verify `MAV_MISSION_TYPE_FENCE`).
  - `DownloadMission_RequestsAllSeqsAndReturnsOrderedItems()`
  - `QgcWpl110_RoundTrip_PreservesRelativeAndAmslFramesAndCoordinates()`

- [ ] **Step 2: Run test to verify it fails**
  Run: `dotnet test tests/Tulpar.Tests --filter MissionProtocolTests`
  Expected: FAIL.

- [ ] **Step 3: Implement `MissionItem`, `MissionFileCodec`, and `MissionProtocolManager`**
  Use `1500ms` per-step timeout and up to `5` retries; respond to both `MISSION_REQUEST_INT (51)` and legacy `MISSION_REQUEST (40)`.

- [ ] **Step 4: Run tests and verify pass**
  Run: `dotnet test tests/Tulpar.Tests --filter MissionProtocolTests`
  Expected: PASS.

- [ ] **Step 5: Commit**
  `git add src/Tulpar.Core/Missions tests/Tulpar.Tests/MissionProtocolTests.cs && git commit -m "feat(core): add mission/geofence protocol state machine and WPL110 codec"`

---

### Task 5: Transports (`Serial`, `UDP`, `TCP`), UDP Forwarder & Telemetry Recorder (`Tulpar.Core`)

**Files:**
- Create: `src/Tulpar.Core/Transport/ITransport.cs`
- Create: `src/Tulpar.Core/Transport/SerialTransport.cs`
- Create: `src/Tulpar.Core/Transport/UdpTransport.cs`
- Create: `src/Tulpar.Core/Transport/TcpTransport.cs`
- Create: `src/Tulpar.Core/Transport/MavlinkUdpForwarder.cs`
- Create: `src/Tulpar.Core/Session/TelemetryRecorder.cs`
- Test: `tests/Tulpar.Tests/TransportAndRecorderTests.cs`

**Interfaces:**
- Consumes: `MavlinkFrame`, `TelemetrySnapshot`, `TelemetryBus`.
- Produces:
  - `public interface ITransport : IAsyncDisposable`: `bool IsConnected`, `event Action<ReadOnlyMemory<byte>>? DataReceived`, `event Action<string>? Disconnected`, `Task ConnectAsync(CancellationToken ct)`, `Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)`
  - `SerialTransport(string portName, int baudRate)`, `UdpTransport(int listenPort, string? remoteHost = null, int remotePort = 0)`, `TcpTransport(string host, int port)`
  - `MavlinkUdpForwarder(string targetHost, int targetPort)`: `void ForwardRawBytes(ReadOnlySpan<byte> rawFrameBytes)`
  - `TelemetryRecorder(string logDirectory)`: `void RecordRawFrame(DateTimeOffset utcNow, ReadOnlySpan<byte> rawPacket)`, `void RecordSnapshot(TelemetrySnapshot snapshot)`

- [ ] **Step 1: Write failing tests in `tests/Tulpar.Tests/TransportAndRecorderTests.cs`**
  - `UdpTransport_LoopbackExchange_DeliversBytesAndCleansUpOnDispose()`
  - `MavlinkUdpForwarder_RelaysRawPacketsToLocalhostListener()`
  - `TelemetryRecorder_WritesValidTlogTimestampHeaderAndCsvRow()`
  - `SerialTransport_InvalidPortOrSuddenClose_RaisesDisconnectedWithoutCrash()` (Review Focus #5).

- [ ] **Step 2: Run test to verify it fails**
  Run: `dotnet test tests/Tulpar.Tests --filter TransportAndRecorderTests`
  Expected: FAIL.

- [ ] **Step 3: Implement transports, `MavlinkUdpForwarder`, and `TelemetryRecorder`**
  In `TelemetryRecorder`, `.tlog` writes big-endian 64-bit microseconds since Unix epoch followed by raw MAVLink packet bytes (Mission Planner compatible).

- [ ] **Step 4: Run tests and verify pass**
  Run: `dotnet test tests/Tulpar.Tests --filter TransportAndRecorderTests`
  Expected: PASS.

- [ ] **Step 5: Commit**
  `git add src/Tulpar.Core/Transport src/Tulpar.Core/Session/TelemetryRecorder.cs tests/Tulpar.Tests/TransportAndRecorderTests.cs && git commit -m "feat(core): add COM/UDP/TCP transports, UDP forwarder, and TLOG/CSV recorder"`

---

### Task 6: Offline-First Map Tile Server, `.mbtiles` Reader & Downloader (`Tulpar.MapHost`)

**Files:**
- Create: `src/Tulpar.MapHost/TileProvider.cs`
- Create: `src/Tulpar.MapHost/TileCacheStore.cs`
- Create: `src/Tulpar.MapHost/MbtilesReader.cs`
- Create: `src/Tulpar.MapHost/OfflineRegionDownloader.cs`
- Create: `src/Tulpar.MapHost/MapHttpServer.cs`
- Create: `src/Tulpar.MapHost/Assets/Map/maplibre-gl.js` (and `.css`) + `index.html` + `map.js`
- Test: `tests/Tulpar.Tests/MapHostTests.cs`

**Interfaces:**
- Consumes: `Microsoft.Data.Sqlite`, `System.Net.Http`.
- Produces:
  - `public sealed record TileProviderConfig(string Id, string Name, string UrlTemplate, int MaxZoom, int MinZoom = 0)`
  - `public sealed class TileCacheStore`: `Task<byte[]?> TryGetTileAsync(string providerId, int z, int x, int y)`, `Task PutTileAsync(string providerId, int z, int x, int y, byte[] data)`
  - `public sealed class MbtilesReader : IDisposable`: `byte[]? TryGetTileXyz(int z, int x, int y)` (handles TMS `y = (1 << z) - 1 - y` flip!)
  - `public sealed class OfflineRegionDownloader`: `Task<OfflineDownloadResult> DownloadBoundsAsync(TileProviderConfig provider, double minLat, double minLon, double maxLat, double maxLon, int minZoom, int maxZoom, int maxTilesLimit, IProgress<OfflineDownloadProgress>? progress, CancellationToken ct)`
  - `public sealed class MapHttpServer : IAsyncDisposable`: `Uri BaseUri { get; }`, `Task StartAsync(CancellationToken ct)`, `void RegisterMbtiles(string layerId, string mbtilesPath)`

- [ ] **Step 1: Write failing tests in `tests/Tulpar.Tests/MapHostTests.cs`**
  - `TileCacheStore_PutAndGet_PersistsAcrossReopen()`
  - `MbtilesReader_ConvertsXyzToTmsYCoordinateCorrectly()` (Review Focus #4).
  - `MapHttpServer_OfflineUpstreamFailure_ServesCachedTileOrReturns204WithoutHanging()` (Review Focus #4).
  - `OfflineRegionDownloader_EnforcesMaxTileLimitAndRateDelay()`

- [ ] **Step 2: Run test to verify it fails**
  Run: `dotnet test tests/Tulpar.Tests --filter MapHostTests`
  Expected: FAIL.

- [ ] **Step 3: Bundle MapLibre GL JS assets locally and implement `TileProvider`, `TileCacheStore`, `MbtilesReader`, `OfflineRegionDownloader`, `MapHttpServer`**
  Download official MapLibre GL JS dist (`maplibre-gl.js`, `maplibre-gl.css`) into `src/Tulpar.MapHost/Assets/Map/` (copied to output), build local `index.html` + `map.js` supporting vehicle marker SVG switching (FixedWing/Multirotor/VTOL/Rover/Boat), 500-point flight trail, Home marker, drag-and-drop waypoint editor, polygon geofence editor, and right-click Guided target picker (`window.chrome.webview.postMessage`).

- [ ] **Step 4: Run tests and verify pass**
  Run: `dotnet test tests/Tulpar.Tests --filter MapHostTests`
  Expected: PASS.

- [ ] **Step 5: Commit**
  `git add src/Tulpar.MapHost tests/Tulpar.Tests/MapHostTests.cs && git commit -m "feat(maphost): add offline tile cache, mbtiles reader, and local MapLibre server"`

---

### Task 7: SpaceX Design System Theme (`DESIGN-spacex.md`) & Adaptive Vector `HudControl` (`Tulpar.App`)

**Files:**
- Create: `src/Tulpar.App/Assets/Fonts/D-DIN.ttf` & `D-DIN-Bold.ttf` (with `Arial Narrow, Arial, Verdana` fallback)
- Create: `src/Tulpar.App/Themes/SpaceXTheme.xaml`
- Create: `src/Tulpar.App/Controls/HudControl.cs`
- Test: `tests/Tulpar.Tests/HudModelTests.cs`

**Interfaces:**
- Consumes: `TelemetrySnapshot`, `IVehicleProfile` from `Tulpar.Core`; tokens from `DESIGN-spacex.md`.
- Produces:
  - `SpaceXTheme.xaml` (`CanvasNightBrush=#000000`, `CanvasNightSoftBrush=#0a0a0a`, `HairlineDarkBrush=#3a3a3f`, `OnPrimaryBrush=#ffffff`, `OnPrimaryMuteBrush=#f0f0fa`, `GhostPillButtonStyle` with `CornerRadius=32`, `SpaceXTextBoxStyle` with `CornerRadius=4`, `ButtonCapTextStyle` / `MicroCapTextStyle` / `DisplayTelemetryStyle`, zero drop shadows).
  - `public sealed class HudControl : FrameworkElement` + `public static class HudProjection`: pure math helper computing artificial horizon pitch/roll transforms, speed/altitude tape ticks, compass tape ticks, and adaptive surface (`İKA`/`İDA`) mode visibility flags.

- [ ] **Step 1: Write failing tests in `tests/Tulpar.Tests/HudModelTests.cs`**
  - `HudProjection_FixedWingVsBoatProfile_TogglesAirspeedHorizonAndAltitudeTapes()`
  - `HudProjection_ClampsPitchAndNormalizesHeadingWrapping359To0()`

- [ ] **Step 2: Run test to verify it fails**
  Run: `dotnet test tests/Tulpar.Tests --filter HudModelTests`
  Expected: FAIL.

- [ ] **Step 3: Add `D-DIN` fonts, `SpaceXTheme.xaml`, and `HudControl` + `HudProjection`**
  Ensure `HudControl` draws crisp `#ffffff` / `#f0f0fa` vector lines on `#000000` / `#0a0a0a` canvas (with optional `#FF3B30` hazard indicator only when `IsArmed` or critical warning), adapting cleanly when `SurfaceOnlyMode` is true.

- [ ] **Step 4: Run tests and verify pass**
  Run: `dotnet test tests/Tulpar.Tests --filter HudModelTests`
  Expected: PASS.

- [ ] **Step 5: Commit**
  `git add src/Tulpar.App/Assets src/Tulpar.App/Themes src/Tulpar.App/Controls/HudControl.cs tests/Tulpar.Tests/HudModelTests.cs && git commit -m "feat(app): add SpaceX design system theme and adaptive vector HUD"`

---

### Task 8: Cockpit Shell, Map/Video Swap, Mission/Geofence Drawer & Safety Guards (`Tulpar.App`)

**Files:**
- Create: `src/Tulpar.App/ViewModels/MainViewModel.cs`
- Create: `src/Tulpar.App/ViewModels/RelayCommand.cs`
- Create: `src/Tulpar.App/Controls/VideoFeedControl.xaml` & `.cs`
- Create: `src/Tulpar.App/Controls/MapHostControl.xaml` & `.cs`
- Create: `src/Tulpar.App/Controls/MissionPlannerPanel.xaml` & `.cs`
- Create: `src/Tulpar.App/Controls/OfflineMapDialog.xaml` & `.cs`
- Modify: `src/Tulpar.App/App.xaml` & `src/Tulpar.App/MainWindow.xaml` & `.cs`
- Test: `tests/Tulpar.Tests/MainViewModelTests.cs`

**Interfaces:**
- Consumes: `Tulpar.Core` (`VehicleSession`, `MissionProtocolManager`, `ITransport`, `MavlinkUdpForwarder`, `TelemetryRecorder`), `Tulpar.MapHost` (`MapHttpServer`, `OfflineRegionDownloader`), `HudControl`, `SpaceXTheme.xaml`.
- Produces: Complete Windows WPF application (`Tulpar.App`) with:
  - Top Command Bar (`48px`): COM/UDP/TCP connect selector, auto vehicle badge, mode picker, ARMED badge, battery/GPS/RSSI, guarded `RTL` & `HOLD` buttons.
  - Left Column (`360px`, collapsible below `1280px`): `HudControl` + `TELEMETRİ` / `KOMUTLAR` / `GÜNLÜK` tabs.
  - Main Stage: `MapHostControl` (WebView2 + MapLibre) + PiP `VideoFeedControl` (`LibVLCSharp` supporting RTSP, UDP/RTP, `dshow://` USB capture devices) with 1-click Map↔Camera swap.
  - Bottom Drawer: Adaptive Mission Waypoint & Geofence Planner (`REL` / `AMSL`, Upload/Download/Clear/Open/Save) + local geofence point-in-polygon breach check.
  - `OfflineMapDialog`: bounding-box offline downloader + `.mbtiles` file picker + custom XYZ URL input.

- [ ] **Step 1: Write failing tests in `tests/Tulpar.Tests/MainViewModelTests.cs`**
  - `SafetyGuards_ArmRtlAndGuidedFlyHere_RequireConfirmationBeforeSendingCommand()` (Review Focus #5).
  - `LocalGeofenceBreachCheck_TriggersAlertWhenVehicleLeavesInclusionPolygon()`
  - `SwapMapAndCamera_TogglesPrimarySurfaceStateWithoutDroppingVideoOrTelemetry()`
  - `VehicleTypeSwitch_UpdatesCommandBarModesAndMissionCommandPalette()`

- [ ] **Step 2: Run test to verify it fails**
  Run: `dotnet test tests/Tulpar.Tests --filter MainViewModelTests`
  Expected: FAIL.

- [ ] **Step 3: Implement `MainViewModel`, `VideoFeedControl`, `MapHostControl`, `MissionPlannerPanel`, `OfflineMapDialog`, and `MainWindow.xaml`**
  Wire `Tulpar.App` cleanly with async background transport loops dispatching UI updates via `Dispatcher` throttled to ~30Hz for panels and 60Hz for HUD.

- [ ] **Step 4: Run full test suite & build portable release**
  - Run: `dotnet test Tulpar.sln` -> Expected: All tests pass.
  - Run: `dotnet publish src/Tulpar.App/Tulpar.App.csproj -c Release -r win-x64 --self-contained true -o publish/Tulpar-win-x64` -> Verify `Tulpar.exe` + `libvlc` plugins + `Assets/Map` exist and launch smoke test opens cleanly.

- [ ] **Step 5: Commit**
  `git add src/Tulpar.App tests/Tulpar.Tests/MainViewModelTests.cs && git commit -m "feat(app): build complete Tulpar GCS cockpit with map, video, and mission planner"`
