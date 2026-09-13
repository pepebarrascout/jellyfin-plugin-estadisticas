using System;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Estadisticas.Data;

/// <summary>
/// Schema definitions and migrations for the two SQLite databases.
/// All CREATE statements use IF NOT EXISTS so they are safe to run on every startup.
/// </summary>
internal static class Schema
{
    /// <summary>Creates the main DB schema (tracks, track_artists, track_genres, plays, scheduled_playlists).</summary>
    public static void CreateMainSchema(SqliteDb db)
    {
        using var conn = db.OpenMain();
        using var tx = conn.BeginTransaction();

        // tracks: one row per known audio item in Jellyfin.
        // The plugin NEVER writes to Jellyfin's DB; this is its own cache of metadata.
        Execute(tx,
            @"CREATE TABLE IF NOT EXISTS tracks (
                item_id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                album_artist TEXT,
                album_name TEXT,
                duration_ms INTEGER,
                file_path TEXT,
                year INTEGER,
                album_item_id TEXT,
                artist_item_id TEXT,
                first_seen TEXT NOT NULL,
                last_updated TEXT NOT NULL
            );");

        // Migration: add 'year' column to tracks if it doesn't exist (for upgrades from v0.0.0.4 or earlier).
        // SQLite doesn't have "IF NOT EXISTS" for ADD COLUMN, so we check the schema first.
        AddColumnIfMissing(tx, "tracks", "year", "INTEGER");
        AddColumnIfMissing(tx, "tracks", "album_item_id", "TEXT");
        AddColumnIfMissing(tx, "tracks", "artist_item_id", "TEXT");

        // Artists are multi-valued per track (e.g. "Artist A feat. Artist B").
        // Each artist is counted separately in Top/Bottom artist queries.
        Execute(tx,
            @"CREATE TABLE IF NOT EXISTS track_artists (
                item_id TEXT NOT NULL,
                artist TEXT NOT NULL,
                PRIMARY KEY (item_id, artist),
                FOREIGN KEY (item_id) REFERENCES tracks(item_id) ON DELETE CASCADE
            );");

        // Genres are multi-valued per track (e.g. "Rock; Metal").
        // Each genre is counted separately in Top/Bottom genre queries.
        Execute(tx,
            @"CREATE TABLE IF NOT EXISTS track_genres (
                item_id TEXT NOT NULL,
                genre TEXT NOT NULL,
                PRIMARY KEY (item_id, genre),
                FOREIGN KEY (item_id) REFERENCES tracks(item_id) ON DELETE CASCADE
            );");

        // plays: one row per actual playback that met the 20-second threshold.
        // This is the central fact table; everything else is derived from it.
        //
        // NOTE: We deliberately do NOT capture position_ms (how many ms the user
        // actually listened). To keep the plugin lightweight on low-power devices
        // (e.g. Raspberry Pi with USB storage), we only store the bare minimum
        // per play: item_id + timestamp + user + client + device.
        //
        // For future "total listening time" queries (e.g. "Género Rock: 38h 20m 15s
        // en los últimos 3 meses"), we approximate using the track's full duration
        // (SUM(tracks.duration_ms) joined to plays). This overestimates real
        // listening time (since users may skip before the end), but avoids the
        // extra column and the extra write per play — which matters on USB storage
        // where each additional byte accelerates wear-leveling.
        Execute(tx,
            @"CREATE TABLE IF NOT EXISTS plays (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                item_id TEXT NOT NULL,
                played_at TEXT NOT NULL,
                user_id TEXT NOT NULL,
                client TEXT,
                device TEXT,
                FOREIGN KEY (item_id) REFERENCES tracks(item_id) ON DELETE CASCADE
            );");

        // Index for time-windowed queries (the hottest path).
        Execute(tx, "CREATE INDEX IF NOT EXISTS idx_plays_played_at ON plays(played_at);");
        Execute(tx, "CREATE INDEX IF NOT EXISTS idx_plays_item_id ON plays(item_id);");
        Execute(tx, "CREATE INDEX IF NOT EXISTS idx_plays_user_id ON plays(user_id);");

        // scheduled_playlists: user-defined recurring playlist publications.
        Execute(tx,
            @"CREATE TABLE IF NOT EXISTS scheduled_playlists (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                jellyfin_playlist_id TEXT,
                query_dimension TEXT NOT NULL,
                query_direction TEXT NOT NULL,
                query_window TEXT NOT NULL,
                genre TEXT,
                year_filter INTEGER,
                playlist_limit INTEGER NOT NULL DEFAULT 50,
                frequency TEXT NOT NULL,
                time_of_day TEXT NOT NULL,
                day_of_week INTEGER,
                day_of_month INTEGER,
                month_and_day TEXT,
                enabled INTEGER NOT NULL DEFAULT 1,
                last_run TEXT,
                next_run TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );");

        Execute(tx, "CREATE INDEX IF NOT EXISTS idx_sched_next_run ON scheduled_playlists(enabled, next_run);");

        // Migration: add 'genre' column to scheduled_playlists (v0.0.0.7) so the user
        // can pick ONE genre (e.g. "Rock") when the dimension is Genres.
        AddColumnIfMissing(tx, "scheduled_playlists", "genre", "TEXT");

        // Migration: add 'year_filter' column to scheduled_playlists (v0.0.0.8) so the
        // user can filter by song release year (e.g. "Top 50 of songs released in 1982").
        AddColumnIfMissing(tx, "scheduled_playlists", "year_filter", "INTEGER");

        tx.Commit();
    }

