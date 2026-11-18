using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Tulpar.Core.Session;

/// <summary>
/// Records raw MAVLink packets to Mission Planner/QGC compatible <c>.tlog</c> files and decoded
/// <see cref="TelemetrySnapshot"/> states to UTF-8 <c>.csv</c> logs using <see cref="CultureInfo.InvariantCulture"/>.
/// </summary>
public sealed class TelemetryRecorder : IDisposable
{
    private const string CsvHeader =
        "TimestampUtc,Vehicle,Mode,Armed,Lat,Lon,AltRelM,AirspeedMps,GroundspeedMps,HeadingDeg,RollDeg,PitchDeg,BatVolts,BatPct,GpsFix,Sats";
    private const int AutoFlushWriteThreshold = 50;
    private static readonly TimeSpan AutoFlushTimeThreshold = TimeSpan.FromSeconds(2);

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly Lock _sync = new();
    private readonly TimeProvider _timeProvider;
    private readonly FileStream _tlogStream;
    private readonly StreamWriter _csvWriter;
    private int _writesSinceFlush;
    private long _lastFlushTimestamp;
    private bool _disposed;

    public TelemetryRecorder(string logDirectory)
        : this(logDirectory, TimeProvider.System)
    {
    }

    public TelemetryRecorder(string logDirectory, TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        string fullDirectory = Path.GetFullPath(logDirectory);
        Directory.CreateDirectory(fullDirectory);

        string stem = $"tulpar_{DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture)}_{Guid.NewGuid().ToString("N")[..6]}";
        TlogFilePath = Path.Combine(fullDirectory, $"{stem}.tlog");
        CsvFilePath = Path.Combine(fullDirectory, $"{stem}.csv");

        _tlogStream = new FileStream(TlogFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite, bufferSize: 4096);
        try
        {
            var csvStream = new FileStream(CsvFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite, bufferSize: 4096);
            _csvWriter = new StreamWriter(csvStream, Utf8NoBom)
            {
                NewLine = "\n"
            };
            _csvWriter.WriteLine(CsvHeader);
            _csvWriter.Flush();
            _lastFlushTimestamp = _timeProvider.GetTimestamp();
        }
        catch
        {
            _tlogStream.Dispose();
            throw;
        }
    }

    public string TlogFilePath { get; }

    public string CsvFilePath { get; }

    public void RecordRawFrame(DateTimeOffset utcNow, ReadOnlySpan<byte> rawPacket)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (rawPacket.IsEmpty)
            {
                return;
            }

            long ticksSinceEpoch = utcNow.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
            ulong microseconds = ticksSinceEpoch <= 0 ? 0UL : (ulong)(ticksSinceEpoch / 10);

            Span<byte> timestampHeader = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(timestampHeader, microseconds);

            _tlogStream.Write(timestampHeader);
            _tlogStream.Write(rawPacket);
            MaybeAutoFlush();
        }
    }

    public void RecordSnapshot(TelemetrySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            DateTimeOffset timestamp = snapshot.UpdatedUtc != default
                ? snapshot.UpdatedUtc
                : snapshot.LastHeartbeatUtc != default
                    ? snapshot.LastHeartbeatUtc
                    : DateTimeOffset.UtcNow;

            string timestampStr = timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
            string vehicleStr = EscapeCsv(snapshot.VehicleDisplayLabel);
            string modeStr = EscapeCsv(snapshot.ModeName);
            string armedStr = snapshot.IsArmed ? "true" : "false";

            string row = string.Create(
                CultureInfo.InvariantCulture,
                $"{timestampStr},{vehicleStr},{modeStr},{armedStr},{snapshot.Latitude},{snapshot.Longitude},{snapshot.AltitudeRelativeMeters},{snapshot.AirspeedMps},{snapshot.GroundspeedMps},{snapshot.HeadingDeg},{snapshot.RollDeg},{snapshot.PitchDeg},{snapshot.BatteryVoltageVolts},{snapshot.BatteryRemainingPercent},{snapshot.GpsFixType},{snapshot.SatellitesVisible}");

            _csvWriter.WriteLine(row);
            MaybeAutoFlush();
        }
    }

    public void Flush()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            FlushCore();
        }
    }

    private void MaybeAutoFlush()
    {
        _writesSinceFlush++;
        if (_writesSinceFlush >= AutoFlushWriteThreshold ||
            _timeProvider.GetElapsedTime(_lastFlushTimestamp) >= AutoFlushTimeThreshold)
        {
            FlushCore();
        }
    }

    private void FlushCore()
    {
        _tlogStream.Flush();
        _csvWriter.Flush();
        _writesSinceFlush = 0;
        _lastFlushTimestamp = _timeProvider.GetTimestamp();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _csvWriter.Flush();
            _csvWriter.Dispose();
            _tlogStream.Flush();
            _tlogStream.Dispose();
        }
    }

    private static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value.IndexOfAny([',', '"', '\r', '\n']) < 0)
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}
