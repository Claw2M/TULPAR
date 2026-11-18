using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Tulpar.Core.Mavlink;

namespace Tulpar.Core.Session;

/// <summary>
/// Represents a single timestamped MAVLink frame decoded from a <c>.tlog</c> telemetry recording.
/// </summary>
public sealed record TlogEntry(
    DateTimeOffset TimestampUtc,
    MavlinkFrame Frame,
    ReadOnlyMemory<byte> RawPacketBytes);

/// <summary>
/// Optional filter criteria applied while streaming entries from a <c>.tlog</c> file or stream.
/// </summary>
public sealed record TlogReplayFilter(
    byte? SystemId = null,
    byte? ComponentId = null,
    IReadOnlySet<uint>? MessageIds = null,
    DateTimeOffset? StartUtc = null,
    DateTimeOffset? EndUtc = null);

/// <summary>
/// Reads Mission Planner / QGroundControl compatible <c>.tlog</c> files (8-byte big-endian Unix
/// microseconds followed by raw MAVLink v1/v2 packets), applying validation and optional filtering.
/// </summary>
public static class TlogReplayReader
{
    private const byte IncompatFlagSigned = 0x01;
    private const int SignatureLength = 13;

    private static readonly ulong MaxValidMicroseconds =
        (ulong)((DateTimeOffset.MaxValue.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10);

    /// <summary>
    /// Synchronously reads and filters valid MAVLink entries from a <c>.tlog</c> file on disk.
    /// </summary>
    public static IEnumerable<TlogEntry> ReadFile(string filePath, TlogReplayFilter? filter = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 8192);
        foreach (TlogEntry entry in ReadStream(stream, filter))
        {
            yield return entry;
        }
    }

    /// <summary>
    /// Synchronously reads and filters valid MAVLink entries from an open <c>.tlog</c> stream.
    /// </summary>
    public static IEnumerable<TlogEntry> ReadStream(Stream stream, TlogReplayFilter? filter = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var parser = new MavlinkParser();
        byte[] tsBuf = new byte[8];
        byte[] headerBuf = new byte[10];

        while (true)
        {
            if (ReadExact(stream, tsBuf) < 8)
            {
                yield break;
            }

            if (!TryDecodeTimestamp(tsBuf, out DateTimeOffset timestampUtc))
            {
                yield break;
            }

            int stxByte = stream.ReadByte();
            if (stxByte < 0)
            {
                yield break;
            }

            byte stx = (byte)stxByte;
            byte[]? packet;

            if (stx == MavlinkEncoder.MagicV2)
            {
                headerBuf[0] = stx;
                if (ReadExact(stream, headerBuf.AsSpan(1, 9)) < 9)
                {
                    yield break;
                }

                byte payloadLen = headerBuf[1];
                byte incompatFlags = headerBuf[2];
                int sigLen = (incompatFlags & IncompatFlagSigned) != 0 ? SignatureLength : 0;
                int totalLen = 12 + payloadLen + sigLen;
                packet = new byte[totalLen];
                headerBuf.AsSpan(0, 10).CopyTo(packet);

                int remaining = totalLen - 10;
                if (ReadExact(stream, packet.AsSpan(10, remaining)) < remaining)
                {
                    yield break;
                }
            }
            else if (stx == MavlinkEncoder.MagicV1)
            {
                headerBuf[0] = stx;
                if (ReadExact(stream, headerBuf.AsSpan(1, 5)) < 5)
                {
                    yield break;
                }

                byte payloadLen = headerBuf[1];
                int totalLen = 8 + payloadLen;
                packet = new byte[totalLen];
                headerBuf.AsSpan(0, 6).CopyTo(packet);

                int remaining = totalLen - 6;
                if (ReadExact(stream, packet.AsSpan(6, remaining)) < remaining)
                {
                    yield break;
                }
            }
            else
            {
                // Corrupt non-MAVLink byte where packet STX was expected; abort cleanly.
                yield break;
            }

            parser.Reset();
            foreach (MavlinkFrame frame in parser.Feed(packet))
            {
                var entry = new TlogEntry(
                    timestampUtc,
                    frame,
                    frame.RawBytes.IsEmpty ? packet : frame.RawBytes);

                if (MatchesFilter(entry, filter))
                {
                    yield return entry;
                }
            }
        }
    }

    /// <summary>
    /// Asynchronously reads and filters valid MAVLink entries from a <c>.tlog</c> file on disk.
    /// </summary>
    public static async IAsyncEnumerable<TlogEntry> ReadFileAsync(
        string filePath,
        TlogReplayFilter? filter = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 8192,
            useAsync: true);

