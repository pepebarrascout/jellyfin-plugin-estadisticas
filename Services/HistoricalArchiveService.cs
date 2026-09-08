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
        RegenerateAggregates();
        PurgeOldPlays();
    }

    /// <summary>
    /// Regenerate ALL yearly aggregates from the main DB, WITHOUT purging.
    /// This is safe to call anytime (e.g. from the "Regenerar histórico" button).
    /// It reads all plays, groups by year, and writes the aggregates.
    /// </summary>
    public void RegenerateAggregates()
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

        _logger.LogInformation("Regenerating aggregates for {Count} years: {Years}", years.Count, string.Join(", ", years));

        foreach (var y in years.Distinct().OrderBy(x => x))
        {
            try { RefreshYearlyAggregate(y); }
            catch (Exception ex) { _logger.LogError(ex, "Failed to refresh aggregate for year {Year}", y); }
        }
    }

    // ====== Query methods for the "Histórico" tab (v0.0.0.9) ======

    /// <summary>
    /// Returns the list of years available in the historical DB.
    /// </summary>
    public List<int> GetAvailableYears()
    {
        var years = new List<int>();
        using var conn = _db.OpenHistorical();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT year FROM yearly_songs ORDER BY year DESC;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0)) years.Add(reader.GetInt32(0));
        }
        return years;
    }

    /// <summary>
    /// Returns a summary for a specific year: top songs, top artists, top genres,
    /// top albums, and total play count.
    /// </summary>
    public Dictionary<string, object> GetYearSummary(int year)
    {
        var result = new Dictionary<string, object>();
        using var conn = _db.OpenHistorical();

        // Top 25 songs
        var topSongs = new List<Dictionary<string, object>>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT item_id, name, album_artist, album_name, play_count, total_duration_ms FROM yearly_songs WHERE year = @y ORDER BY play_count DESC, name ASC LIMIT 25;";
            cmd.Parameters.AddWithValue("@y", year);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                topSongs.Add(new Dictionary<string, object>
                {
                    ["itemId"] = r.IsDBNull(0) ? "" : r.GetString(0),
                    ["name"] = r.IsDBNull(1) ? "" : r.GetString(1),
                    ["albumArtist"] = r.IsDBNull(2) ? null : r.GetString(2),
                    ["albumName"] = r.IsDBNull(3) ? null : r.GetString(3),
                    ["playCount"] = r.GetInt64(4),
                    ["durationMs"] = r.GetInt64(5)
                });
            }
        }
        result["topSongs"] = topSongs;

        // Top 25 artists
        var topArtists = new List<Dictionary<string, object>>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT artist, play_count FROM yearly_artists WHERE year = @y ORDER BY play_count DESC, artist ASC LIMIT 25;";
            cmd.Parameters.AddWithValue("@y", year);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                topArtists.Add(new Dictionary<string, object>
                {
                    ["name"] = r.GetString(0),
                    ["playCount"] = r.GetInt64(1)
                });
            }
        }
        result["topArtists"] = topArtists;

        // Top 25 genres
        var topGenres = new List<Dictionary<string, object>>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT genre, play_count FROM yearly_genres WHERE year = @y ORDER BY play_count DESC, genre ASC LIMIT 25;";
            cmd.Parameters.AddWithValue("@y", year);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                topGenres.Add(new Dictionary<string, object>
                {
                    ["name"] = r.GetString(0),
                    ["playCount"] = r.GetInt64(1)
                });
            }
        }
        result["topGenres"] = topGenres;

        // Top 25 albums
        var topAlbums = new List<Dictionary<string, object>>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT album_artist, album_name, play_count FROM yearly_albums WHERE year = @y ORDER BY play_count DESC, album_name ASC LIMIT 25;";
            cmd.Parameters.AddWithValue("@y", year);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                topAlbums.Add(new Dictionary<string, object>
                {
                    ["albumArtist"] = r.IsDBNull(0) ? null : r.GetString(0),
                    ["albumName"] = r.GetString(1),
                    ["playCount"] = r.GetInt64(2)
                });
            }
        }
        result["topAlbums"] = topAlbums;

        // Total plays for the year
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COALESCE(SUM(play_count), 0), COALESCE(SUM(total_duration_ms), 0) FROM yearly_songs WHERE year = @y;";
            cmd.Parameters.AddWithValue("@y", year);
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                result["totalPlays"] = r.GetInt64(0);
                result["totalDurationMs"] = r.GetInt64(1);
            }
        }

        return result;
    }

    /// <summary>
    /// Returns a side-by-side comparison of all available years.
    /// For each year: total plays, top genre, top artist, top song, total duration.
    /// </summary>
    public List<Dictionary<string, object>> GetYearComparison()
    {
        var result = new List<Dictionary<string, object>>();
        var years = GetAvailableYears();

        foreach (var year in years)
        {
            var row = new Dictionary<string, object> { ["year"] = year };
            using var conn = _db.OpenHistorical();

            // Total plays
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COALESCE(SUM(play_count), 0) FROM yearly_songs WHERE year = @y;";
                cmd.Parameters.AddWithValue("@y", year);
                row["totalPlays"] = (long)cmd.ExecuteScalar();
            }

            // Top genre
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT genre, play_count FROM yearly_genres WHERE year = @y ORDER BY play_count DESC LIMIT 1;";
                cmd.Parameters.AddWithValue("@y", year);
                using var r = cmd.ExecuteReader();
                if (r.Read())
                {
                    row["topGenre"] = r.GetString(0);
                    row["topGenrePlays"] = r.GetInt64(1);
                }
                else { row["topGenre"] = null; row["topGenrePlays"] = 0L; }
            }

            // Top artist
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT artist, play_count FROM yearly_artists WHERE year = @y ORDER BY play_count DESC LIMIT 1;";
                cmd.Parameters.AddWithValue("@y", year);
                using var r = cmd.ExecuteReader();
                if (r.Read())
                {
                    row["topArtist"] = r.GetString(0);
                    row["topArtistPlays"] = r.GetInt64(1);
                }
                else { row["topArtist"] = null; row["topArtistPlays"] = 0L; }
            }

            // Top song
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name, play_count FROM yearly_songs WHERE year = @y ORDER BY play_count DESC LIMIT 1;";
                cmd.Parameters.AddWithValue("@y", year);
                using var r = cmd.ExecuteReader();
                if (r.Read())
                {
                    row["topSong"] = r.GetString(0);
                    row["topSongPlays"] = r.GetInt64(1);
                }
                else { row["topSong"] = null; row["topSongPlays"] = 0L; }
            }

            // Total duration
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COALESCE(SUM(total_duration_ms), 0) FROM yearly_songs WHERE year = @y;";
                cmd.Parameters.AddWithValue("@y", year);
                row["totalDurationMs"] = (long)cmd.ExecuteScalar();
            }

            result.Add(row);
        }

        return result;
    }
}
