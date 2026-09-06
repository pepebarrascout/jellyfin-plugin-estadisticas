using System;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Estadisticas.Data;

/// <summary>
/// Manages the two SQLite databases used by the plugin:
/// - <c>estadisticas.db</c>: main DB with play events, track metadata, scheduled playlists. Retention 14 months.
/// - <c>estadisticas_historical.db</c>: lightweight yearly aggregates (one row per song/artist/genre per year). Kept indefinitely.
///
/// Both databases live in <c>&lt;JellyfinDataPath&gt;/plugins/estadisticas/</c> and are completely
/// independent from Jellyfin's own database. The plugin NEVER writes to Jellyfin's DB.
/// </summary>
public sealed class SqliteDb : IDisposable
{
    private readonly ILogger<SqliteDb> _logger;
    private readonly string _mainConnStr;
    private readonly string _histConnStr;
    private readonly object _initLock = new();
    private bool _initialized;

    /// <summary>
    /// Builds the connection strings and stores them. Does NOT open a connection yet.
    /// Call <see cref="Initialize"/> once at startup to create schema.
    /// </summary>
    /// <param name="pluginDataPath">Absolute path to the plugin's data folder (e.g. /var/lib/jellyfin/plugins/estadisticas).</param>
    public SqliteDb(string pluginDataPath, ILogger<SqliteDb> logger)
    {
        _logger = logger;
        Directory.CreateDirectory(pluginDataPath);

        var mainPath = Path.Combine(pluginDataPath, "estadisticas.db");
        var histPath = Path.Combine(pluginDataPath, "estadisticas_historical.db");

        // WAL mode gives better concurrency (reads don't block writes).
        _mainConnStr = new SqliteConnectionStringBuilder
        {
            DataSource = mainPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 30
        }.ToString();

        _histConnStr = new SqliteConnectionStringBuilder
        {
            DataSource = histPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 30
        }.ToString();
    }

    /// <summary>Opens a new connection to the main DB. Caller must dispose.</summary>
    public SqliteConnection OpenMain()
    {
        var conn = new SqliteConnection(_mainConnStr);
        conn.Open();
        // Recommended pragmas for our workload.
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA temp_store=MEMORY;";
            cmd.ExecuteNonQuery();
        }
        return conn;
    }

    /// <summary>Opens a new connection to the historical DB. Caller must dispose.</summary>
    public SqliteConnection OpenHistorical()
    {
        var conn = new SqliteConnection(_histConnStr);
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;";
            cmd.ExecuteNonQuery();
        }
        return conn;
    }

    /// <summary>Creates schemas if they don't exist. Idempotent.</summary>
    public void Initialize()
    {
        lock (_initLock)
        {
            if (_initialized) return;
            Schema.CreateMainSchema(this);
            Schema.CreateHistoricalSchema(this);
            _initialized = true;
            _logger.LogInformation("SQLite databases initialized (main + historical)");
        }
    }

    /// <summary>
    /// True if <see cref="Initialize"/> ran successfully. Services and controllers should
    /// check this before issuing queries, so that a failed init degrades gracefully
    /// instead of throwing unhandled exceptions that become HTTP 500s.
    /// </summary>
    public bool IsInitialized => _initialized;

    /// <inheritdoc/>
    public void Dispose()
    {
        // Connections are opened/disposed per call, so nothing to dispose here.
        // SqliteConnection pools internally via Microsoft.Data.Sqlite.
    }
}
