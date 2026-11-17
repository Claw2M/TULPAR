namespace Tulpar.Core.Transport;

/// <summary>
/// Abstraction for bidirectional byte-stream MAVLink transports (Serial, UDP, TCP).
/// </summary>
public interface ITransport : IAsyncDisposable
{
    bool IsConnected { get; }
    string EndpointDescription { get; }
    event Action<ReadOnlyMemory<byte>>? DataReceived;
    event Action<string>? Disconnected;
    Task ConnectAsync(CancellationToken ct = default);
    Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct = default);
}
