using System.Net;
using System.Net.Sockets;

namespace Tulpar.Core.Transport;

/// <summary>
/// MAVLink UDP transport supporting both explicit remote endpoints and MAVProxy/Mission Planner
/// style auto-learning of the remote endpoint from the first received datagram.
/// </summary>
public sealed class UdpTransport : ITransport
{
    private const int SioUdpConnReset = -1744830452;

    private readonly int _listenPort;
    private readonly string? _remoteHost;
    private readonly int _configuredRemotePort;
    private readonly bool _hasConfiguredRemote;
    private readonly TransportCore _core = new();

    private UdpClient? _client;
    private IPEndPoint? _remoteEndPoint;
    private int _localPort;
    private string _endpointDescription;

    public UdpTransport(int listenPort, string? remoteHost = null, int remotePort = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(listenPort, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(listenPort, 65535);

        if (remoteHost is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(remoteHost);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(remotePort, 0);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(remotePort, 65535);
            _remoteHost = remoteHost.Trim();
            _configuredRemotePort = remotePort;
            _hasConfiguredRemote = true;
            _endpointDescription = $"UDP :{_listenPort} -> {_remoteHost}:{_configuredRemotePort}";
        }
        else
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(remotePort, 0);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(remotePort, 65535);
            _hasConfiguredRemote = false;
            _endpointDescription = $"UDP :{_listenPort}";
        }

        _listenPort = listenPort;
    }

    public bool IsConnected => _core.IsConnected;

    public int LocalPort => Volatile.Read(ref _localPort);

    public string EndpointDescription => Volatile.Read(ref _endpointDescription);

    public event Action<ReadOnlyMemory<byte>>? DataReceived;

    public event Action<string>? Disconnected;

    public Task ConnectAsync(CancellationToken ct = default)
    {
        return _core.ConnectAsync(
            this,
            openAsync: async token =>
            {
                IPEndPoint? resolvedRemote = null;
                AddressFamily family = AddressFamily.InterNetwork;

                if (_hasConfiguredRemote && _remoteHost is not null)
                {
                    resolvedRemote = await ResolveRemoteEndPointAsync(_remoteHost, _configuredRemotePort, token)
                        .ConfigureAwait(false);
                    family = resolvedRemote.AddressFamily;
                }

                IPAddress bindAddress = family == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;
                var udpClient = new UdpClient(new IPEndPoint(bindAddress, _listenPort));
                try
                {
                    DisableUdpConnResetIfWindows(udpClient);

                    int boundPort = ((IPEndPoint)udpClient.Client.LocalEndPoint!).Port;
                    Volatile.Write(ref _localPort, boundPort);
                    Volatile.Write(ref _remoteEndPoint, resolvedRemote);
                    Volatile.Write(ref _client, udpClient);
                    UpdateEndpointDescription(boundPort, resolvedRemote);

                    int disposedFlag = 0;
                    Action closeResource = () =>
                    {
                        if (Interlocked.Exchange(ref disposedFlag, 1) == 0)
                        {
                            Volatile.Write(ref _client, null);
                            udpClient.Dispose();
                        }
                    };

                    return closeResource;
                }
                catch
                {
                    Volatile.Write(ref _client, null);
                    udpClient.Dispose();
                    throw;
                }
            },
            receiveBodyAsync: ReceiveLoopAsync,
            raiseDisconnected: reason => Disconnected?.Invoke(reason),
            ct);
    }

    public Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
    {
        return _core.ExecuteSendAsync(
            this,
            async token =>
            {
                if (bytes.IsEmpty)
                {
                    return;
                }

                IPEndPoint? target = Volatile.Read(ref _remoteEndPoint);
                if (target is null)
                {
                    // Auto-learn mode before first peer datagram: drop silently like MAVProxy/Mission Planner.
                    return;
                }

                UdpClient? client = Volatile.Read(ref _client);
                if (client is null)
                {
                    throw new InvalidOperationException("UDP transport is not connected.");
                }

                await client.SendAsync(bytes, target, token).ConfigureAwait(false);
            },
            reason => Disconnected?.Invoke(reason),
            ct);
    }

    public ValueTask DisposeAsync() => _core.DisposeAsync();

    private async Task<string?> ReceiveLoopAsync(CancellationToken ct)
    {
        UdpClient? client = Volatile.Read(ref _client);
        if (client is null)
        {
            return "UDP socket is unavailable.";
        }

        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await client.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset && !ct.IsCancellationRequested)
            {
                // Ignore ICMP Port Unreachable errors on Windows UDP sockets.
                continue;
            }

            if (!_hasConfiguredRemote)
            {
                if (Interlocked.CompareExchange(ref _remoteEndPoint, result.RemoteEndPoint, null) is null)
                {
                    UpdateEndpointDescription(LocalPort, result.RemoteEndPoint);
                }
            }

            DataReceived?.Invoke(result.Buffer);
        }

        return null;
    }

    private void UpdateEndpointDescription(int localPort, IPEndPoint? remote)
    {
        string description = remote is not null
            ? $"UDP :{localPort} -> {remote.Address}:{remote.Port}"
            : $"UDP :{localPort} (auto-remote)";
        Volatile.Write(ref _endpointDescription, description);
    }

    private static async Task<IPEndPoint> ResolveRemoteEndPointAsync(string host, int port, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out IPAddress? parsedIp))
        {
            return new IPEndPoint(parsedIp, port);
        }

        IPAddress[] ipv4 = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct).ConfigureAwait(false);
        if (ipv4.Length > 0)
        {
            return new IPEndPoint(ipv4[0], port);
        }

        IPAddress[] any = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        if (any.Length > 0)
        {
            return new IPEndPoint(any[0], port);
        }

        throw new SocketException((int)SocketError.HostNotFound);
    }

    private static void DisableUdpConnResetIfWindows(UdpClient client)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            client.Client.IOControl((IOControlCode)SioUdpConnReset, [0, 0, 0, 0], null);
        }
        catch
        {
            // Safe best-effort socket option.
        }
    }
}