        await foreach (TlogEntry entry in ReadStreamAsync(stream, filter, ct).ConfigureAwait(false))
        {
            yield return entry;
        }
    }

    /// <summary>
    /// Asynchronously reads and filters valid MAVLink entries from an open <c>.tlog</c> stream.
    /// </summary>
    public static async IAsyncEnumerable<TlogEntry> ReadStreamAsync(
        Stream stream,
        TlogReplayFilter? filter = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var parser = new MavlinkParser();
        byte[] tsBuf = new byte[8];
        byte[] headerBuf = new byte[10];

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (await ReadExactAsync(stream, tsBuf, ct).ConfigureAwait(false) < 8)
            {
                yield break;
            }

            if (!TryDecodeTimestamp(tsBuf, out DateTimeOffset timestampUtc))
            {
                yield break;
            }

            if (await ReadExactAsync(stream, headerBuf.AsMemory(0, 1), ct).ConfigureAwait(false) < 1)
            {
                yield break;
            }

            byte stx = headerBuf[0];
            byte[] packet;

            if (stx == MavlinkEncoder.MagicV2)
            {
                if (await ReadExactAsync(stream, headerBuf.AsMemory(1, 9), ct).ConfigureAwait(false) < 9)
                {
                    yield break;
                }

                byte payloadLen = headerBuf[1];
                byte incompatFlags = headerBuf[2];
                int sigLen = (incompatFlags & IncompatFlagSigned) != 0 ? SignatureLength : 0;
                int totalLen = 12 + payloadLen + sigLen;
                packet = new byte[totalLen];
                headerBuf.AsSpan(0, 10).CopyTo(packet);

                int remaining = totalLen - 10;
                if (await ReadExactAsync(stream, packet.AsMemory(10, remaining), ct).ConfigureAwait(false) < remaining)
                {
                    yield break;
                }
            }
            else if (stx == MavlinkEncoder.MagicV1)
            {
                if (await ReadExactAsync(stream, headerBuf.AsMemory(1, 5), ct).ConfigureAwait(false) < 5)
                {
                    yield break;
                }

                byte payloadLen = headerBuf[1];
                int totalLen = 8 + payloadLen;
                packet = new byte[totalLen];
                headerBuf.AsSpan(0, 6).CopyTo(packet);

                int remaining = totalLen - 6;
                if (await ReadExactAsync(stream, packet.AsMemory(6, remaining), ct).ConfigureAwait(false) < remaining)
                {
                    yield break;
                }
            }
            else
            {
                yield break;
            }

            parser.Reset();
            foreach (MavlinkFrame frame in parser.Feed(packet))
            {
                var entry = new TlogEntry(
                    timestampUtc,
                    frame,
                    frame.RawBytes.IsEmpty ? packet : frame.RawBytes);

                if (MatchesFilter(entry, filter))
                {
                    yield return entry;
                }
            }
        }
    }

    /// <summary>
    /// Replays matching <c>.tlog</c> entries into a <see cref="VehicleSession"/>.
    /// Set <paramref name="speedMultiplier"/> to <c>0</c> (or <see cref="double.PositiveInfinity"/>) for unthrottled fast replay.
    /// </summary>
    public static async Task ReplayToSessionAsync(
        string filePath,
        VehicleSession session,
        double speedMultiplier = 1.0,
        TlogReplayFilter? filter = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(session);

        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 8192,
            useAsync: true);

        await ReplayToSessionAsync(stream, session, speedMultiplier, filter, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Replays matching <c>.tlog</c> entries from a stream into a <see cref="VehicleSession"/>.
    /// Set <paramref name="speedMultiplier"/> to <c>0</c> (or <see cref="double.PositiveInfinity"/>) for unthrottled fast replay.
    /// </summary>
    public static async Task ReplayToSessionAsync(
        Stream stream,
        VehicleSession session,
        double speedMultiplier = 1.0,
        TlogReplayFilter? filter = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(session);
        if (double.IsNaN(speedMultiplier) || speedMultiplier < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(speedMultiplier), "Speed multiplier must be non-negative.");
        }

        DateTimeOffset? prevTimestamp = null;
        bool throttle = speedMultiplier > 0.0 && double.IsFinite(speedMultiplier);

        await foreach (TlogEntry entry in ReadStreamAsync(stream, filter, ct).ConfigureAwait(false))
        {
            if (throttle && prevTimestamp is { } prev && entry.TimestampUtc > prev)
            {
                TimeSpan delta = entry.TimestampUtc - prev;
                long scaledTicks = (long)(delta.Ticks / speedMultiplier);
                if (scaledTicks > 0)
                {
                    await Task.Delay(TimeSpan.FromTicks(scaledTicks), ct).ConfigureAwait(false);
                }
            }

            prevTimestamp = entry.TimestampUtc;
            session.ProcessIncomingBytes(entry.RawPacketBytes.Span);
        }
    }

    private static bool MatchesFilter(TlogEntry entry, TlogReplayFilter? filter)
    {
        if (filter is null)
        {
            return true;
        }

        if (filter.SystemId is { } sysId && entry.Frame.SystemId != sysId)
        {
            return false;
        }

        if (filter.ComponentId is { } compId && entry.Frame.ComponentId != compId)
        {
            return false;
        }

        if (filter.MessageIds is { } messageIds && !messageIds.Contains(entry.Frame.MessageId))
        {
            return false;
        }

        if (filter.StartUtc is { } startUtc && entry.TimestampUtc < startUtc)
        {
            return false;
        }

        if (filter.EndUtc is { } endUtc && entry.TimestampUtc > endUtc)
        {
            return false;
        }

        return true;
    }

    private static bool TryDecodeTimestamp(ReadOnlySpan<byte> bigEndianMicros, out DateTimeOffset timestampUtc)
    {
        ulong microseconds = BinaryPrimitives.ReadUInt64BigEndian(bigEndianMicros);
        if (microseconds > MaxValidMicroseconds)
        {
            timestampUtc = default;
            return false;
        }

        timestampUtc = DateTimeOffset.UnixEpoch.AddTicks((long)(microseconds * 10));
        return true;
    }

    private static int ReadExact(Stream stream, Span<byte> buffer)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = stream.Read(buffer[totalRead..]);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        return totalRead;
    }

    private static async ValueTask<int> ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[totalRead..], ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        return totalRead;
    }
}
