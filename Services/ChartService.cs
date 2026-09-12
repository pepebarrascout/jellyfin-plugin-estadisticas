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

            int longest = 1, current = 1;
            for (int i = 1; i < dates.Count; i++)
            {
                if ((dates[i] - dates[i - 1]).Days == 1)
                {
                    current++;
                    if (current > longest) longest = current;
                }
                else
                    current = 1;
            }

            int currentStreak = 1;
            for (int i = dates.Count - 1; i > 0; i--)
            {
                if ((dates[i] - dates[i - 1]).Days == 1)
                    currentStreak++;
                else
                    break;
            }

            var today = ServerClock.NowLocal().Date;
            var lastDate = dates[dates.Count - 1];
            if ((today - lastDate).Days > 1)
                currentStreak = 0;

            result["currentStreak"] = currentStreak;
            result["longestStreak"] = longest;
            result["lastPlayDate"] = lastDate.ToString("yyyy-MM-dd");
            return result;
        });
    }

    /// <summary>
    /// Recap data: top genre, top artist, top song, totals for the current quarter.
    /// </summary>
    public Dictionary<string, object> GetRecap()
    {
        return _cache.GetOrSet("Chart:Recap", () =>
        {
            var result = new Dictionary<string, object>();
            var now = ServerClock.NowLocal();
            var quarterStart = GetQuarterStart(now);
            var quarterStartUtc = ServerClock.ToUtc(quarterStart).ToString("o");

            using var conn = _db.OpenMain();

            // Top genre
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT tg.genre, COUNT(*) AS c FROM plays p
                    JOIN track_genres tg ON tg.item_id = p.item_id
                    WHERE p.played_at >= @s GROUP BY tg.genre ORDER BY c DESC LIMIT 1;";
                cmd.Parameters.AddWithValue("@s", quarterStartUtc);
                using var r = cmd.ExecuteReader();
                result["topGenre"] = r.Read() ? r.GetString(0) : null;
            }

            // Top artist
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT ta.artist, COUNT(*) AS c FROM plays p
                    JOIN track_artists ta ON ta.item_id = p.item_id
                    WHERE p.played_at >= @s GROUP BY ta.artist ORDER BY c DESC LIMIT 1;";
                cmd.Parameters.AddWithValue("@s", quarterStartUtc);
                using var r = cmd.ExecuteReader();
                result["topArtist"] = r.Read() ? r.GetString(0) : null;
            }

            // Top song
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT t.name, COUNT(*) AS c FROM plays p
                    JOIN tracks t ON t.item_id = p.item_id
                    WHERE p.played_at >= @s GROUP BY t.item_id, t.name ORDER BY c DESC LIMIT 1;";
                cmd.Parameters.AddWithValue("@s", quarterStartUtc);
                using var r = cmd.ExecuteReader();
                result["topSong"] = r.Read() ? r.GetString(0) : null;
            }

            // Total plays
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM plays WHERE played_at >= @s;";
                cmd.Parameters.AddWithValue("@s", quarterStartUtc);
                result["totalPlays"] = (long)cmd.ExecuteScalar();
            }

            // Total duration
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT COALESCE(SUM(t.duration_ms), 0) FROM plays p
                    JOIN tracks t ON t.item_id = p.item_id WHERE p.played_at >= @s;";
                cmd.Parameters.AddWithValue("@s", quarterStartUtc);
                result["totalDurationMs"] = (long)cmd.ExecuteScalar();
            }

            // Distinct songs
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(DISTINCT item_id) FROM plays WHERE played_at >= @s;";
                cmd.Parameters.AddWithValue("@s", quarterStartUtc);
                result["distinctSongs"] = (long)cmd.ExecuteScalar();
            }

            return result;
        });
    }

    /// <summary>
    /// Quarter comparison: top 10 genres, current quarter vs previous quarter.
    /// </summary>
    public Dictionary<string, object> GetQuarterCompare()
    {
        return _cache.GetOrSet("Chart:QuarterCompare", () =>
        {
            var result = new Dictionary<string, object>();
            var now = ServerClock.NowLocal();
            var currentStart = GetQuarterStart(now);
            var previousStart = currentStart.AddMonths(-3);
            var currentStartUtc = ServerClock.ToUtc(currentStart).ToString("o");
            var previousStartUtc = ServerClock.ToUtc(previousStart).ToString("o");
            var previousEndUtc = currentStartUtc;

            using var conn = _db.OpenMain();

            // Get top 10 genres across both quarters combined
            var genres = new List<string>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $@"
                    SELECT tg.genre, COUNT(*) AS c FROM plays p
                    JOIN track_genres tg ON tg.item_id = p.item_id
                    WHERE p.played_at >= @ps GROUP BY tg.genre ORDER BY c DESC LIMIT 10;";
                cmd.Parameters.AddWithValue("@ps", previousStartUtc);
                using var r = cmd.ExecuteReader();
                while (r.Read()) genres.Add(r.GetString(0));
            }

            // Current quarter plays per genre
            var current = new List<long>();
            var previous = new List<long>();
            foreach (var g in genres)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT COUNT(*) FROM plays p
                    JOIN track_genres tg ON tg.item_id = p.item_id
                    WHERE p.played_at >= @cs AND tg.genre = @g;";
                cmd.Parameters.AddWithValue("@cs", currentStartUtc);
                cmd.Parameters.AddWithValue("@g", g);
                current.Add((long)cmd.ExecuteScalar());

                cmd.Parameters.Clear();
                cmd.CommandText = @"SELECT COUNT(*) FROM plays p
                    JOIN track_genres tg ON tg.item_id = p.item_id
                    WHERE p.played_at >= @ps AND p.played_at < @pe AND tg.genre = @g;";
                cmd.Parameters.AddWithValue("@ps", previousStartUtc);
                cmd.Parameters.AddWithValue("@pe", previousEndUtc);
                cmd.Parameters.AddWithValue("@g", g);
                previous.Add((long)cmd.ExecuteScalar());
            }

            result["genres"] = genres;
            result["current"] = current;
            result["previous"] = previous;
            return result;
        });
    }

    /// <summary>
    /// Genre evolution: top 5 genres, plays per month, last 12 months.
    /// Returns months[], genres[], and series{genre: [plays per month]}.
    /// </summary>
    public Dictionary<string, object> GetGenreEvolution()
    {
        return _cache.GetOrSet("Chart:GenreEvolution", () =>
        {
            var result = new Dictionary<string, object>();
            var now = ServerClock.NowLocal();
            var months = new List<string>();
            var monthStarts = new List<DateTime>();

            for (int i = 11; i >= 0; i--)
            {
                var mStart = new DateTime(now.Year, now.Month, 1).AddMonths(-i);
                monthStarts.Add(mStart);
                months.Add(mStart.ToString("MMM yyyy"));
            }

            using var conn = _db.OpenMain();

            // Find top 5 genres across the full 12-month range
            var topGenres = new List<string>();
            var rangeStart = ServerClock.ToUtc(monthStarts[0]).ToString("o");
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT tg.genre, COUNT(*) AS c FROM plays p
                    JOIN track_genres tg ON tg.item_id = p.item_id
                    WHERE p.played_at >= @s GROUP BY tg.genre ORDER BY c DESC LIMIT 5;";
                cmd.Parameters.AddWithValue("@s", rangeStart);
                using var r = cmd.ExecuteReader();
                while (r.Read()) topGenres.Add(r.GetString(0));
            }

            // For each genre, get plays per month
            var series = new Dictionary<string, List<long>>();
            foreach (var g in topGenres)
            {
                var counts = new List<long>();
                for (int i = 0; i < 12; i++)
                {
                    var ms = ServerClock.ToUtc(monthStarts[i]).ToString("o");
                    var me = i < 11 ? ServerClock.ToUtc(monthStarts[i + 1]).ToString("o") : DateTime.UtcNow.ToString("o");
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"SELECT COUNT(*) FROM plays p
                        JOIN track_genres tg ON tg.item_id = p.item_id
                        WHERE p.played_at >= @ms AND p.played_at < @me AND tg.genre = @g;";
                    cmd.Parameters.AddWithValue("@ms", ms);
                    cmd.Parameters.AddWithValue("@me", me);
                    cmd.Parameters.AddWithValue("@g", g);
                    counts.Add((long)cmd.ExecuteScalar());
                }
                series[g] = counts;
            }

            result["months"] = months;
            result["genres"] = topGenres;
            result["series"] = series;
            return result;
        });
    }

    /// <summary>
    /// Discoveries: songs first seen (first_seen) in the last N months.
    /// Returns [{itemId, name, albumArtist}].
    /// </summary>
    public List<Dictionary<string, object>> GetDiscoveries(int months)
    {
        var cacheKey = $"Chart:Discoveries:{months}";
        return _cache.GetOrSet(cacheKey, () =>
        {
            var result = new List<Dictionary<string, object>>();
            var cutoff = DateTime.UtcNow.AddMonths(-months).ToString("o");
            using var conn = _db.OpenMain();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT item_id, name, album_artist FROM tracks
                WHERE first_seen >= @c ORDER BY first_seen DESC LIMIT 50;";
            cmd.Parameters.AddWithValue("@c", cutoff);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new Dictionary<string, object>
                {
                    ["itemId"] = reader.GetString(0),
                    ["name"] = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    ["albumArtist"] = reader.IsDBNull(2) ? null : reader.GetString(2)
                });
            }
            return result;
        });
    }

    private static DateTime GetQuarterStart(DateTime now)
    {
        var month = now.Month;
        var quarterMonth = ((month - 1) / 3) * 3 + 1; // 1, 4, 7, 10
        return new DateTime(now.Year, quarterMonth, 1, 0, 0, 0);
    }
}