    /// <summary>Creates the historical DB schema (yearly aggregates, retained indefinitely).</summary>
    public static void CreateHistoricalSchema(SqliteDb db)
    {
        using var conn = db.OpenHistorical();
        using var tx = conn.BeginTransaction();

        // Per-song per-year aggregate (kept lightweight: just counts + total duration).
        Execute(tx,
            @"CREATE TABLE IF NOT EXISTS yearly_songs (
                year INTEGER NOT NULL,
                item_id TEXT NOT NULL,
                name TEXT NOT NULL,
                album_artist TEXT,
                album_name TEXT,
                play_count INTEGER NOT NULL,
                total_duration_ms INTEGER NOT NULL,
                PRIMARY KEY (year, item_id)
            );");

        // Per-artist per-year aggregate. One row per (year, artist_name).
        Execute(tx,
            @"CREATE TABLE IF NOT EXISTS yearly_artists (
                year INTEGER NOT NULL,
                artist TEXT NOT NULL,
                play_count INTEGER NOT NULL,
                PRIMARY KEY (year, artist)
            );");

        // Per-genre per-year aggregate.
        Execute(tx,
            @"CREATE TABLE IF NOT EXISTS yearly_genres (
                year INTEGER NOT NULL,
                genre TEXT NOT NULL,
                play_count INTEGER NOT NULL,
                PRIMARY KEY (year, genre)
            );");

        // Per-album per-year aggregate.
        Execute(tx,
            @"CREATE TABLE IF NOT EXISTS yearly_albums (
                year INTEGER NOT NULL,
                album_artist TEXT NOT NULL,
                album_name TEXT NOT NULL,
                play_count INTEGER NOT NULL,
                PRIMARY KEY (year, album_artist, album_name)
            );");

        tx.Commit();
    }

    private static void Execute(SqliteTransaction tx, string sql)
    {
        using var cmd = tx.Connection!.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Adds a column to a table if it doesn't already exist. Used for schema migrations
    /// when upgrading from older plugin versions. SQLite doesn't support
    /// "ALTER TABLE ... ADD COLUMN IF NOT EXISTS", so we check pragma table_info first.
    /// </summary>
    private static void AddColumnIfMissing(SqliteTransaction tx, string table, string column, string type)
    {
        // Check if column already exists
        using (var checkCmd = tx.Connection!.CreateCommand())
        {
            checkCmd.Transaction = tx;
            checkCmd.CommandText = $"PRAGMA table_info({table});";
            using var reader = checkCmd.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.GetString(reader.GetOrdinal("name"));
                if (string.Equals(name, column, System.StringComparison.OrdinalIgnoreCase))
                {
                    return; // Column already exists, nothing to do
                }
            }
        }

        // Column doesn't exist — add it
        using (var alterCmd = tx.Connection!.CreateCommand())
        {
            alterCmd.Transaction = tx;
            alterCmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type};";
            alterCmd.ExecuteNonQuery();
        }
    }
}
