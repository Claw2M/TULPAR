using Microsoft.Data.Sqlite;

namespace Tulpar.MapHost;

/// <summary>
/// Thread-safe SQLite tile cache persisting raster map tiles keyed by (providerId, z, x, y).
/// </summary>
public sealed class TileCacheStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposed;

    public TileCacheStore(string sqlitePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sqlitePath);

        bool isMemory = sqlitePath.Equals(":memory:", StringComparison.OrdinalIgnoreCase);
        if (!isMemory)
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(sqlitePath));
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }

        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = sqlitePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        };

        _connection = new SqliteConnection(csb.ConnectionString);
        _connection.Open();

        try
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = isMemory
                ? """
                  PRAGMA busy_timeout = 5000;
                  CREATE TABLE IF NOT EXISTS tiles (
                      provider_id TEXT NOT NULL,
                      z INTEGER NOT NULL,
                      x INTEGER NOT NULL,
                      y INTEGER NOT NULL,
                      tile_data BLOB NOT NULL,
                      updated_utc INTEGER NOT NULL,
                      PRIMARY KEY (provider_id, z, x, y)
                  ) WITHOUT ROWID;
                  """
                : """
                  PRAGMA journal_mode = WAL;
                  PRAGMA synchronous = NORMAL;
                  PRAGMA busy_timeout = 5000;
                  CREATE TABLE IF NOT EXISTS tiles (
                      provider_id TEXT NOT NULL,
                      z INTEGER NOT NULL,
                      x INTEGER NOT NULL,
                      y INTEGER NOT NULL,
                      tile_data BLOB NOT NULL,
                      updated_utc INTEGER NOT NULL,
                      PRIMARY KEY (provider_id, z, x, y)
                  ) WITHOUT ROWID;
                  """;
            cmd.ExecuteNonQuery();
        }
        catch
        {
            _connection.Dispose();
            throw;
        }
    }

    public async Task<byte[]?> TryGetTileAsync(
        string providerId,
        int z,
        int x,
        int y,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            await using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT tile_data
                FROM tiles
                WHERE provider_id = $providerId AND z = $z AND x = $x AND y = $y
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$providerId", providerId);
            cmd.Parameters.AddWithValue("$z", z);
            cmd.Parameters.AddWithValue("$x", x);
            cmd.Parameters.AddWithValue("$y", y);

            object? result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return result is byte[] bytes && bytes.Length > 0 ? bytes : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task PutTileAsync(
        string providerId,
        int z,
        int x,
        int y,
        byte[] data,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length == 0)
        {
            return;
        }

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            await using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO tiles (provider_id, z, x, y, tile_data, updated_utc)
                VALUES ($providerId, $z, $x, $y, $tileData, $updatedUtc)
                ON CONFLICT(provider_id, z, x, y) DO UPDATE SET
                    tile_data = excluded.tile_data,
                    updated_utc = excluded.updated_utc;
                """;
            cmd.Parameters.AddWithValue("$providerId", providerId);
            cmd.Parameters.AddWithValue("$z", z);
            cmd.Parameters.AddWithValue("$x", x);
            cmd.Parameters.AddWithValue("$y", y);
            cmd.Parameters.AddWithValue("$tileData", data);
            cmd.Parameters.AddWithValue("$updatedUtc", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _gate.Wait();
        try
        {
            if (_disposed != 0)
            {
                return;
            }

            Volatile.Write(ref _disposed, 1);
            _connection.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }
}
