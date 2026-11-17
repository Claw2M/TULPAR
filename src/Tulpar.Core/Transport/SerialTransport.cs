using System.IO.Ports;

namespace Tulpar.Core.Transport;

internal interface ISerialChannel : IDisposable
{
    bool IsOpen { get; }
    Stream BaseStream { get; }
    void Open();
    void Close();
}

/// <summary>
/// MAVLink transport backed by a physical or virtual RS-232/USB serial port.
/// </summary>
public sealed class SerialTransport : ITransport
{
    private const int ReadBufferSize = 8192;

    private readonly string _portName;
    private readonly int _baudRate;
    private readonly Func<ISerialChannel> _channelFactory;
    private readonly TransportCore _core = new();

    private ISerialChannel? _channel;
    private Stream? _stream;

    public SerialTransport(string portName, int baudRate, bool dtrEnable = false, bool rtsEnable = false)
        : this(
            portName,
            baudRate,
            () => new SystemSerialChannel(portName.Trim(), baudRate, dtrEnable, rtsEnable))
    {
    }

    internal SerialTransport(string portName, int baudRate, Func<ISerialChannel> channelFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(baudRate, 0);
        ArgumentNullException.ThrowIfNull(channelFactory);

        _portName = portName.Trim();
        _baudRate = baudRate;
        _channelFactory = channelFactory;
        EndpointDescription = $"SERIAL {_portName} @ {_baudRate}";
    }

    public bool IsConnected => _core.IsConnected;

    public string EndpointDescription { get; }

    public event Action<ReadOnlyMemory<byte>>? DataReceived;

    public event Action<string>? Disconnected;

    public static IReadOnlyList<string> GetAvailablePorts() =>
        SerialPort.GetPortNames()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public Task ConnectAsync(CancellationToken ct = default)
    {
        return _core.ConnectAsync(
            this,
            openAsync: token =>
            {
                token.ThrowIfCancellationRequested();
                ISerialChannel channel = _channelFactory();
                try
                {
                    channel.Open();
                    Stream stream = channel.BaseStream;
                    Volatile.Write(ref _channel, channel);
                    Volatile.Write(ref _stream, stream);

                    Action closeResource = () =>
                    {
                        Volatile.Write(ref _stream, null);
                        ISerialChannel? active = Interlocked.Exchange(ref _channel, null);
                        active?.Dispose();
                    };

                    return Task.FromResult(closeResource);
                }
                catch
                {
                    Volatile.Write(ref _stream, null);
                    Volatile.Write(ref _channel, null);
                    try
                    {
                        channel.Dispose();
                    }
                    catch
                    {
                    }

                    throw;
                }
            },
            receiveBodyAsync: token =>
            {
                Stream? stream = Volatile.Read(ref _stream);
                if (stream is null)
                {
                    return Task.FromResult<string?>("Serial port stream is unavailable.");
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

                Stream? stream = Volatile.Read(ref _stream);
                if (stream is null)
                {
                    throw new InvalidOperationException("Serial transport is not connected.");
                }

                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
            },
            reason => Disconnected?.Invoke(reason),
            ct);
    }

    public ValueTask DisposeAsync() => _core.DisposeAsync();

    private sealed class SystemSerialChannel : ISerialChannel
    {
        private readonly SerialPort _port;
        private int _disposed;

        public SystemSerialChannel(string portName, int baudRate, bool dtrEnable, bool rtsEnable)
        {
            _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
            {
                DtrEnable = dtrEnable,
                RtsEnable = rtsEnable,
                Handshake = Handshake.None,
                ReadTimeout = 500,
                WriteTimeout = 2000,
                ReadBufferSize = 65536,
                WriteBufferSize = 16384
            };
        }

        public bool IsOpen => _port.IsOpen;

        public Stream BaseStream => _port.BaseStream;

        public void Open() => _port.Open();

        public void Close()
        {
            try
            {
                if (_port.IsOpen)
                {
                    _port.Close();
                }
            }
            catch
            {
                // Ignore errors closing an already-unplugged USB serial port.
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Close();
                _port.Dispose();
            }
        }
    }
}
