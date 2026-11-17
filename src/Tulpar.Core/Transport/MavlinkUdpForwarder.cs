using System.Net;
using System.Net.Sockets;

namespace Tulpar.Core.Transport;

/// <summary>
/// Non-blocking UDP relay forwarding raw MAVLink packet bytes to an external listener (e.g., 127.0.0.1:14551).
/// Send errors never propagate so a closed listener cannot disrupt GCS telemetry.
/// </summary>
public sealed class MavlinkUdpForwarder : IDisposable
{
    private readonly Lock _sync = new();
    private readonly IPEndPoint _targetEndPoint;
    private readonly UdpClient _udpClient;

    private bool _isEnabled = true;
    private int _disposed;

    public MavlinkUdpForwarder(string targetHost, int targetPort)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetHost);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(targetPort, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(targetPort, 65535);

        string trimmedHost = targetHost.Trim();
        IPAddress targetAddress = ResolveTargetAddress(trimmedHost);
        _targetEndPoint = new IPEndPoint(targetAddress, targetPort);

        _udpClient = new UdpClient(_targetEndPoint.AddressFamily);
        _udpClient.Client.Blocking = false;
    }

    public bool IsEnabled
    {
        get => Volatile.Read(ref _isEnabled);
        set => Volatile.Write(ref _isEnabled, value);
    }

    public void ForwardRawBytes(ReadOnlySpan<byte> rawFrameBytes)
    {
        if (!IsEnabled || Volatile.Read(ref _disposed) != 0 || rawFrameBytes.IsEmpty)
        {
            return;
        }

        lock (_sync)
        {
            if (!_isEnabled || _disposed != 0)
            {
                return;
            }

            try
            {
                _udpClient.Send(rawFrameBytes, _targetEndPoint);
            }
            catch (SocketException)
            {
                // Ignore socket errors (e.g., closed listener or would-block) so forwarding never crashes Tulpar.
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed != 0)
            {
                return;
            }

            _disposed = 1;
            _udpClient.Dispose();
        }
    }

    private static IPAddress ResolveTargetAddress(string host)
    {
        if (IPAddress.TryParse(host, out IPAddress? parsed))
        {
            return parsed;
        }

        IPAddress[] ipv4 = Dns.GetHostAddresses(host, AddressFamily.InterNetwork);
        if (ipv4.Length > 0)
        {
            return ipv4[0];
        }

        IPAddress[] any = Dns.GetHostAddresses(host);
        if (any.Length > 0)
        {
            return any[0];
        }

        throw new SocketException((int)SocketError.HostNotFound);
    }
}
