using System.Buffers.Binary;

namespace Tulpar.Core.Mavlink;

/// <summary>
/// Stateful streaming MAVLink v1 and v2 frame parser with CRC validation, resync, and incompat_flags handling.
/// </summary>
public sealed class MavlinkParser
{
    private const byte IncompatFlagSigned = 0x01;
    private const int SignatureLength = 13;

    private byte[] _buffer = new byte[4096];
    private int _count;

    /// <summary>
    /// Raised when a frame fails CRC, carries an unknown message ID, or uses unsupported MAVLink2 incompat_flags.
    /// </summary>
    public event Action<string>? FrameWarning;

    /// <summary>
    /// Resets any buffered partial frame bytes.
    /// </summary>
    public void Reset()
    {
        _count = 0;
    }

    /// <summary>
    /// Feeds raw transport bytes into the parser and returns any complete, valid frames found.
    /// </summary>
    public IEnumerable<MavlinkFrame> Feed(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty && _count == 0)
        {
            return [];
        }

        Append(bytes);

        List<MavlinkFrame>? frames = null;
        int pos = 0;

        while (pos < _count)
        {
            byte stx = _buffer[pos];
            if (stx is MavlinkEncoder.MagicV2 or MavlinkEncoder.MagicV1)
            {
                bool parsed = stx == MavlinkEncoder.MagicV2
                    ? TryParseV2Frame(ref pos, out MavlinkFrame? frame, out bool needMoreBytes)
                    : TryParseV1Frame(ref pos, out frame, out needMoreBytes);

                if (!parsed)
                {
                    if (needMoreBytes)
                    {
                        break;
                    }

                    continue;
                }

                if (frame is not null)
                {
                    frames ??= new List<MavlinkFrame>();
                    frames.Add(frame);
                }
            }
            else
            {
                pos++;
            }
        }

        if (pos > 0)
        {
            int remaining = _count - pos;
            if (remaining > 0)
            {
                _buffer.AsSpan(pos, remaining).CopyTo(_buffer);
            }

            _count = remaining;
        }

        return frames is null ? [] : frames;
    }

    private void Append(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        int required = _count + bytes.Length;
        if (required > _buffer.Length)
        {
            int nextCapacity = Math.Max(_buffer.Length * 2, required);
            Array.Resize(ref _buffer, nextCapacity);
        }

        bytes.CopyTo(_buffer.AsSpan(_count));
        _count += bytes.Length;
    }

    private bool TryParseV1Frame(ref int pos, out MavlinkFrame? frame, out bool needMoreBytes)
    {
        frame = null;
        int available = _count - pos;
        if (available < 6)
        {
            needMoreBytes = true;
            return false;
        }

        byte payloadLen = _buffer[pos + 1];
        int frameLen = 8 + payloadLen;
        if (available < frameLen)
        {
            needMoreBytes = true;
            return false;
        }

        needMoreBytes = false;
        byte seq = _buffer[pos + 2];
        byte sysId = _buffer[pos + 3];
        byte compId = _buffer[pos + 4];
        uint msgId = _buffer[pos + 5];

        if (!MavlinkCrc.TryGetCrcExtra(msgId, out byte crcExtra))
        {
            FrameWarning?.Invoke($"Unknown MAVLink v1 message ID {msgId}; resyncing.");
            pos++;
            return false;
        }

        ushort computedCrc = MavlinkCrc.Compute(_buffer.AsSpan(pos + 1, 5 + payloadLen), crcExtra);
        ushort expectedCrc = BinaryPrimitives.ReadUInt16LittleEndian(_buffer.AsSpan(pos + 6 + payloadLen, 2));
        if (computedCrc != expectedCrc)
        {
            FrameWarning?.Invoke(
                $"MAVLink v1 CRC mismatch on msgId {msgId}: expected 0x{expectedCrc:X4}, got 0x{computedCrc:X4}; resyncing.");
            // Advance by 1 byte past STX so false 0xFE noise inside a real stream does not skip valid frames behind it.
            pos++;
            return false;
        }

        byte[] payload = _buffer.AsSpan(pos + 6, payloadLen).ToArray();
        frame = new MavlinkFrame(1, seq, sysId, compId, msgId, payload);
        pos += frameLen;
        return true;
    }

    private bool TryParseV2Frame(ref int pos, out MavlinkFrame? frame, out bool needMoreBytes)
    {
        frame = null;
        int available = _count - pos;
        if (available < 10)
        {
            needMoreBytes = true;
            return false;
        }

        byte payloadLen = _buffer[pos + 1];
        byte incompatFlags = _buffer[pos + 2];
        byte compatFlags = _buffer[pos + 3];
        byte seq = _buffer[pos + 4];
        byte sysId = _buffer[pos + 5];
        byte compId = _buffer[pos + 6];
        uint msgId = (uint)(_buffer[pos + 7] | (_buffer[pos + 8] << 8) | (_buffer[pos + 9] << 16));

        bool isSigned = (incompatFlags & IncompatFlagSigned) != 0;
        int sigLen = isSigned ? SignatureLength : 0;
        int frameLen = 12 + payloadLen + sigLen;

        if (available < frameLen)
        {
            needMoreBytes = true;
            return false;
        }

        needMoreBytes = false;

        if (!MavlinkCrc.TryGetCrcExtra(msgId, out byte crcExtra))
        {
            FrameWarning?.Invoke($"Unknown MAVLink v2 message ID {msgId}; resyncing.");
            pos++;
            return false;
        }

        ushort computedCrc = MavlinkCrc.Compute(_buffer.AsSpan(pos + 1, 9 + payloadLen), crcExtra);
        ushort expectedCrc = BinaryPrimitives.ReadUInt16LittleEndian(_buffer.AsSpan(pos + 10 + payloadLen, 2));
        if (computedCrc != expectedCrc)
        {
            FrameWarning?.Invoke(
                $"MAVLink v2 CRC mismatch on msgId {msgId}: expected 0x{expectedCrc:X4}, got 0x{computedCrc:X4}; resyncing.");
            // Advance by 1 byte past STX so false 0xFD noise does not swallow subsequent valid packets.
            pos++;
            return false;
        }

        if (incompatFlags != 0)
        {
            string reason = isSigned
                ? $"MAVLink v2 signed frame (incompat_flags=0x{incompatFlags:X2}) for msgId {msgId} is unsupported in v1; skipped."
                : $"Unsupported MAVLink v2 incompat_flags 0x{incompatFlags:X2} for msgId {msgId}; skipped.";
            FrameWarning?.Invoke(reason);
            pos += frameLen;
            return true;
        }

        byte[] payload = _buffer.AsSpan(pos + 10, payloadLen).ToArray();
        frame = new MavlinkFrame(2, seq, sysId, compId, msgId, payload, incompatFlags, compatFlags);
        pos += frameLen;
        return true;
    }
}
