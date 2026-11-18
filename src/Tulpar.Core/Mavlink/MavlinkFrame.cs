namespace Tulpar.Core.Mavlink;

/// <summary>
/// Represents a parsed or outgoing MAVLink v1/v2 protocol frame.
/// </summary>
public sealed record MavlinkFrame(
    byte Version,
    byte Sequence,
    byte SystemId,
    byte ComponentId,
    uint MessageId,
    ReadOnlyMemory<byte> Payload,
    byte IncompatFlags = 0,
    byte CompatFlags = 0,
    ReadOnlyMemory<byte> RawBytes = default)
{
    /// <summary>
    /// Original wire packet bytes (header + payload + CRC + optional signature) when parsed from a stream.
    /// </summary>
    public ReadOnlyMemory<byte> RawPacketBytes => RawBytes;
}
