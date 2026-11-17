using System.Net.Sockets;

namespace Tulpar.Core.Transport;

/// <summary>
/// MAVLink TCP client transport with background async receive loop and clean cancellation teardown.
/// </summary>
public sealed class TcpTransport : ITransport
{
    private const int ReadBufferSize = 8192;

    private readonly string _host;
    private readonly int _port;
    private readonly TransportCore _core = new();

    private TcpClient? _client;
    private NetworkStream? _stream;

    public TcpTransport(string host, int port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(port, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);

        _host = host.Trim();
        _port = port;
        EndpointDescription = $"TCP {_host}:{_port}";
    }

    public bool IsConnected => _core.IsConnected;

    public string EndpointDescription { get; }

    public event Action<ReadOnlyMemory<byte>>? DataReceived;

    public event Action<string>? Disconnected;

    public Task ConnectAsync(CancellationToken ct = default)
    {
        return _core.ConnectAsync(
            this,
            openAsync: async token =>
            {
                var client = new TcpClient { NoDelay = true };
                try
                {
                    await client.ConnectAsync(_host, _port, token).ConfigureAwait(false);
                    NetworkStream stream = client.GetStream();
                    Volatile.Write(ref _client, client);
                    Volatile.Write(ref _stream, stream);

                    int disposedFlag = 0;
                    Action closeResource = () =>
                    {
                        if (Interlocked.Exchange(ref disposedFlag, 1) == 0)
                        {
                            Volatile.Write(ref _stream, null);
                            Volatile.Write(ref _client, null);
                            try
                            {
                                stream.Dispose();
                            }
                            catch
                            {
                            }

                            client.Dispose();
                        }
                    };

                    return closeResource;
                }
                catch
                {
                    Volatile.Write(ref _stream, null);
                    Volatile.Write(ref _client, null);
                    client.Dispose();
                    throw;
                }
            },
            receiveBodyAsync: token =>
            {
                NetworkStream? stream = Volatile.Read(ref _stream);
                if (stream is null)
                {
                    return Task.FromResult<string?>("TCP network stream is unavailable.");
                }

                return TransportCore.RunStreamReadLoopAsync(
                    stream,
                    ReadBufferSize,
                    bytes => DataReceived?.Invoke(bytes),
                    token);
            },
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

                NetworkStream? stream = Volatile.Read(ref _stream);
                if (stream is null)
                {
                    throw new InvalidOperationException("TCP transport is not connected.");
                }

                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
            },
            reason => Disconnected?.Invoke(reason),
            ct);
    }

    public ValueTask DisposeAsync() => _core.DisposeAsync();
}
