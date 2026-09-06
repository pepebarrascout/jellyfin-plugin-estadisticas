using System;
using System.Linq;
using Jellyfin.Plugin.Estadisticas.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Estadisticas.Services;

/// <summary>
/// Maintains the historical DB: a lightweight yearly aggregate of the main DB.
///
/// Workflow:
/// - <see cref="RefreshYearlyAggregate"/>: recomputes the aggregate for a given year
///   from the main DB and writes it (idempotent — uses UPSERT).
/// - <see cref="PurgeOldPlays"/>: deletes plays older than 14 months from the main DB.
///   Should be called monthly via a scheduled task.
///
/// The historical DB is intentionally small (no per-play rows; only per-year aggregates).
/// </summary>
public sealed class HistoricalArchiveService
{
    private readonly SqliteDb _db;
    private readonly ILogger<HistoricalArchiveService> _logger;

    public HistoricalArchiveService(SqliteDb db, ILogger<HistoricalArchiveService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Recompute the yearly aggregate for the given year (UTC calendar year).
    /// Idempotent: deletes existing rows for the year first, then re-inserts.
    /// </summary>
    public void RefreshYearlyAggregate(int year)
    {
        var yearStart = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToString("o");
        var yearEnd = new DateTime(year + 1, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToString("o");

        using var mainConn = _db.OpenMain();
        using var histConn = _db.OpenHistorical();
        using var histTx = histConn.BeginTransaction();

        // Clear existing aggregates for this year
        foreach (var table in new[] { "yearly_songs", "yearly_artists", "yearly_genres", "yearly_albums" })
        {
            using var delCmd = histConn.CreateCommand();
            delCmd.Transaction = histTx;
            delCmd.CommandText = $"DELETE FROM {table} WHERE year = @y;";
            delCmd.Parameters.AddWithValue("@y", year);
            delCmd.ExecuteNonQuery();
        }

        // Songs aggregate
        using (var src = mainConn.CreateCommand())
        {
            src.CommandText = @"
                SELECT t.item_id, t.name, t.album_artist, t.album_name,
                       COUNT(p.id) AS plays,
                       COALESCE(SUM(t.duration_ms), 0) AS total_dur
                FROM plays p JOIN tracks t ON t.item_id = p.item_id
                WHERE p.played_at >= @start AND p.played_at < @end
                GROUP BY t.item_id, t.name, t.album_artist, t.album_name;";
            src.Parameters.AddWithValue("@start", yearStart);
            src.Parameters.AddWithValue("@end", yearEnd);
            using var reader = src.ExecuteReader();
            while (reader.Read())
            {
                using var ins = histConn.CreateCommand();
                ins.Transaction = histTx;
                ins.CommandText = @"INSERT INTO yearly_songs
                    (year, item_id, name, album_artist, album_name, play_count, total_duration_ms)
                    VALUES (@y, @id, @n, @aa, @an, @pc, @td);";
                ins.Parameters.AddWithValue("@y", year);
                ins.Parameters.AddWithValue("@id", reader.GetString(0));
                ins.Parameters.AddWithValue("@n", reader.GetString(1));
                ins.Parameters.AddWithValue("@aa", reader.IsDBNull(2) ? (object?)null : reader.GetString(2));
                ins.Parameters.AddWithValue("@an", reader.IsDBNull(3) ? (object?)null : reader.GetString(3));
                ins.Parameters.AddWithValue("@pc", reader.GetInt64(4));
                ins.Parameters.AddWithValue("@td", reader.GetInt64(5));
                ins.ExecuteNonQuery();
            }
        }

        // Artists aggregate
        using (var src = mainConn.CreateCommand())
        {
            src.CommandText = @"
                SELECT ta.artist, COUNT(p.id) AS plays
                FROM plays p JOIN track_artists ta ON ta.item_id = p.item_id
                WHERE p.played_at >= @start AND p.played_at < @end
                GROUP BY ta.artist;";
            src.Parameters.AddWithValue("@start", yearStart);
            src.Parameters.AddWithValue("@end", yearEnd);
            using var reader = src.ExecuteReader();
            while (reader.Read())
            {
                using var ins = histConn.CreateCommand();
                ins.Transaction = histTx;
                ins.CommandText = @"INSERT INTO yearly_artists (year, artist, play_count) VALUES (@y, @a, @pc);";
                ins.Parameters.AddWithValue("@y", year);
                ins.Parameters.AddWithValue("@a", reader.GetString(0));
                ins.Parameters.AddWithValue("@pc", reader.GetInt64(1));
                ins.ExecuteNonQuery();
            }
        }

        // Genres aggregate
        using (var src = mainConn.CreateCommand())
        {
            src.CommandText = @"
                SELECT tg.genre, COUNT(p.id) AS plays
                FROM plays p JOIN track_genres tg ON tg.item_id = p.item_id
                WHERE p.played_at >= @start AND p.played_at < @end
                GROUP BY tg.genre;";
            src.Parameters.AddWithValue("@start", yearStart);
            src.Parameters.AddWithValue("@end", yearEnd);
            using var reader = src.ExecuteReader();
            while (reader.Read())
            {
                using var ins = histConn.CreateCommand();
                ins.Transaction = histTx;
                ins.CommandText = @"INSERT INTO yearly_genres (year, genre, play_count) VALUES (@y, @g, @pc);";
                ins.Parameters.AddWithValue("@y", year);
                ins.Parameters.AddWithValue("@g", reader.GetString(0));
                ins.Parameters.AddWithValue("@pc", reader.GetInt64(1));
                ins.ExecuteNonQuery();
            }
        }

        // Albums aggregate
        using (var src = mainConn.CreateCommand())
        {
            src.CommandText = @"
                SELECT t.album_artist, t.album_name, COUNT(p.id) AS plays
                FROM plays p JOIN tracks t ON t.item_id = p.item_id
                WHERE p.played_at >= @start AND p.played_at < @end
                  AND t.album_name IS NOT NULL
                GROUP BY t.album_artist, t.album_name;";
            src.Parameters.AddWithValue("@start", yearStart);
            src.Parameters.AddWithValue("@end", yearEnd);
            using var reader = src.ExecuteReader();
            while (reader.Read())
            {
                using var ins = histConn.CreateCommand();
                ins.Transaction = histTx;
                ins.CommandText = @"INSERT INTO yearly_albums (year, album_artist, album_name, play_count)
                    VALUES (@y, @aa, @an, @pc);";
                ins.Parameters.AddWithValue("@y", year);
                ins.Parameters.AddWithValue("@aa", reader.IsDBNull(0) ? (object?)null : reader.GetString(0));
                ins.Parameters.AddWithValue("@an", reader.GetString(1));
                ins.Parameters.AddWithValue("@pc", reader.GetInt64(2));
                ins.ExecuteNonQuery();
            }
        }

        histTx.Commit();
        _logger.LogInformation("Refreshed historical aggregate for year {Year}", year);
    }

    /// <summary>
    /// Delete plays older than 14 months from the main DB. Idempotent.
    /// Tracks, artists, and genres metadata are NOT purged (they're small and useful).
    /// Only the `plays` table grows unboundedly.
    /// </summary>
    /// <returns>Number of rows deleted.</returns>
    public long PurgeOldPlays()
    {
        var cutoff = DateTime.UtcNow.AddMonths(-14).ToString("o");
        using var conn = _db.OpenMain();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM plays WHERE played_at < @cutoff;";
        cmd.Parameters.AddWithValue("@cutoff", cutoff);
        var deleted = cmd.ExecuteNonQuery();
        _logger.LogInformation("Purged {Count} plays older than {Cutoff}", deleted, cutoff);
        return deleted;
    }

    /// <summary>
    /// Full monthly maintenance: refresh aggregates for any year that has plays,
    /// then purge plays older than 14 months. Should be called by a scheduled task
    /// that runs once a month.
    /// </summary>
    public void RunMonthlyMaintenance()
    {
        // Find all years present in the plays table
        var years = new System.Collections.Generic.List<int>();
        using (var conn = _db.OpenMain())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT DISTINCT substr(played_at, 1, 4) AS y FROM plays WHERE played_at IS NOT NULL ORDER BY y;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (int.TryParse(reader.GetString(0), out var y)) years.Add(y);
            }
        }

        foreach (var y in years.Distinct().OrderBy(x => x))
        {
            try { RefreshYearlyAggregate(y); }
            catch (Exception ex) { _logger.LogError(ex, "Failed to refresh aggregate for year {Year}", y); }
        }

        PurgeOldPlays();
    }
}
