using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.Estadisticas.Data;
using Jellyfin.Plugin.Estadisticas.Models;

namespace Jellyfin.Plugin.Estadisticas.Services;

/// <summary>
/// Runs Top 50 and Bottom 50 queries against the plugin's SQLite DB.
/// All windows exclude the current period (current week / month / year).
///
/// v0.0.0.11 changes:
/// - QueryCache: results cached for 1 hour to avoid timeouts on Raspberry Pi
/// - Artista and Álbum are now SEPARATE columns (not combined)
/// - track_artists used as fallback when tracks.album_artist is NULL
/// - Albums GROUP BY (album_name, album_artist) to separate same-named albums
/// </summary>
public sealed class StatisticsService
{
    private readonly SqliteDb _db;
    private readonly ILogger<StatisticsService> _logger;
    private readonly QueryCache _cache;

    public StatisticsService(SqliteDb db, ILogger<StatisticsService> logger, QueryCache cache)
    {
        _db = db;
        _logger = logger;
        _cache = cache;
    }

    /// <summary>
    /// Run a Top/Bottom 50 query. Results are cached for 1 hour.
    /// </summary>
    public List<QueryResultRow> Query(
        QueryDimension dimension,
        QueryDirection direction,
        QueryWindow window,
        int limit = 50,
        int? yearFilter = null)
    {
        var cacheKey = $"Query:{dimension}:{direction}:{window}:{limit}:{yearFilter ?? 0}";
        return _cache.GetOrSet(cacheKey, () => QueryUncached(dimension, direction, window, limit, yearFilter));
    }

    private List<QueryResultRow> QueryUncached(
        QueryDimension dimension,
        QueryDirection direction,
        QueryWindow window,
        int limit,
        int? yearFilter)
    {
        var (start, end) = TimeWindow.GetRange(window);
        var startStr = start.ToString("o");
        var endStr = end.ToString("o");
        var dirClause = direction == QueryDirection.Top ? "DESC" : "ASC";
        var yearClause = yearFilter.HasValue ? " AND t.year = @year" : "";

        using var conn = _db.OpenMain();
        using var cmd = conn.CreateCommand();

        switch (dimension)
        {
            case QueryDimension.Songs:
                cmd.CommandText = direction == QueryDirection.Top
                    ? TopSongsSql(dirClause, yearClause)
                    : BottomSongsSql(dirClause, yearClause);
                break;
            case QueryDimension.Artists:
                cmd.CommandText = direction == QueryDirection.Top
                    ? TopArtistsSql(dirClause, yearClause)
                    : BottomArtistsSql(dirClause, yearClause);
                break;
            case QueryDimension.Albums:
                cmd.CommandText = direction == QueryDirection.Top
                    ? TopAlbumsSql(dirClause, yearClause)
                    : BottomAlbumsSql(dirClause, yearClause);
                break;
            case QueryDimension.Genres:
                cmd.CommandText = direction == QueryDirection.Top
                    ? TopGenresSql(dirClause, yearClause)
                    : BottomGenresSql(dirClause, yearClause);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(dimension));
        }

        cmd.Parameters.AddWithValue("@start", startStr);
        cmd.Parameters.AddWithValue("@end", endStr);
        cmd.Parameters.AddWithValue("@limit", limit);
        if (yearFilter.HasValue) cmd.Parameters.AddWithValue("@year", yearFilter.Value);

        var results = new List<QueryResultRow>();
        using var reader = cmd.ExecuteReader();
        int rank = 1;
        while (reader.Read())
        {
            results.Add(new QueryResultRow
            {
                Rank = rank++,
                ItemId = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                Name = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                Artist = reader.IsDBNull(2) ? null : reader.GetString(2),
                Album = reader.IsDBNull(3) ? null : reader.GetString(3),
                PlayCount = reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
                TotalPlayCount = reader.IsDBNull(5) ? 0 : reader.GetInt64(5)
            });
        }

