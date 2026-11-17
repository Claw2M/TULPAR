using Microsoft.Data.Sqlite;

namespace Tulpar.MapHost;

/// <summary>
/// Read-only MBTiles 1.x archive reader translating XYZ slippy-map coordinates into TMS tile rows.
/// </summary>
public sealed class MbtilesReader : IDisposable
{
    private readonly Lock _sync = new();
    private readonly SqliteConnection _connection;
    private int _disposed;

    public MbtilesReader(string mbtilesPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mbtilesPath);

        string fullPath = Path.GetFullPath(mbtilesPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"MBTiles file not found: {fullPath}", fullPath);
        }

        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        };

        _connection = new SqliteConnection(csb.ConnectionString);
        try
        {
            _connection.Open();

            if (!TableOrViewExists(_connection, "tiles"))
            {
                throw new InvalidDataException($"Invalid MBTiles archive '{fullPath}': missing 'tiles' table or view.");
            }

            string? metadataName = null;
            string? metadataFormat = null;
            if (TableOrViewExists(_connection, "metadata"))
            {
                using SqliteCommand metaCmd = _connection.CreateCommand();
                metaCmd.CommandText = "SELECT name, value FROM metadata WHERE name IN ('name', 'format');";
                using SqliteDataReader reader = metaCmd.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.IsDBNull(0) || reader.IsDBNull(1))
                    {
                        continue;
                    }

                    string key = reader.GetString(0);
                    string val = reader.GetString(1);
                    if (key.Equals("name", StringComparison.OrdinalIgnoreCase))
                    {
                        metadataName = val;
                    }
                    else if (key.Equals("format", StringComparison.OrdinalIgnoreCase))
                    {
                        metadataFormat = val;
                    }
                }
            }

            Name = string.IsNullOrWhiteSpace(metadataName)
                ? Path.GetFileNameWithoutExtension(fullPath)
                : metadataName.Trim();

            Format = string.IsNullOrWhiteSpace(metadataFormat)
                ? "png"
                : metadataFormat.Trim().ToLowerInvariant();
        }
        catch
        {
            _connection.Dispose();
            throw;
        }
    }

    public string Name { get; }

    public string Format { get; }

    public byte[]? TryGetTileXyz(int z, int x, int y)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (z is < 0 or > 30)
        {
            return null;
        }

        long gridSize = 1L << z;
        if (x < 0 || x >= gridSize || y < 0 || y >= gridSize)
        {
            return null;
        }

        // MBTiles uses TMS Y-origin (bottom-up); XYZ slippy map uses top-down Y.
        long tmsY = gridSize - 1 - y;

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);

            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT tile_data
                FROM tiles
                WHERE zoom_level = $z AND tile_column = $x AND tile_row = $y
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$z", z);
            cmd.Parameters.AddWithValue("$x", x);
            cmd.Parameters.AddWithValue("$y", tmsY);

            object? result = cmd.ExecuteScalar();
            return result is byte[] bytes && bytes.Length > 0 ? bytes : null;
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
            _connection.Dispose();
        }
    }

    private static bool TableOrViewExists(SqliteConnection conn, string objectName)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT 1
            FROM sqlite_master
            WHERE name = $name AND type IN ('table', 'view')
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$name", objectName);
        return cmd.ExecuteScalar() is not null;
    }
}
