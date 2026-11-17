using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Tulpar.Core.Mavlink;
using Tulpar.Core.Session;
using Tulpar.Core.Transport;
using Tulpar.Core.Vehicles;

namespace Tulpar.Tests;

public class TransportAndRecorderTests
{
    private static byte[] EncodeHeartbeat(byte sysId = 1, byte seq = 1) =>
        MavlinkEncoder.EncodeV2(
            sysId,
            1,
            seq,
            new HeartbeatMessage(
                MavType.Quadrotor,
                MavAutopilot.ArduPilotMega,
                MavModeFlag.CustomModeEnabled | MavModeFlag.SafetyArmed,
                4u,
                MavState.Active));

    [Fact]
    public async Task UdpTransport_LoopbackExchange_DeliversBytesAndLearnsRemoteEndpoint()
    {
        await using var gcsTransport = new UdpTransport(listenPort: 0);
        var receivedTcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnectedEvents = new List<string>();

        gcsTransport.DataReceived += data => receivedTcs.TrySetResult(data.ToArray());
        gcsTransport.Disconnected += reason => disconnectedEvents.Add(reason);

        await gcsTransport.ConnectAsync(CancellationToken.None);
        Assert.True(gcsTransport.IsConnected);
        Assert.True(gcsTransport.LocalPort > 0);
        Assert.Contains("UDP", gcsTransport.EndpointDescription, StringComparison.OrdinalIgnoreCase);

        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int peerPort = ((IPEndPoint)peer.Client.LocalEndPoint!).Port;

        byte[] vehicleHeartbeat = EncodeHeartbeat(sysId: 1, seq: 7);
        await peer.SendAsync(vehicleHeartbeat, new IPEndPoint(IPAddress.Loopback, gcsTransport.LocalPort));

        byte[] receivedAtGcs = await receivedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(vehicleHeartbeat, receivedAtGcs);

        // Remote endpoint should now be learned from the first packet, so SendAsync delivers back to peer.
        byte[] gcsAck = MavlinkEncoder.EncodeV2(
            255,
            190,
            1,
            new CommandAckMessage(MavCmd.ComponentArmDisarm, MavResult.Accepted));
        await gcsTransport.SendAsync(gcsAck, CancellationToken.None);

        UdpReceiveResult peerResult = await peer.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(gcsAck, peerResult.Buffer);
        Assert.Equal(gcsTransport.LocalPort, peerResult.RemoteEndPoint.Port);
        Assert.Equal(peerPort, ((IPEndPoint)peer.Client.LocalEndPoint!).Port);

        await gcsTransport.DisposeAsync();
        await gcsTransport.DisposeAsync(); // Idempotent dispose
        Assert.False(gcsTransport.IsConnected);
        Assert.Empty(disconnectedEvents);
    }

    [Fact]
    public async Task UdpTransport_ConfiguredRemoteAndDisposeAsync_CancelsCleanlyWithoutObjectDisposedException()
    {
        using var remote = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int remotePort = ((IPEndPoint)remote.Client.LocalEndPoint!).Port;

        await using var udp = new UdpTransport(listenPort: 0, remoteHost: "127.0.0.1", remotePort: remotePort);
        var disconnected = new List<string>();
        udp.Disconnected += disconnected.Add;

        await udp.ConnectAsync(CancellationToken.None);
        Assert.True(udp.IsConnected);

        byte[] ping = EncodeHeartbeat(sysId: 255, seq: 1);
        await udp.SendAsync(ping, CancellationToken.None);

        UdpReceiveResult received = await remote.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ping, received.Buffer);

        // Send a packet while remote listener is closed to verify Windows ICMP ConnectionReset does not kill loop
        remote.Dispose();
        await udp.SendAsync(ping, CancellationToken.None);
        await Task.Delay(50);
        Assert.True(udp.IsConnected);

