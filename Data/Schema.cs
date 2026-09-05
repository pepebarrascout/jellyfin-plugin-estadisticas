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
                first_seen TEXT NOT NULL,
                last_updated TEXT NOT NULL
            );");

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
                limit INTEGER NOT NULL DEFAULT 25,
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
}
