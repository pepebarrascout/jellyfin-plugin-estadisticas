using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Estadisticas.Data;
using Jellyfin.Plugin.Estadisticas.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Estadisticas.Services;

/// <summary>
/// Provides data for the "Gráficos" tab. All methods return data shaped
/// for specific chart types (timeline, heatmap, radial, treemap, bubble).
/// Results are cached via QueryCache (1h TTL).
/// </summary>
public sealed class ChartService
{
    private readonly SqliteDb _db;
    private readonly ILogger<ChartService> _logger;
    private readonly QueryCache _cache;

    public ChartService(SqliteDb db, ILogger<ChartService> logger, QueryCache cache)
    {
        _db = db;
        _logger = logger;
        _cache = cache;
    }

    /// <summary>
    /// Plays per day for the given window. Returns [{date: "2026-09-01", count: 42}].
    /// Used by the timeline line chart.
    /// </summary>
    public List<Dictionary<string, object>> GetTimeline(QueryWindow window)
    {
        var cacheKey = $"Chart:Timeline:{window}";
        return _cache.GetOrSet(cacheKey, () =>
        {
            var (start, end) = TimeWindow.GetRange(window);
            var result = new List<Dictionary<string, object>>();
            using var conn = _db.OpenMain();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT substr(played_at, 1, 10) AS day, COUNT(*) AS cnt
                FROM plays
                WHERE played_at >= @start AND played_at < @end
                GROUP BY day ORDER BY day;";
            cmd.Parameters.AddWithValue("@start", start.ToString("o"));
            cmd.Parameters.AddWithValue("@end", end.ToString("o"));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new Dictionary<string, object>
                {
                    ["date"] = reader.GetString(0),
                    ["count"] = reader.GetInt64(1)
                });
            }
            return result;
        });
    }

    /// <summary>
    /// Plays per day for the last N months (for heatmap). Returns same format
    /// as timeline but with a wider range.
    /// </summary>
    public List<Dictionary<string, object>> GetHeatmap(int months = 12)
    {
        var cacheKey = $"Chart:Heatmap:{months}";
        return _cache.GetOrSet(cacheKey, () =>
        {
            var end = DateTime.UtcNow;
            var start = end.AddDays(-months * 30);
            var result = new List<Dictionary<string, object>>();
            using var conn = _db.OpenMain();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT substr(played_at, 1, 10) AS day, COUNT(*) AS cnt
                FROM plays
                WHERE played_at >= @start AND played_at < @end
                GROUP BY day ORDER BY day;";
            cmd.Parameters.AddWithValue("@start", start.ToString("o"));
            cmd.Parameters.AddWithValue("@end", end.ToString("o"));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new Dictionary<string, object>
                {
                    ["date"] = reader.GetString(0),
                    ["count"] = reader.GetInt64(1)
                });
            }
            return result;
        });
    }

    /// <summary>
    /// Plays by hour of day (0-23). Uses server-local time.
    /// Returns [{hour: 0, count: 12}, {hour: 1, count: 5}, ...].
    /// </summary>
    public List<Dictionary<string, object>> GetByHour(QueryWindow window)
    {
        var cacheKey = $"Chart:ByHour:{window}";
        return _cache.GetOrSet(cacheKey, () =>
        {
            var (start, end) = TimeWindow.GetRange(window);
            var result = new List<Dictionary<string, object>>();
            using var conn = _db.OpenMain();
            using var cmd = conn.CreateCommand();
            // Extract hour from played_at (UTC), convert to server-local hour
            // SQLite doesn't have timezone functions, so we do it in C#
            cmd.CommandText = "SELECT played_at FROM plays WHERE played_at >= @start AND played_at < @end;";
            cmd.Parameters.AddWithValue("@start", start.ToString("o"));
            cmd.Parameters.AddWithValue("@end", end.ToString("o"));
            var hourCounts = new long[24];
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var playedAtStr = reader.GetString(0);
                if (DateTime.TryParse(playedAtStr, out var utc))
                {
                    var local = TimeZoneInfo.ConvertTime(utc, ServerClock.Zone);
                    hourCounts[local.Hour]++;
                }
            }
            for (int h = 0; h < 24; h++)
            {
                result.Add(new Dictionary<string, object> { ["hour"] = h, ["count"] = hourCounts[h] });
            }
            return result;
        });
    }

    /// <summary>
    /// Plays by day of week (0=Monday..6=Sunday). Uses server-local time.
    /// </summary>
    public List<Dictionary<string, object>> GetByDayOfWeek(QueryWindow window)
    {
        var cacheKey = $"Chart:ByDayOfWeek:{window}";
        return _cache.GetOrSet(cacheKey, () =>
        {
            var (start, end) = TimeWindow.GetRange(window);
            var result = new List<Dictionary<string, object>>();
            using var conn = _db.OpenMain();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT played_at FROM plays WHERE played_at >= @start AND played_at < @end;";
            cmd.Parameters.AddWithValue("@start", start.ToString("o"));
            cmd.Parameters.AddWithValue("@end", end.ToString("o"));
            var dayCounts = new long[7]; // 0=Mon..6=Sun
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var playedAtStr = reader.GetString(0);
                if (DateTime.TryParse(playedAtStr, out var utc))
                {
                    var local = TimeZoneInfo.ConvertTime(utc, ServerClock.Zone);
                    var dow = ((int)local.DayOfWeek + 6) % 7; // Mon=0..Sun=6
                    dayCounts[dow]++;
                }
            }
            var dayNames = new[] { "Lun", "Mar", "Mié", "Jue", "Vie", "Sáb", "Dom" };
            for (int d = 0; d < 7; d++)
            {
                result.Add(new Dictionary<string, object> { ["day"] = d, ["dayName"] = dayNames[d], ["count"] = dayCounts[d] });
            }
            return result;
        });
    }

    /// <summary>
    /// Top 10 genres by plays + "Others" aggregated. For treemap.
    /// Returns [{name: "Rock", plays: 5000}, ..., {name: "Otros", plays: 3000}].
    /// </summary>
    public List<Dictionary<string, object>> GetGenreTreemap(QueryWindow window)
    {
        var cacheKey = $"Chart:GenreTreemap:{window}";
        return _cache.GetOrSet(cacheKey, () =>
        {
            var (start, end) = TimeWindow.GetRange(window);
            var result = new List<Dictionary<string, object>>();
            using var conn = _db.OpenMain();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT tg.genre, COUNT(p.id) AS plays
                FROM plays p
                JOIN track_genres tg ON tg.item_id = p.item_id
                WHERE p.played_at >= @start AND p.played_at < @end
                GROUP BY tg.genre ORDER BY plays DESC;";
            cmd.Parameters.AddWithValue("@start", start.ToString("o"));
            cmd.Parameters.AddWithValue("@end", end.ToString("o"));
            var allGenres = new List<(string genre, long plays)>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                allGenres.Add((reader.GetString(0), reader.GetInt64(1)));
            }
            // Top 10 + "Otros"
            var top10 = allGenres.Take(10).ToList();
            var othersPlays = allGenres.Skip(10).Sum(g => g.plays);
            foreach (var (genre, plays) in top10)
            {
                result.Add(new Dictionary<string, object> { ["name"] = genre, ["plays"] = plays });
            }
            if (othersPlays > 0)
            {
                result.Add(new Dictionary<string, object> { ["name"] = "Otros", ["plays"] = othersPlays });
            }
            return result;
        });
    }

    /// <summary>
    /// Artist bubbles: total plays (X), distinct songs (Y), total duration (size).
    /// Returns top 30 artists. For bubble chart.
    /// </summary>
    public List<Dictionary<string, object>> GetArtistBubbles(QueryWindow window)
    {
        var cacheKey = $"Chart:ArtistBubbles:{window}";
        return _cache.GetOrSet(cacheKey, () =>
        {
            var (start, end) = TimeWindow.GetRange(window);
            var result = new List<Dictionary<string, object>>();
            using var conn = _db.OpenMain();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT ta.artist,
                       COUNT(DISTINCT p.item_id) AS distinct_songs,
                       COUNT(p.id) AS total_plays,
                       COALESCE(SUM(t.duration_ms), 0) AS total_ms
                FROM plays p
                JOIN track_artists ta ON ta.item_id = p.item_id
                JOIN tracks t ON t.item_id = p.item_id
                WHERE p.played_at >= @start AND p.played_at < @end
                GROUP BY ta.artist
                ORDER BY total_plays DESC LIMIT 30;";
            cmd.Parameters.AddWithValue("@start", start.ToString("o"));
            cmd.Parameters.AddWithValue("@end", end.ToString("o"));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new Dictionary<string, object>
                {
                    ["artist"] = reader.GetString(0),
                    ["distinctSongs"] = reader.GetInt64(1),
                    ["totalPlays"] = reader.GetInt64(2),
                    ["durationMs"] = reader.GetInt64(3)
                });
            }
            return result;
        });
    }

    /// <summary>
    /// Consecutive days streak (at least 1 play per day). Returns current streak
    /// and longest streak. Used by the "Racha" achievement/chart.
    /// </summary>
    public Dictionary<string, object> GetStreak()
    {
        return _cache.GetOrSet("Chart:Streak", () =>
        {
            var result = new Dictionary<string, object>();
            using var conn = _db.OpenMain();
            using var cmd = conn.CreateCommand();
            // Get all distinct play dates (server-local), ordered
            cmd.CommandText = "SELECT DISTINCT substr(played_at, 1, 10) AS day FROM plays ORDER BY day;";
            var dates = new List<DateTime>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (DateTime.TryParse(reader.GetString(0), out var d))
                    dates.Add(d.Date);
            }

            if (dates.Count == 0)
            {
                result["currentStreak"] = 0;
                result["longestStreak"] = 0;
                return result;
            }

            // Calculate longest streak
            int longest = 1, current = 1;
            for (int i = 1; i < dates.Count; i++)
            {
                if ((dates[i] - dates[i - 1]).Days == 1)
                {
                    current++;
                    if (current > longest) longest = current;
                }
                else
                {
                    current = 1;
                }
            }

            // Calculate current streak (from the last date backwards)
            int currentStreak = 1;
            for (int i = dates.Count - 1; i > 0; i--)
            {
                if ((dates[i] - dates[i - 1]).Days == 1)
                    currentStreak++;
                else
                    break;
            }

            // Check if the last date is today or yesterday (otherwise streak is 0)
            var today = ServerClock.NowLocal().Date;
            var lastDate = dates[dates.Count - 1];
            if ((today - lastDate).Days > 1)
            {
                currentStreak = 0; // Streak broken
            }

            result["currentStreak"] = currentStreak;
            result["longestStreak"] = longest;
            result["lastPlayDate"] = lastDate.ToString("yyyy-MM-dd");
            return result;
        });
    }
}
