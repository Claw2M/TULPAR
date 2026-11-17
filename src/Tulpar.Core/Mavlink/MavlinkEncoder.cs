using System.Buffers.Binary;

namespace Tulpar.Core.Mavlink;

/// <summary>
/// Serializes MAVLink v1 and v2 packets with X.25 CRC and MAVLink2 zero-truncation.
/// </summary>
public static class MavlinkEncoder
{
    public const byte MagicV1 = 0xFE;
    public const byte MagicV2 = 0xFD;
    public const int MaxPayloadLength = 255;

    /// <summary>
    /// Encodes a MAVLink v2 packet with trailing zero-truncation (minimum 1 byte if payload is non-empty).
    /// </summary>
    public static byte[] EncodeV2(
        byte sysId,
        byte compId,
        byte seq,
        uint messageId,
        ReadOnlySpan<byte> payload,
        byte crcExtra)
    {
        if (payload.Length > MaxPayloadLength)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), "MAVLink payload length cannot exceed 255 bytes.");
        }

        if (messageId > 0x00FF_FFFF)
        {
            throw new ArgumentOutOfRangeException(nameof(messageId), "MAVLink v2 message ID must fit in 24 bits.");
        }

        // MAVLink 2 zero-truncation strips trailing 0x00 bytes while preserving at least 1 byte for non-empty payloads.
        int trimmedLength = payload.Length;
        while (trimmedLength > 1 && payload[trimmedLength - 1] == 0)
        {
            trimmedLength--;
        }

        byte[] packet = new byte[12 + trimmedLength];
        packet[0] = MagicV2;
        packet[1] = (byte)trimmedLength;
        packet[2] = 0; // incompat_flags
        packet[3] = 0; // compat_flags
        packet[4] = seq;
        packet[5] = sysId;
        packet[6] = compId;
        packet[7] = (byte)(messageId & 0xFF);
        packet[8] = (byte)((messageId >> 8) & 0xFF);
        packet[9] = (byte)((messageId >> 16) & 0xFF);

        if (trimmedLength > 0)
        {
            payload[..trimmedLength].CopyTo(packet.AsSpan(10, trimmedLength));
        }

        ushort crc = MavlinkCrc.Compute(packet.AsSpan(1, 9 + trimmedLength), crcExtra);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(10 + trimmedLength, 2), crc);
        return packet;
    }

    /// <summary>
    /// Encodes a strongly-typed MAVLink message as a MAVLink v2 packet.
    /// </summary>
    public static byte[] EncodeV2(byte sysId, byte compId, byte seq, IMavlinkMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return EncodeV2(sysId, compId, seq, message.MessageId, message.EncodePayload(), message.CrcExtra);
    }

    /// <summary>
    /// Encodes a MAVLink v1 packet (without zero-truncation).
    /// </summary>
    public static byte[] EncodeV1(
        byte sysId,
        byte compId,
        byte seq,
        byte messageId,
        ReadOnlySpan<byte> payload,
        byte crcExtra)
    {
        if (payload.Length > MaxPayloadLength)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), "MAVLink payload length cannot exceed 255 bytes.");
        }

        byte[] packet = new byte[8 + payload.Length];
        packet[0] = MagicV1;
        packet[1] = (byte)payload.Length;
        packet[2] = seq;
        packet[3] = sysId;
        packet[4] = compId;
        packet[5] = messageId;

        if (!payload.IsEmpty)
        {
            payload.CopyTo(packet.AsSpan(6));
        }

        ushort crc = MavlinkCrc.Compute(packet.AsSpan(1, 5 + payload.Length), crcExtra);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6 + payload.Length, 2), crc);
        return packet;
    }
}