        await udp.DisposeAsync();
        await udp.DisposeAsync();
        Assert.False(udp.IsConnected);
        Assert.Empty(disconnected);
    }

    [Fact]
    public async Task TcpTransport_LoopbackExchangeAndUnexpectedServerClose_RaisesDisconnectedCleanly()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync();

        await using var tcp = new TcpTransport("127.0.0.1", port);
        var receivedTcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnectedTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        int disconnectCount = 0;

        tcp.DataReceived += data => receivedTcs.TrySetResult(data.ToArray());
        tcp.Disconnected += reason =>
        {
            Interlocked.Increment(ref disconnectCount);
            disconnectedTcs.TrySetResult(reason);
        };

        await tcp.ConnectAsync(CancellationToken.None);
        Assert.True(tcp.IsConnected);
        Assert.Contains(port.ToString(CultureInfo.InvariantCulture), tcp.EndpointDescription, StringComparison.Ordinal);

        using TcpClient serverClient = await acceptTask.WaitAsync(TimeSpan.FromSeconds(5));
        using NetworkStream serverStream = serverClient.GetStream();

        byte[] tx = EncodeHeartbeat(sysId: 255, seq: 3);
        await tcp.SendAsync(tx, CancellationToken.None);

        byte[] serverBuf = new byte[tx.Length];
        await serverStream.ReadExactlyAsync(serverBuf).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(tx, serverBuf);

        byte[] rx = EncodeHeartbeat(sysId: 1, seq: 9);
        await serverStream.WriteAsync(rx);
        await serverStream.FlushAsync();

        byte[] clientReceived = await receivedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(rx, clientReceived);

        // Abrupt server close must raise Disconnected once without unhandled exceptions
        serverClient.Close();

        string disconnectReason = await disconnectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(string.IsNullOrWhiteSpace(disconnectReason));
        Assert.False(tcp.IsConnected);
        Assert.Equal(1, disconnectCount);

        await tcp.DisposeAsync();
    }

    [Fact]
    public async Task TcpTransport_DisposeWhileReceiveBlocked_CancelsCleanlyWithoutObjectDisposedException()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync();
        await using var tcp = new TcpTransport("localhost", port);
        var disconnected = new List<string>();
        tcp.Disconnected += disconnected.Add;

        await tcp.ConnectAsync(CancellationToken.None);
        using TcpClient accepted = await acceptTask.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(30);

        await tcp.DisposeAsync();
        await tcp.DisposeAsync();

        Assert.False(tcp.IsConnected);
        Assert.Empty(disconnected);
    }

    [Fact]
    public async Task MavlinkUdpForwarder_RelaysRawPacketsToLocalhostListener()
    {
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int listenerPort = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;

        using var forwarder = new MavlinkUdpForwarder("127.0.0.1", listenerPort);
        Assert.True(forwarder.IsEnabled);

        byte[] packet1 = EncodeHeartbeat(sysId: 1, seq: 1);
        forwarder.ForwardRawBytes(packet1);

        UdpReceiveResult received1 = await listener.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(packet1, received1.Buffer);

        // When disabled, packets must not be relayed.
        forwarder.IsEnabled = false;
        byte[] suppressedPacket = EncodeHeartbeat(sysId: 1, seq: 2);
        forwarder.ForwardRawBytes(suppressedPacket);

        // Re-enable and send a distinguishing third packet to prove packet 2 was skipped.
        forwarder.IsEnabled = true;
        byte[] packet3 = EncodeHeartbeat(sysId: 1, seq: 3);
        forwarder.ForwardRawBytes(packet3);

        UdpReceiveResult received3 = await listener.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(packet3, received3.Buffer);

        // Closed listener must never crash ForwardRawBytes.
        listener.Dispose();
        Exception? ex = Record.Exception(() => forwarder.ForwardRawBytes(packet1));
        Assert.Null(ex);
    }

    [Fact]
    public void TelemetryRecorder_WritesValidTlogTimestampHeaderAndCsvRow()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "tulpar-recorder-test-" + Guid.NewGuid().ToString("N"));

        try
        {
            byte[] rawFrame = EncodeHeartbeat(sysId: 1, seq: 42);
            var frameTime = new DateTimeOffset(2026, 11, 17, 10, 15, 30, TimeSpan.Zero).AddMicroseconds(123456);
            ulong expectedMicroseconds = (ulong)((frameTime.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10);

            var snapshot = new TelemetrySnapshot(
                IsConnected: true,
                SystemId: 1,
                ComponentId: 1,
                VehicleType: MavType.FixedWing,
                Autopilot: MavAutopilot.ArduPilotMega,
                VehicleDisplayLabel: "FIXED WING",
                VehicleCategory: VehicleCategory.FixedWing,
                ModeName: "GUIDED",
                IsArmed: true,
                Latitude: 39.9250181,
                Longitude: 32.8369562,
                AltitudeRelativeMeters: 125.5,
                AirspeedMps: 24.75,
                GroundspeedMps: 21.25,
                HeadingDeg: 180.5,
                RollDeg: 12.34,
                PitchDeg: -5.67,
                BatteryVoltageVolts: 22.4,
                BatteryRemainingPercent: 78,
                GpsFixType: GpsFixType.RtkFixed,
                SatellitesVisible: 16,
                UpdatedUtc: frameTime);

            string tlogPath;
            string csvPath;

            CultureInfo originalCulture = CultureInfo.CurrentCulture;
            CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;
            try
            {
                // Force Turkish locale (uses comma decimal separator) to prove InvariantCulture formatting.
                var trCulture = CultureInfo.GetCultureInfo("tr-TR");
                CultureInfo.CurrentCulture = trCulture;
                CultureInfo.CurrentUICulture = trCulture;

                using var recorder = new TelemetryRecorder(tempDir);
                tlogPath = recorder.TlogFilePath;
                csvPath = recorder.CsvFilePath;

                recorder.RecordRawFrame(frameTime, rawFrame);
                recorder.RecordSnapshot(snapshot);
                recorder.Flush();
            }
            finally
            {
                CultureInfo.CurrentCulture = originalCulture;
                CultureInfo.CurrentUICulture = originalUiCulture;
            }

            Assert.True(File.Exists(tlogPath));
            Assert.True(File.Exists(csvPath));

            byte[] tlogBytes = File.ReadAllBytes(tlogPath);
            Assert.Equal(8 + rawFrame.Length, tlogBytes.Length);
            ulong actualMicroseconds = BinaryPrimitives.ReadUInt64BigEndian(tlogBytes.AsSpan(0, 8));
            Assert.Equal(expectedMicroseconds, actualMicroseconds);
            Assert.Equal(rawFrame, tlogBytes.AsSpan(8).ToArray());

            string[] csvLines = File.ReadAllLines(csvPath, Encoding.UTF8);
            Assert.Equal(2, csvLines.Length);
            Assert.Equal(
                "TimestampUtc,Vehicle,Mode,Armed,Lat,Lon,AltRelM,AirspeedMps,GroundspeedMps,HeadingDeg,RollDeg,PitchDeg,BatVolts,BatPct,GpsFix,Sats",
                csvLines[0]);

            string[] cols = csvLines[1].Split(',');
            Assert.Equal(16, cols.Length);
            Assert.Equal(frameTime.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), cols[0]);
            Assert.Equal("FIXED WING", cols[1]);
            Assert.Equal("GUIDED", cols[2]);
            Assert.Equal("true", cols[3]);
            Assert.Equal("39.9250181", cols[4]);
            Assert.Equal("32.8369562", cols[5]);
            Assert.Equal("125.5", cols[6]);
            Assert.Equal("24.75", cols[7]);
            Assert.Equal("21.25", cols[8]);
            Assert.Equal("180.5", cols[9]);
            Assert.Equal("12.34", cols[10]);
            Assert.Equal("-5.67", cols[11]);
            Assert.Equal("22.4", cols[12]);
            Assert.Equal("78", cols[13]);
            Assert.Equal("RtkFixed", cols[14]);
            Assert.Equal("16", cols[15]);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SerialTransport_InvalidPortOrSuddenClose_RaisesDisconnectedWithoutCrash()
    {
        // 1. GetAvailablePorts does not throw
        IReadOnlyList<string> ports = SerialTransport.GetAvailablePorts();
        Assert.NotNull(ports);

        // 2. Real SerialTransport against guaranteed non-existent COM port fails cleanly without crashing
        string missingPort = "COM254";
        while (ports.Contains(missingPort, StringComparer.OrdinalIgnoreCase))
        {
            missingPort += "9";
        }

        await using var invalidSerial = new SerialTransport(missingPort, 115200);
        await Assert.ThrowsAnyAsync<Exception>(() => invalidSerial.ConnectAsync(CancellationToken.None));
        Assert.False(invalidSerial.IsConnected);

        // 3. Sudden close / USB unplug during active ReadAsync raises Disconnected once and shuts down cleanly
        var fakeStream = new FaultablePipeStream();
        var fakeChannel = new FakeSerialChannel(fakeStream);
        await using var serial = new SerialTransport("COM99", 57600, () => fakeChannel);

        var receivedTcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnectedTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        int disconnectCount = 0;

        serial.DataReceived += bytes => receivedTcs.TrySetResult(bytes.ToArray());
        serial.Disconnected += reason =>
        {
            Interlocked.Increment(ref disconnectCount);
            disconnectedTcs.TrySetResult(reason);
        };

        await serial.ConnectAsync(CancellationToken.None);
        Assert.True(serial.IsConnected);

        byte[] incoming = EncodeHeartbeat(sysId: 1, seq: 11);
        fakeStream.EnqueueIncoming(incoming);
        byte[] delivered = await receivedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(incoming, delivered);

        byte[] outgoing = EncodeHeartbeat(sysId: 255, seq: 1);
        await serial.SendAsync(outgoing, CancellationToken.None);
        Assert.Equal(outgoing, fakeStream.GetWrittenBytes());

        // Simulate sudden USB serial unplug via IOException on active ReadAsync
        fakeStream.FailNextRead(new IOException("USB serial device disconnected."));

        string reason = await disconnectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("disconnected", reason, StringComparison.OrdinalIgnoreCase);
        Assert.False(serial.IsConnected);
        Assert.Equal(1, disconnectCount);
        Assert.True(fakeChannel.WasClosed);

        await serial.DisposeAsync();
        await serial.DisposeAsync();
    }

    [Theory]
    [InlineData("unauthorized")]
    [InlineData("invalidop")]
    [InlineData("eof")]
    public async Task SerialTransport_ReadFaultVariants_RaiseDisconnectedOnceAndSupportReentrantDispose(string faultKind)
    {
        var fakeStream = new FaultablePipeStream();
        var fakeChannel = new FakeSerialChannel(fakeStream);
        await using var serial = new SerialTransport("COM98", 921600, () => fakeChannel);

        var disconnectedTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        int disconnectCount = 0;

        serial.Disconnected += async reason =>
        {
            try
            {
                Interlocked.Increment(ref disconnectCount);
                // Calling DisposeAsync from inside Disconnected must never deadlock.
                await serial.DisposeAsync();
                disconnectedTcs.TrySetResult(reason);
            }
            catch (Exception ex)
            {
                disconnectedTcs.TrySetException(ex);
            }
        };

        await serial.ConnectAsync(CancellationToken.None);

        switch (faultKind)
        {
            case "unauthorized":
                fakeStream.FailNextRead(new UnauthorizedAccessException("Access to port denied."));
                break;
            case "invalidop":
                fakeStream.FailNextRead(new InvalidOperationException("Port closed unexpectedly."));
                break;
            case "eof":
                fakeStream.CompleteIncoming();
                break;
        }

        string reason = await disconnectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.False(serial.IsConnected);
        Assert.Equal(1, disconnectCount);
    }

    [Fact]
    public async Task SerialTransport_DisposeWhileReadBlocked_ExitsCleanlyWithoutDisconnectedOrObjectDisposed()
    {
        var fakeStream = new FaultablePipeStream();
        var fakeChannel = new FakeSerialChannel(fakeStream);
        var serial = new SerialTransport("COM97", 57600, () => fakeChannel);

        var disconnected = new List<string>();
        serial.Disconnected += disconnected.Add;

        await serial.ConnectAsync(CancellationToken.None);
        Assert.True(serial.IsConnected);

        await Task.Delay(30);
        await serial.DisposeAsync();
        await serial.DisposeAsync();

        Assert.False(serial.IsConnected);
        Assert.Empty(disconnected);
        Assert.True(fakeChannel.WasDisposed);

        await Assert.ThrowsAsync<ObjectDisposedException>(() => serial.ConnectAsync(CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => serial.SendAsync(new byte[] { 1 }, CancellationToken.None));
    }

    [Fact]
    public async Task Transports_StateValidationAndCsvEscaping_WorkAsExpected()
    {
        await using var udp = new UdpTransport(0);
        await Assert.ThrowsAsync<InvalidOperationException>(() => udp.SendAsync(new byte[] { 1, 2 }, CancellationToken.None));

        await using var tcp = new TcpTransport("127.0.0.1", 65000);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tcp.SendAsync(new byte[] { 1, 2 }, CancellationToken.None));

        // TelemetryRecorder escapes commas/quotes in Vehicle/Mode and falls back on default timestamp
        string tempDir = Path.Combine(Path.GetTempPath(), "tulpar-recorder-csv-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var recorder = new TelemetryRecorder(tempDir))
            {
                var snap = TelemetrySnapshot.Empty with
                {
                    VehicleDisplayLabel = "ROVER, \"MK1\"",
                    ModeName = "AUTO,HOLD",
                    UpdatedUtc = default
                };
                recorder.RecordSnapshot(snap);
                recorder.Flush();

                // Even with empty packet, .tlog remains valid (0 bytes)
                recorder.RecordRawFrame(DateTimeOffset.UtcNow, ReadOnlySpan<byte>.Empty);
                recorder.Flush();
            }

            string[] files = Directory.GetFiles(tempDir);
            string csvFile = Assert.Single(files, f => f.EndsWith(".csv", StringComparison.OrdinalIgnoreCase));
            string tlogFile = Assert.Single(files, f => f.EndsWith(".tlog", StringComparison.OrdinalIgnoreCase));
            Assert.Empty(File.ReadAllBytes(tlogFile));

            string[] lines = File.ReadAllLines(csvFile, Encoding.UTF8);
            Assert.Equal(2, lines.Length);
            Assert.Contains("\"ROVER, \"\"MK1\"\"\"", lines[1], StringComparison.Ordinal);
            Assert.Contains("\"AUTO,HOLD\"", lines[1], StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SerialTransport_WriteFault_RaisesDisconnectedOnceWithoutDuplicate()
    {
        var fakeStream = new FaultablePipeStream();
        var fakeChannel = new FakeSerialChannel(fakeStream);
        await using var serial = new SerialTransport("COM96", 115200, () => fakeChannel);

        var disconnectedTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        int disconnectCount = 0;

        serial.Disconnected += reason =>
        {
            Interlocked.Increment(ref disconnectCount);
            disconnectedTcs.TrySetResult(reason);
        };

        await serial.ConnectAsync(CancellationToken.None);
        fakeStream.FailNextWrite(new IOException("Serial TX write failed."));

        await Assert.ThrowsAsync<IOException>(() => serial.SendAsync(new byte[] { 0xFD, 0x01 }, CancellationToken.None));

        string reason = await disconnectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("TX write failed", reason, StringComparison.Ordinal);
        Assert.False(serial.IsConnected);
        Assert.Equal(1, disconnectCount);
    }

    [Fact]
    public async Task TcpTransport_CancelledConnect_ThrowsOperationCanceledWithoutLeaking()
    {
        await using var tcp = new TcpTransport("127.0.0.1", 65001);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tcp.ConnectAsync(cts.Token));
        Assert.False(tcp.IsConnected);
    }

    [Fact]
    public async Task EndToEnd_UdpTransportSessionRecorderAndForwarder_InteropCleanly()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "tulpar-e2e-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var forwardSink = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            int sinkPort = ((IPEndPoint)forwardSink.Client.LocalEndPoint!).Port;

            using var forwarder = new MavlinkUdpForwarder("127.0.0.1", sinkPort);
            using var recorder = new TelemetryRecorder(tempDir);
            var session = new VehicleSession();

            var snapshotTcs = new TaskCompletionSource<TelemetrySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Bus.SnapshotUpdated += snap =>
            {
                recorder.RecordSnapshot(snap);
                if (snap.IsConnected && snap.Latitude != 0)
                {
                    snapshotTcs.TrySetResult(snap);
                }
            };

            await using var transport = new UdpTransport(0);
            transport.DataReceived += bytes =>
            {
                recorder.RecordRawFrame(DateTimeOffset.UtcNow, bytes.Span);
                forwarder.ForwardRawBytes(bytes.Span);
                session.ProcessIncomingBytes(bytes.Span);
            };

            await transport.ConnectAsync(CancellationToken.None);

            // Auto-learn mode: SendAsync before first incoming packet must be a harmless no-op.
            await transport.SendAsync(EncodeHeartbeat(255, 1), CancellationToken.None);

            using var sim = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            byte[] hb = EncodeHeartbeat(sysId: 1, seq: 1);
            byte[] pos = MavlinkEncoder.EncodeV2(1, 1, 2, new GlobalPositionIntMessage(100u, 399250180, 328369560, 900000, 120000));

            await sim.SendAsync(hb, new IPEndPoint(IPAddress.Loopback, transport.LocalPort));
            await sim.SendAsync(pos, new IPEndPoint(IPAddress.Loopback, transport.LocalPort));

            TelemetrySnapshot finalSnap = await snapshotTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(39.9250180, finalSnap.Latitude, 7);
            Assert.Equal(120.0, finalSnap.AltitudeRelativeMeters, 3);

            UdpReceiveResult fwd1 = await forwardSink.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
            UdpReceiveResult fwd2 = await forwardSink.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(hb, fwd1.Buffer);
            Assert.Equal(pos, fwd2.Buffer);

            await transport.DisposeAsync();
            recorder.Flush();
            recorder.Dispose();

            Assert.Equal(16 + hb.Length + pos.Length, new FileInfo(recorder.TlogFilePath).Length);
            Assert.True(File.ReadAllLines(recorder.CsvFilePath, Encoding.UTF8).Length >= 3);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private sealed class FakeSerialChannel(Stream stream) : ISerialChannel
    {
        public bool IsOpen { get; private set; }
        public bool WasClosed { get; private set; }
        public bool WasDisposed { get; private set; }
        public Stream BaseStream => stream;

        public void Open() => IsOpen = true;

        public void Close()
        {
            IsOpen = false;
            WasClosed = true;
            stream.Dispose();
        }

        public void Dispose()
        {
            WasDisposed = true;
            Close();
        }
    }

    private sealed class FaultablePipeStream : Stream
    {
        private readonly Channel<object> _reads = Channel.CreateUnbounded<object>();
        private readonly MemoryStream _written = new();
        private readonly Lock _writeLock = new();
        private Exception? _nextWriteFault;
        private bool _disposed;

        public void EnqueueIncoming(byte[] data) => _reads.Writer.TryWrite(data);
        public void FailNextRead(Exception ex) => _reads.Writer.TryWrite(ex);
        public void FailNextWrite(Exception ex) => Volatile.Write(ref _nextWriteFault, ex);
        public void CompleteIncoming() => _reads.Writer.TryComplete();

        public byte[] GetWrittenBytes()
        {
            lock (_writeLock)
            {
                return _written.ToArray();
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!await _reads.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return 0;
            }

            while (_reads.Reader.TryRead(out object? item))
            {
                if (item is Exception ex)
                {
                    throw ex;
                }

                if (item is byte[] bytes)
                {
                    int copyLen = Math.Min(bytes.Length, buffer.Length);
                    bytes.AsSpan(0, copyLen).CopyTo(buffer.Span);
                    return copyLen;
                }
            }

            return 0;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            Exception? writeFault = Interlocked.Exchange(ref _nextWriteFault, null);
            if (writeFault is not null)
            {
                throw writeFault;
            }

            lock (_writeLock)
            {
                _written.Write(buffer.Span);
            }
            return ValueTask.CompletedTask;
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                _reads.Writer.TryComplete();
            }

            base.Dispose(disposing);
        }
    }
}
