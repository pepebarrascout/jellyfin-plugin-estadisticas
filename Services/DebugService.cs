using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Estadisticas.Data;
using Jellyfin.Plugin.Estadisticas.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Estadisticas.Services;

/// <summary>
/// Provides diagnostic information for the plugin's "Resumen" tab.
/// Shows row counts, server time and per-window play counts so the user can
/// confirm the plugin is actually capturing plays.
/// </summary>
public sealed class DebugService
{
    private readonly SqliteDb _db;
    private readonly ILogger<DebugService> _logger;

    public DebugService(SqliteDb db, ILogger<DebugService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Returns a diagnostic snapshot of the plugin's DB state.
    /// Useful to confirm the plugin is actually capturing plays.
    /// </summary>
    public Dictionary<string, object> GetStatus()
    {
        var result = new Dictionary<string, object>
        {
            ["dbInitialized"] = _db.IsInitialized,
            // Single server wall-clock time (America/Guatemala, UTC-6),
            // serialized with its explicit UTC offset so any client renders it correctly.
            ["serverTime"] = new DateTimeOffset(
                ServerClock.NowLocal().Ticks,
                ServerClock.UtcOffset(DateTime.UtcNow)).ToString("o"),
        };

        if (!_db.IsInitialized)
        {
            result["error"] = "SQLite DB not initialized. Check Jellyfin logs for the original error.";
            return result;
        }

        try
        {
            using var conn = _db.OpenMain();
            result["tracksCount"] = Count(conn, "tracks");
            result["trackArtistsCount"] = Count(conn, "track_artists");
            result["trackGenresCount"] = Count(conn, "track_genres");
            result["playsCount"] = Count(conn, "plays");
            result["scheduledPlaylistsCount"] = Count(conn, "scheduled_playlists");

            // Earliest and latest play
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT MIN(played_at), MAX(played_at) FROM plays;";
                using var r = cmd.ExecuteReader();
                if (r.Read())
                {
                    result["earliestPlay"] = r.IsDBNull(0) ? null : r.GetString(0);
                    result["latestPlay"] = r.IsDBNull(1) ? null : r.GetString(1);
                }
            }

            // Per-window counts — optimized: single query with CASE WHEN to
            // count all windows in one pass (6x faster than 6 separate queries
            // on large tables).
            var windowCounts = new Dictionary<string, long>();
            try
            {
                var windows = new[] { "2w", "1m", "3m", "6m", "12m", "last_year" };
                var ranges = new Dictionary<string, (string start, string end)>();
                foreach (var w in windows)
                {
                    var win = Models.TimeWindow.ParseCode(w);
                    var (start, end) = Models.TimeWindow.GetRange(win);
                    ranges[w] = (start.ToString("o"), end.ToString("o"));
                }

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT
                        SUM(CASE WHEN played_at >= @s2w AND played_at < @e2w THEN 1 ELSE 0 END) AS w2w,
                        SUM(CASE WHEN played_at >= @s1m AND played_at < @e1m THEN 1 ELSE 0 END) AS w1m,
                        SUM(CASE WHEN played_at >= @s3m AND played_at < @e3m THEN 1 ELSE 0 END) AS w3m,
                        SUM(CASE WHEN played_at >= @s6m AND played_at < @e6m THEN 1 ELSE 0 END) AS w6m,
                        SUM(CASE WHEN played_at >= @s12m AND played_at < @e12m THEN 1 ELSE 0 END) AS w12m,
                        SUM(CASE WHEN played_at >= @sly AND played_at < @ely THEN 1 ELSE 0 END) AS wly
                    FROM plays;";
                cmd.Parameters.AddWithValue("@s2w", ranges["2w"].start);
                cmd.Parameters.AddWithValue("@e2w", ranges["2w"].end);
                cmd.Parameters.AddWithValue("@s1m", ranges["1m"].start);
                cmd.Parameters.AddWithValue("@e1m", ranges["1m"].end);
                cmd.Parameters.AddWithValue("@s3m", ranges["3m"].start);
                cmd.Parameters.AddWithValue("@e3m", ranges["3m"].end);
                cmd.Parameters.AddWithValue("@s6m", ranges["6m"].start);
                cmd.Parameters.AddWithValue("@e6m", ranges["6m"].end);
                cmd.Parameters.AddWithValue("@s12m", ranges["12m"].start);
                cmd.Parameters.AddWithValue("@e12m", ranges["12m"].end);
                cmd.Parameters.AddWithValue("@sly", ranges["last_year"].start);
                cmd.Parameters.AddWithValue("@ely", ranges["last_year"].end);

                using var r = cmd.ExecuteReader();
                if (r.Read())
                {
                    windowCounts["2w"] = r.IsDBNull(0) ? 0 : r.GetInt64(0);
                    windowCounts["1m"] = r.IsDBNull(1) ? 0 : r.GetInt64(1);
                    windowCounts["3m"] = r.IsDBNull(2) ? 0 : r.GetInt64(2);
                    windowCounts["6m"] = r.IsDBNull(3) ? 0 : r.GetInt64(3);
                    windowCounts["12m"] = r.IsDBNull(4) ? 0 : r.GetInt64(4);
                    windowCounts["last_year"] = r.IsDBNull(5) ? 0 : r.GetInt64(5);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to compute per-window counts");
                foreach (var w in new[] { "2w", "1m", "3m", "6m", "12m", "last_year" })
                    windowCounts[w] = -1;
            }
            result["playsPerWindow"] = windowCounts;
        }
        catch (Exception ex)
        {
            result["error"] = "Failed to read DB: " + ex.Message;
        }

        return result;
    }

    /// <summary>
    /// Wipes all plays, track_artists, track_genres and tracks from the main DB.
    /// Scheduled playlists are KEPT (the user may have configured real ones).
    /// Returns the number of rows deleted.
    /// </summary>
    public Dictionary<string, object> ClearAllPlays()
    {
        var result = new Dictionary<string, object>();

        if (!_db.IsInitialized)
        {
            result["success"] = false;
            result["error"] = "SQLite DB not initialized.";
            return result;
        }

        try
        {
            using var conn = _db.OpenMain();
            using var tx = conn.BeginTransaction();
            long plays, artists, genres, tracks;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM plays;";
                plays = cmd.ExecuteNonQuery();
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM track_artists;";
                artists = cmd.ExecuteNonQuery();
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM track_genres;";
                genres = cmd.ExecuteNonQuery();
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM tracks;";
                tracks = cmd.ExecuteNonQuery();
            }
            tx.Commit();

            result["success"] = true;
            result["deletedPlays"] = plays;
            result["deletedTrackArtists"] = artists;
            result["deletedTrackGenres"] = genres;
            result["deletedTracks"] = tracks;
            _logger.LogInformation("Cleared all plays and tracks (kept {Sched} scheduled playlists)",
                Count(conn, "scheduled_playlists"));
        }
        catch (Exception ex)
        {
            result["success"] = false;
            result["error"] = ex.Message;
        }

        return result;
    }

    private static long Count(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table};";
        return (long)cmd.ExecuteScalar()!;
    }
}