        _logger.LogDebug(
            "Query: {Dir} {Dim} window={Window} limit={Limit} year={Year} -> {Count} rows",
            direction, dimension, window, limit, yearFilter, results.Count);
        return results;
    }

    /// <summary>
    /// Returns ONLY the Jellyfin ItemIds for the query result.
    /// </summary>
    public List<string> GetItemIdsForPlaylist(
        QueryDimension dimension,
        QueryDirection direction,
        QueryWindow window,
        int limit = 50,
        int? yearFilter = null)
    {
        var cacheKey = $"ItemIds:{dimension}:{direction}:{window}:{limit}:{yearFilter ?? 0}";
        return _cache.GetOrSet(cacheKey, () => GetItemIdsForPlaylistUncached(dimension, direction, window, limit, yearFilter));
    }

    private List<string> GetItemIdsForPlaylistUncached(
        QueryDimension dimension,
        QueryDirection direction,
        QueryWindow window,
        int limit,
        int? yearFilter)
    {
        var (start, end) = TimeWindow.GetRange(window);
        var startStr = start.ToString("o");
        var endStr = end.ToString("o");
        var dirClause = direction == QueryDirection.Top ? "DESC" : "ASC";
        var yearClause = yearFilter.HasValue ? " AND t.year = @year" : "";

        using var conn = _db.OpenMain();
        using var cmd = conn.CreateCommand();

        if (dimension == QueryDimension.Songs)
        {
            cmd.CommandText = direction == QueryDirection.Top
                ? TopSongsSql(dirClause, yearClause)
                : BottomSongsSql(dirClause, yearClause);
            cmd.Parameters.AddWithValue("@start", startStr);
            cmd.Parameters.AddWithValue("@end", endStr);
            cmd.Parameters.AddWithValue("@limit", limit);
            if (yearFilter.HasValue) cmd.Parameters.AddWithValue("@year", yearFilter.Value);

            var ids = new List<string>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (!reader.IsDBNull(0)) ids.Add(reader.GetString(0));
            }
            return ids;
        }

        // For aggregated dimensions: get top entities, then resolve songs.
        var entityNames = new List<(string name, string? extra)>();
        if (dimension == QueryDimension.Artists)
        {
            cmd.CommandText = direction == QueryDirection.Top
                ? TopArtistsSql(dirClause, yearClause)
                : BottomArtistsSql(dirClause, yearClause);
        }
        else if (dimension == QueryDimension.Albums)
        {
            cmd.CommandText = direction == QueryDirection.Top
                ? TopAlbumsSql(dirClause, yearClause)
                : BottomAlbumsSql(dirClause, yearClause);
        }
        else
        {
            cmd.CommandText = direction == QueryDirection.Top
                ? TopGenresSql(dirClause, yearClause)
                : BottomGenresSql(dirClause, yearClause);
        }
        cmd.Parameters.AddWithValue("@start", startStr);
        cmd.Parameters.AddWithValue("@end", endStr);
        cmd.Parameters.AddWithValue("@limit", limit);
        if (yearFilter.HasValue) cmd.Parameters.AddWithValue("@year", yearFilter.Value);

        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                entityNames.Add((reader.IsDBNull(1) ? "" : reader.GetString(1), null));
            }
        }

        var resultIds = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var orderClause = direction == QueryDirection.Top ? "DESC" : "ASC";
        var yearFilterSongClause = yearFilter.HasValue ? " AND t.year = @year" : "";

        foreach (var (entity, _) in entityNames)
        {
            using var songCmd = conn.CreateCommand();
            songCmd.Parameters.AddWithValue("@start", startStr);
            songCmd.Parameters.AddWithValue("@end", endStr);
            songCmd.Parameters.AddWithValue("@entity", entity);
            if (yearFilter.HasValue) songCmd.Parameters.AddWithValue("@year", yearFilter.Value);

            if (dimension == QueryDimension.Artists)
            {
                songCmd.CommandText = $@"
                    SELECT t.item_id
                    FROM track_artists ta
                    JOIN tracks t ON t.item_id = ta.item_id
                    LEFT JOIN plays p ON p.item_id = t.item_id AND p.played_at >= @start AND p.played_at < @end
                    WHERE ta.artist = @entity{yearFilterSongClause}
                    GROUP BY t.item_id
                    ORDER BY COUNT(p.id) {orderClause}, t.name ASC
                    LIMIT 1;";
            }
            else if (dimension == QueryDimension.Albums)
            {
                songCmd.CommandText = $@"
                    SELECT t.item_id
                    FROM tracks t
                    LEFT JOIN plays p ON p.item_id = t.item_id AND p.played_at >= @start AND p.played_at < @end
                    WHERE t.album_name = @entity{yearFilterSongClause}
                    GROUP BY t.item_id
                    ORDER BY COUNT(p.id) {orderClause}, t.name ASC
                    LIMIT 1;";
            }
            else
            {
                songCmd.CommandText = $@"
                    SELECT t.item_id
                    FROM track_genres tg
                    JOIN tracks t ON t.item_id = tg.item_id
                    LEFT JOIN plays p ON p.item_id = t.item_id AND p.played_at >= @start AND p.played_at < @end
                    WHERE tg.genre = @entity{yearFilterSongClause}
                    GROUP BY t.item_id
                    ORDER BY COUNT(p.id) {orderClause}, t.name ASC
                    LIMIT 1;";
            }

            using var songReader = songCmd.ExecuteReader();
            if (songReader.Read() && !songReader.IsDBNull(0))
            {
                var id = songReader.GetString(0);
                if (seen.Add(id)) resultIds.Add(id);
            }
        }

        return resultIds;
    }

    /// <summary>
    /// ItemIds for SCHEDULED playlists.
    /// </summary>
    public List<string> GetScheduledItemIds(
        QueryDimension dimension,
        QueryWindow window,
        int limit,
        bool ascending,
        string? genreFilter,
        int? yearFilter = null)
    {
        var cacheKey = $"SchedIds:{dimension}:{window}:{limit}:{ascending}:{genreFilter}:{yearFilter ?? 0}";
        return _cache.GetOrSet(cacheKey, () => GetScheduledItemIdsUncached(dimension, window, limit, ascending, genreFilter, yearFilter));
    }

    private List<string> GetScheduledItemIdsUncached(
        QueryDimension dimension,
        QueryWindow window,
        int limit,
        bool ascending,
        string? genreFilter,
        int? yearFilter)
    {
        var (start, end) = TimeWindow.GetRange(window);
        var startStr = start.ToString("o");
        var endStr = end.ToString("o");
        var dir = ascending ? "ASC" : "DESC";

        using var conn = _db.OpenMain();
        using var cmd = conn.CreateCommand();
        cmd.Parameters.AddWithValue("@start", startStr);
        cmd.Parameters.AddWithValue("@end", endStr);
        cmd.Parameters.AddWithValue("@limit", limit);

        var hasGenre = dimension == QueryDimension.Genres && !string.IsNullOrWhiteSpace(genreFilter);
        var yearClause = yearFilter.HasValue ? " AND t.year = @year" : "";
        if (yearFilter.HasValue) cmd.Parameters.AddWithValue("@year", yearFilter.Value);

        if (hasGenre)
        {
            cmd.CommandText = $"""
                SELECT t.item_id
                FROM tracks t
                JOIN track_genres tg ON tg.item_id = t.item_id
                LEFT JOIN plays p ON p.item_id = t.item_id AND p.played_at >= @start AND p.played_at < @end
                WHERE tg.genre = @genre{yearClause}
                GROUP BY t.item_id, t.name, t.first_seen
                ORDER BY COUNT(p.id) {dir},
                         (SELECT COUNT(*) FROM plays p2 WHERE p2.item_id = t.item_id) ASC,
                         t.first_seen ASC,
                         t.name ASC
                LIMIT @limit;
                """;
            cmd.Parameters.AddWithValue("@genre", genreFilter!.Trim());
        }
        else
        {
            cmd.CommandText = $"""
                SELECT t.item_id
                FROM tracks t
                LEFT JOIN plays p ON p.item_id = t.item_id AND p.played_at >= @start AND p.played_at < @end
                WHERE 1=1{yearClause}
                GROUP BY t.item_id, t.name, t.first_seen
                ORDER BY COUNT(p.id) {dir},
                         (SELECT COUNT(*) FROM plays p2 WHERE p2.item_id = t.item_id) ASC,
                         t.first_seen ASC,
                         t.name ASC
                LIMIT @limit;
                """;
        }

        var ids = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0)) ids.Add(reader.GetString(0));
        }

        _logger.LogDebug(
            "Scheduled query: dim={Dim} window={Window} limit={Limit} asc={Asc} genre={Genre} year={Year} -> {Count} ids",
            dimension, window, limit, ascending, genreFilter, yearFilter, ids.Count);
        return ids;
    }

    /// <summary>Distinct genres, cached.</summary>
    public List<string> GetAllGenres()
    {
        return _cache.GetOrSet("AllGenres", () =>
        {
            var genres = new List<string>();
            using var conn = _db.OpenMain();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT genre FROM track_genres ORDER BY genre COLLATE NOCASE;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (!reader.IsDBNull(0)) genres.Add(reader.GetString(0));
            }
            return genres;
        });
    }

    /// <summary>Distinct years, cached.</summary>
    public List<int> GetAllYears()
    {
        return _cache.GetOrSet("AllYears", () =>
        {
            var years = new List<int>();
            using var conn = _db.OpenMain();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT year FROM tracks WHERE year IS NOT NULL ORDER BY year DESC;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (!reader.IsDBNull(0)) years.Add(reader.GetInt32(0));
            }
            return years;
        });
    }

    /// <summary>
    /// Total listening time (ms) by genre and time window. Cached.
    ///
    /// The "total" column is the ALL-TIME total (no window filter), not the
    /// sum of the 6 windows (which overlap).
    /// </summary>
    public Dictionary<string, Dictionary<string, long>> GetListeningTimeByGenrePerWindow()
    {
        return _cache.GetOrSet("ListeningTime", () =>
        {
            var result = new Dictionary<string, Dictionary<string, long>>(StringComparer.OrdinalIgnoreCase);
            var windows = new[] { QueryWindow.TwoWeeks, QueryWindow.OneMonth, QueryWindow.ThreeMonths, QueryWindow.SixMonths, QueryWindow.TwelveMonths, QueryWindow.LastYear };

            using var conn = _db.OpenMain();

            // Per-window totals
            foreach (var w in windows)
            {
                var (start, end) = TimeWindow.GetRange(w);
                var startStr = start.ToString("o");
                var endStr = end.ToString("o");
                var code = TimeWindow.Code(w);

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT tg.genre, COALESCE(SUM(t.duration_ms), 0) AS total_ms
                    FROM plays p
                    JOIN tracks t ON t.item_id = p.item_id
                    JOIN track_genres tg ON tg.item_id = t.item_id
                    WHERE p.played_at >= @start AND p.played_at < @end
                      AND t.duration_ms IS NOT NULL
                    GROUP BY tg.genre;";
                cmd.Parameters.AddWithValue("@start", startStr);
                cmd.Parameters.AddWithValue("@end", endStr);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var genre = reader.GetString(0);
                    var totalMs = reader.GetInt64(1);
                    if (!result.TryGetValue(genre, out var row))
                    {
                        row = new Dictionary<string, long>();
                        result[genre] = row;
                    }
                    row[code] = totalMs;
                }
            }

            // All-time total (no window filter) — this is the correct "Total" column
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT tg.genre, COALESCE(SUM(t.duration_ms), 0) AS total_ms
                    FROM plays p
                    JOIN tracks t ON t.item_id = p.item_id
                    JOIN track_genres tg ON tg.item_id = t.item_id
                    WHERE t.duration_ms IS NOT NULL
                    GROUP BY tg.genre;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var genre = reader.GetString(0);
                    var totalMs = reader.GetInt64(1);
                    if (!result.TryGetValue(genre, out var row))
                    {
                        row = new Dictionary<string, long>();
                        result[genre] = row;
                    }
                    row["total"] = totalMs;
                }
            }

            return result;
        });
    }

    // ---- SQL templates ----
    // Columns: item_id, name, artist, album, period_plays, total_plays
    // artist: uses COALESCE(t.album_artist, (SELECT artist FROM track_artists WHERE item_id = t.item_id LIMIT 1))
    //         to fallback to track_artists when album_artist is NULL
    // album: separate column (not combined with artist)

    private static string TopSongsSql(string dir, string yearClause) => $@"
        SELECT t.item_id,
               t.name,
               COALESCE(t.album_artist, (SELECT ta.artist FROM track_artists ta WHERE ta.item_id = t.item_id LIMIT 1)) AS artist,
               t.album_name AS album,
               COUNT(p.id) AS period_plays,
               (SELECT COUNT(*) FROM plays p2 WHERE p2.item_id = t.item_id) AS total_plays
        FROM plays p
        JOIN tracks t ON t.item_id = p.item_id
        WHERE p.played_at >= @start AND p.played_at < @end{yearClause}
        GROUP BY t.item_id, t.name, t.album_artist, t.album_name
        ORDER BY period_plays {dir}, t.name ASC
        LIMIT @limit;";

    private static string BottomSongsSql(string dir, string yearClause) => $@"
        SELECT t.item_id,
               t.name,
               COALESCE(t.album_artist, (SELECT ta.artist FROM track_artists ta WHERE ta.item_id = t.item_id LIMIT 1)) AS artist,
               t.album_name AS album,
               COUNT(p.id) AS period_plays,
               (SELECT COUNT(*) FROM plays p2 WHERE p2.item_id = t.item_id) AS total_plays
        FROM tracks t
        LEFT JOIN plays p ON p.item_id = t.item_id AND p.played_at >= @start AND p.played_at < @end
        WHERE 1=1{yearClause}
        GROUP BY t.item_id, t.name, t.album_artist, t.album_name
        ORDER BY period_plays {dir}, total_plays ASC, t.first_seen ASC, t.name ASC
        LIMIT @limit;";

    private static string TopArtistsSql(string dir, string yearClause) => $@"
        SELECT '' AS item_id,
               ta.artist AS name,
               NULL AS artist,
               NULL AS album,
               COUNT(p.id) AS period_plays,
               (SELECT COUNT(*) FROM plays p2 JOIN track_artists ta2 ON ta2.item_id = p2.item_id WHERE ta2.artist = ta.artist) AS total_plays
        FROM plays p
        JOIN track_artists ta ON ta.item_id = p.item_id
        JOIN tracks t ON t.item_id = ta.item_id
        WHERE p.played_at >= @start AND p.played_at < @end{yearClause}
        GROUP BY ta.artist
        ORDER BY period_plays {dir}, ta.artist ASC
        LIMIT @limit;";

    private static string BottomArtistsSql(string dir, string yearClause) => $@"
        SELECT '' AS item_id,
               ta.artist AS name,
               NULL AS artist,
               NULL AS album,
               COUNT(p.id) AS period_plays,
               (SELECT COUNT(*) FROM plays p2 JOIN track_artists ta2 ON ta2.item_id = p2.item_id WHERE ta2.artist = ta.artist) AS total_plays
        FROM track_artists ta
        JOIN tracks t ON t.item_id = ta.item_id
        LEFT JOIN plays p ON p.item_id = ta.item_id AND p.played_at >= @start AND p.played_at < @end
        WHERE 1=1{yearClause}
        GROUP BY ta.artist
        ORDER BY period_plays {dir}, total_plays ASC, MIN(t.first_seen) ASC, ta.artist ASC
        LIMIT @limit;";

    // Albums: GROUP BY (album_name, album_artist) so same-named albums from
    // different artists are separate rows. artist = album_artist (or fallback).
    private static string TopAlbumsSql(string dir, string yearClause) => $@"
        SELECT '' AS item_id,
               COALESCE(t.album_name, '(Sin álbum)') AS name,
               COALESCE(t.album_artist, (SELECT ta.artist FROM track_artists ta WHERE ta.item_id = t.item_id LIMIT 1)) AS artist,
               t.album_name AS album,
               COUNT(p.id) AS period_plays,
               (SELECT COUNT(*) FROM plays p2 JOIN tracks t2 ON t2.item_id = p2.item_id
                WHERE t2.album_name IS t.album_name
                  AND COALESCE(t2.album_artist, (SELECT ta3.artist FROM track_artists ta3 WHERE ta3.item_id = t2.item_id LIMIT 1))
                    IS COALESCE(t.album_artist, (SELECT ta4.artist FROM track_artists ta4 WHERE ta4.item_id = t.item_id LIMIT 1))) AS total_plays
        FROM plays p
        JOIN tracks t ON t.item_id = p.item_id
        WHERE p.played_at >= @start AND p.played_at < @end
          AND t.album_name IS NOT NULL{yearClause}
        GROUP BY t.album_name,
                 COALESCE(t.album_artist, (SELECT ta.artist FROM track_artists ta WHERE ta.item_id = t.item_id LIMIT 1))
        ORDER BY period_plays {dir}, t.album_name ASC
        LIMIT @limit;";

    private static string BottomAlbumsSql(string dir, string yearClause) => $@"
        SELECT '' AS item_id,
               COALESCE(t.album_name, '(Sin álbum)') AS name,
               COALESCE(t.album_artist, (SELECT ta.artist FROM track_artists ta WHERE ta.item_id = t.item_id LIMIT 1)) AS artist,
               t.album_name AS album,
               COUNT(p.id) AS period_plays,
               (SELECT COUNT(*) FROM plays p2 JOIN tracks t2 ON t2.item_id = p2.item_id
                WHERE t2.album_name IS t.album_name
                  AND COALESCE(t2.album_artist, (SELECT ta3.artist FROM track_artists ta3 WHERE ta3.item_id = t2.item_id LIMIT 1))
                    IS COALESCE(t.album_artist, (SELECT ta4.artist FROM track_artists ta4 WHERE ta4.item_id = t.item_id LIMIT 1))) AS total_plays
        FROM tracks t
        LEFT JOIN plays p ON p.item_id = t.item_id AND p.played_at >= @start AND p.played_at < @end
        WHERE t.album_name IS NOT NULL{yearClause}
        GROUP BY t.album_name,
                 COALESCE(t.album_artist, (SELECT ta.artist FROM track_artists ta WHERE ta.item_id = t.item_id LIMIT 1))
        ORDER BY period_plays {dir}, total_plays ASC, MIN(t.first_seen) ASC, t.album_name ASC
        LIMIT @limit;";

    private static string TopGenresSql(string dir, string yearClause) => $@"
        SELECT '' AS item_id,
               tg.genre AS name,
               NULL AS artist,
               NULL AS album,
               COUNT(p.id) AS period_plays,
               (SELECT COUNT(*) FROM plays p2 JOIN track_genres tg2 ON tg2.item_id = p2.item_id WHERE tg2.genre = tg.genre) AS total_plays
        FROM plays p
        JOIN track_genres tg ON tg.item_id = p.item_id
        JOIN tracks t ON t.item_id = tg.item_id
        WHERE p.played_at >= @start AND p.played_at < @end{yearClause}
        GROUP BY tg.genre
        ORDER BY period_plays {dir}, tg.genre ASC
        LIMIT @limit;";

    private static string BottomGenresSql(string dir, string yearClause) => $@"
        SELECT '' AS item_id,
               tg.genre AS name,
               NULL AS artist,
               NULL AS album,
               COUNT(p.id) AS period_plays,
               (SELECT COUNT(*) FROM plays p2 JOIN track_genres tg2 ON tg2.item_id = p2.item_id WHERE tg2.genre = tg.genre) AS total_plays
        FROM track_genres tg
        JOIN tracks t ON t.item_id = tg.item_id
        LEFT JOIN plays p ON p.item_id = tg.item_id AND p.played_at >= @start AND p.played_at < @end
        WHERE 1=1{yearClause}
        GROUP BY tg.genre
        ORDER BY period_plays {dir}, total_plays ASC, MIN(t.first_seen) ASC, tg.genre ASC
        LIMIT @limit;";
}
