using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace TafClient.Service;

/// <summary>
/// SQLite-backed persistent cache of installed maps, one table per mod
/// (table name = mod technical name, e.g. "taesc", "tacc").
///
/// Stored at ~/.taf/maps.db. Survives restarts so the client doesn't need
/// to re-scan every launch — only when the directory changes (handled by
/// MapService's FileSystemWatcher) or when explicitly re-scanned via the
/// `--scan-maps` CLI argument.
/// </summary>
public sealed class MapDatabase
{
    private readonly ILogger<MapDatabase> _log;
    private readonly string _connectionString;

    public static string DbPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".taf", "maps.db");

    public MapDatabase(ILogger<MapDatabase> log)
    {
        _log = log;
        string dir = Path.GetDirectoryName(DbPath)!;
        Directory.CreateDirectory(dir);
        _connectionString = $"Data Source={DbPath}";
        EnsureDatabase();
    }

    private void EnsureDatabase()
    {
        using var conn = Open();
        // Per-mod table created lazily in EnsureModTable; nothing global needed yet.
        _log.LogInformation("[MapDb] Database ready at {Path}", DbPath);
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    /// <summary>Quotes a mod technical name for safe use as a SQL table name.</summary>
    private static string TableName(string modTechnical)
    {
        // Mod technical names are always simple lowercase identifiers (tacc, taesc, ...)
        // but sanitize defensively against anything unexpected.
        var clean = new string(modTechnical.Where(char.IsLetterOrDigit).ToArray());
        return $"maps_{clean}";
    }

    private void EnsureModTable(SqliteConnection conn, string modTechnical)
    {
        string table = TableName(modTechnical);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            CREATE TABLE IF NOT EXISTS "{table}" (
                map_name        TEXT PRIMARY KEY,
                hpi_archive_name TEXT NOT NULL,
                description     TEXT,
                author          TEXT,
                max_players     INTEGER,
                width           INTEGER,
                height          INTEGER,
                sea_level       INTEGER,
                crc             TEXT,
                scanned_at      TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Replaces the entire map list for a mod (used after a full directory re-scan).
    /// </summary>
    public void ReplaceMaps(string modTechnical, IReadOnlyList<MapBean> maps)
    {
        using var conn = Open();
        EnsureModTable(conn, modTechnical);
        string table = TableName(modTechnical);

        using var tx = conn.BeginTransaction();
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = $"""DELETE FROM "{table}";""";
            del.ExecuteNonQuery();
        }

        using (var ins = conn.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = $"""
                INSERT INTO "{table}"
                    (map_name, hpi_archive_name, description, author,
                     max_players, width, height, sea_level, crc, scanned_at)
                VALUES
                    ($name, $archive, $desc, $author, $maxp, $w, $h, $sea, $crc, $ts);
                """;
            var pName    = ins.CreateParameter(); pName.ParameterName    = "$name";
            var pArchive = ins.CreateParameter(); pArchive.ParameterName = "$archive";
            var pDesc    = ins.CreateParameter(); pDesc.ParameterName    = "$desc";
            var pAuthor  = ins.CreateParameter(); pAuthor.ParameterName  = "$author";
            var pMaxp    = ins.CreateParameter(); pMaxp.ParameterName    = "$maxp";
            var pW       = ins.CreateParameter(); pW.ParameterName       = "$w";
            var pH       = ins.CreateParameter(); pH.ParameterName       = "$h";
            var pSea     = ins.CreateParameter(); pSea.ParameterName     = "$sea";
            var pCrc     = ins.CreateParameter(); pCrc.ParameterName     = "$crc";
            var pTs      = ins.CreateParameter(); pTs.ParameterName      = "$ts";
            ins.Parameters.AddRange(new[] { pName, pArchive, pDesc, pAuthor, pMaxp, pW, pH, pSea, pCrc, pTs });

            string now = DateTimeOffset.UtcNow.ToString("O");
            foreach (var m in maps)
            {
                pName.Value    = m.MapName;
                pArchive.Value = m.HpiArchiveName;
                pDesc.Value    = m.Description ?? "";
                pAuthor.Value  = m.Author ?? "";
                pMaxp.Value    = m.MaxPlayers;
                pW.Value       = m.Width;
                pH.Value       = m.Height;
                pSea.Value     = m.SeaLevel;
                pCrc.Value     = m.Crc ?? "";
                pTs.Value      = now;
                ins.ExecuteNonQuery();
            }
        }
        tx.Commit();
        _log.LogInformation("[MapDb] Stored {Count} maps for mod={Mod} in table {Table}",
            maps.Count, modTechnical, table);
    }

    /// <summary>Inserts or updates a single map row (used for incremental updates after a download).</summary>
    public void UpsertMap(string modTechnical, MapBean map)
    {
        using var conn = Open();
        EnsureModTable(conn, modTechnical);
        string table = TableName(modTechnical);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO "{table}"
                (map_name, hpi_archive_name, description, author,
                 max_players, width, height, sea_level, crc, scanned_at)
            VALUES
                ($name, $archive, $desc, $author, $maxp, $w, $h, $sea, $crc, $ts)
            ON CONFLICT(map_name) DO UPDATE SET
                hpi_archive_name = excluded.hpi_archive_name,
                description      = excluded.description,
                author           = excluded.author,
                max_players      = excluded.max_players,
                width            = excluded.width,
                height           = excluded.height,
                sea_level        = excluded.sea_level,
                crc              = excluded.crc,
                scanned_at       = excluded.scanned_at;
            """;
        cmd.Parameters.AddWithValue("$name",    map.MapName);
        cmd.Parameters.AddWithValue("$archive", map.HpiArchiveName);
        cmd.Parameters.AddWithValue("$desc",    map.Description ?? "");
        cmd.Parameters.AddWithValue("$author",  map.Author ?? "");
        cmd.Parameters.AddWithValue("$maxp",    map.MaxPlayers);
        cmd.Parameters.AddWithValue("$w",       map.Width);
        cmd.Parameters.AddWithValue("$h",       map.Height);
        cmd.Parameters.AddWithValue("$sea",     map.SeaLevel);
        cmd.Parameters.AddWithValue("$crc",     map.Crc ?? "");
        cmd.Parameters.AddWithValue("$ts",      DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
        _log.LogInformation("[MapDb] Upserted map={Map} for mod={Mod}", map.MapName, modTechnical);
    }

    /// <summary>Removes a single map row (used when an archive is deleted from disk).</summary>
    public void DeleteMap(string modTechnical, string mapName)
    {
        using var conn = Open();
        EnsureModTable(conn, modTechnical);
        string table = TableName(modTechnical);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""DELETE FROM "{table}" WHERE map_name = $name;""";
        cmd.Parameters.AddWithValue("$name", mapName);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Loads all cached maps for a mod. Returns empty list if the table doesn't exist yet.</summary>
    public List<MapBean> LoadMaps(string modTechnical)
    {
        var results = new List<MapBean>();
        using var conn = Open();
        string table = TableName(modTechnical);

        using (var check = conn.CreateCommand())
        {
            check.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=$t;";
            check.Parameters.AddWithValue("$t", table);
            if (check.ExecuteScalar() is null) return results; // table doesn't exist yet
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT map_name, hpi_archive_name, description, author,
                   max_players, width, height, sea_level, crc
            FROM "{table}";
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new MapBean
            {
                MapName        = reader.GetString(0),
                HpiArchiveName = reader.GetString(1),
                Description    = reader.IsDBNull(2) ? "" : reader.GetString(2),
                Author         = reader.IsDBNull(3) ? "" : reader.GetString(3),
                MaxPlayers     = reader.IsDBNull(4) ? 0  : reader.GetInt32(4),
                Width          = reader.IsDBNull(5) ? 0  : reader.GetInt32(5),
                Height         = reader.IsDBNull(6) ? 0  : reader.GetInt32(6),
                SeaLevel       = reader.IsDBNull(7) ? 0  : reader.GetInt32(7),
                Crc            = reader.IsDBNull(8) ? "" : reader.GetString(8),
                IsInstalled    = true,
                ThumbnailUrl   = TafClient.Service.MapService.GetThumbnailUrl(reader.GetString(0)),
            });
        }
        return results;
    }

    /// <summary>Lists all mod tables present in the database (for diagnostics / CLI output).</summary>
    public List<string> ListModTables()
    {
        var mods = new List<string>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'maps_%';";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            mods.Add(reader.GetString(0).Substring("maps_".Length));
        return mods;
    }
}
