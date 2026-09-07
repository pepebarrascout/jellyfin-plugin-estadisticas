using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.Estadisticas.Data;
using Jellyfin.Plugin.Estadisticas.Models;

namespace Jellyfin.Plugin.Estadisticas.Services;

/// <summary>
/// Runs Top 50 and Bottom 50 queries against the plugin's SQLite DB.
/// All windows exclude the current period (current week / month / year).
///
/// Bottom queries use LEFT JOIN to include tracks/artists/genres/albums with 0 plays in
/// the window. Tie-breaking for Bottom (v0.0.0.7):
///   1. period_plays ASC
///   2. total_plays ASC (historic)
///   3. first_seen ASC  (chronological: the oldest track in the DB ranks first)
///   4. name ASC (deterministic last resort)
/// This makes the ranking stable and reproducible.
///
/// v0.0.0.8: optional year filter. When set (e.g. 1982), only songs whose release year
/// matches are included in the results.
/// </summary>
public sealed class StatisticsService
{
    private readonly SqliteDb _db;
    private readonly ILogger<StatisticsService> _logger;

    public StatisticsService(SqliteDb db, ILogger<StatisticsService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Run a Top/Bottom 50 query.
    /// </summary>
    /// <param name="yearFilter">Optional: only include songs released in this year.</param>
    public List<QueryResultRow> Query(
        QueryDimension dimension,
        QueryDirection direction,
        QueryWindow window,
        int limit = 50,
        int? yearFilter = null)
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
                Subtitle = reader.IsDBNull(2) ? null : reader.GetString(2),
                PlayCount = reader.IsDBNull(3) ? 0 : reader.GetInt64(3),
                TotalPlayCount = reader.IsDBNull(4) ? 0 : reader.GetInt64(4)
            });
        }

        _logger.LogDebug(
            "Query: {Dir} {Dim} window={Window} limit={Limit} year={Year} -> {Count} rows",
            direction, dimension, window, limit, yearFilter, results.Count);
        return results;
    }

    /// <summary>
    /// Returns ONLY the Jellyfin ItemIds for the query result.
    /// Used when building a playlist (we only need item ids, not the display rows).
    /// For non-Songs dimensions (Artists/Albums/Genres), we resolve the matching songs
    /// by descending play count within the window, then take the top `limit` distinct songs.
    /// </summary>
    /// <param name="yearFilter">Optional: only include songs released in this year.</param>
    public List<string> GetItemIdsForPlaylist(
        QueryDimension dimension,
        QueryDirection direction,
        QueryWindow window,
        int limit = 50,
        int? yearFilter = null)
    {
        var (start, end) = TimeWindow.GetRange(window);
        var startStr = start.ToString("o");
        var endStr = end.ToString("o");
        var dirClause = direction == QueryDirection.Top ? "DESC" : "ASC";
        var yearClause = yearFilter.HasValue ? " AND t.year = @year" : "";
        var yearClauseSongs = yearFilter.HasValue ? " AND t.year = @year" : "";

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

        // For aggregated dimensions: first get the top entities, then resolve songs.
        var entityNames = new List<string>();
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
        else // Genres
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
                if (!reader.IsDBNull(1)) entityNames.Add(reader.GetString(1));
            }
        }

        // For each entity, find its best song in the window.
        var resultIds = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var orderClause = direction == QueryDirection.Top ? "DESC" : "ASC";
        var yearFilterSongClause = yearFilter.HasValue ? " AND t.year = @year" : "";

        foreach (var entity in entityNames)
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
            else // Genres
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
    /// ItemIds for SCHEDULED playlists (v0.0.0.7).
    ///
    /// - Dimension = Songs:  ranked songs (most/least played in the window).
    /// - Dimension = Genres: ranked songs of ONE genre (genreFilter). If genreFilter is
    ///   empty it falls back to all songs.
    /// - Artists/Albums are no longer offered for scheduled lists.
    ///
    /// v0.0.0.8: optional year filter. When set, only songs released in that year
    /// are included.
    ///
    /// The `ascending` flag controls the ORDER INSIDE the playlist:
    ///   ascending  = from the least played to the most played,
    ///   descending = from the most played to the least played.
    ///
    /// Ties (same period plays) always resolve as: less historic plays first,
    /// then chronological (first_seen ASC: the oldest registered tracks first).
    /// </summary>
    public List<string> GetScheduledItemIds(
        QueryDimension dimension,
        QueryWindow window,
        int limit,
        bool ascending,
        string? genreFilter,
        int? yearFilter = null)
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

    /// <summary>
    /// Distinct list of every genre known to the plugin DB (used by the
    /// scheduled-playlists form to let the user pick one genre).
    /// </summary>
    public List<string> GetAllGenres()
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
    }

    /// <summary>
    /// Distinct list of every release year known to the plugin DB (used by the
    /// UI to populate the year selector). Ordered descending (newest first).
    /// </summary>
    public List<int> GetAllYears()
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
    }

    /// <summary>
    /// Total listening time (milliseconds) broken down by genre and time window.
    /// Used by the "Resumen" tab to show the "Tiempo total escuchado" table.
    ///
    /// Approximation: sums the FULL duration of each track that was played in the
    /// window, regardless of how many seconds the user actually listened. This is
    /// the agreed tradeoff to avoid capturing position_ms (which would overload
    /// the Raspberry Pi server with extra writes per play).
    ///
    /// Returns a dictionary: genre -> (windowCode -> totalMs).
    /// Genres with 0 plays in ALL windows are excluded.
    /// </summary>
    public Dictionary<string, Dictionary<string, long>> GetListeningTimeByGenrePerWindow()
    {
        var result = new Dictionary<string, Dictionary<string, long>>(StringComparer.OrdinalIgnoreCase);
        var windows = new[] { QueryWindow.TwoWeeks, QueryWindow.OneMonth, QueryWindow.ThreeMonths, QueryWindow.SixMonths, QueryWindow.TwelveMonths, QueryWindow.LastYear };

        using var conn = _db.OpenMain();
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

        return result;
    }

    // ---- SQL templates ----
    // Each template accepts a `yearClause` string that is injected into the WHERE.
    // When no year filter is active, yearClause is "" (empty string, no-op).

    private static string TopSongsSql(string dir, string yearClause) => $@"
        SELECT t.item_id,
               t.name,
               COALESCE(t.album_artist || ' - ' || t.album_name, t.album_name, t.album_artist, '') AS subtitle,
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
               COALESCE(t.album_artist || ' - ' || t.album_name, t.album_name, t.album_artist, '') AS subtitle,
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
               NULL AS subtitle,
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
               NULL AS subtitle,
               COUNT(p.id) AS period_plays,
               (SELECT COUNT(*) FROM plays p2 JOIN track_artists ta2 ON ta2.item_id = p2.item_id WHERE ta2.artist = ta.artist) AS total_plays
        FROM track_artists ta
        JOIN tracks t ON t.item_id = ta.item_id
        LEFT JOIN plays p ON p.item_id = ta.item_id AND p.played_at >= @start AND p.played_at < @end
        WHERE 1=1{yearClause}
        GROUP BY ta.artist
        ORDER BY period_plays {dir}, total_plays ASC, MIN(t.first_seen) ASC, ta.artist ASC
        LIMIT @limit;";

    private static string TopAlbumsSql(string dir, string yearClause) => $@"
        SELECT '' AS item_id,
               COALESCE(t.album_name, '(Sin album)') AS name,
               t.album_artist AS subtitle,
               COUNT(p.id) AS period_plays,
               (SELECT COUNT(*) FROM plays p2 JOIN tracks t2 ON t2.item_id = p2.item_id WHERE t2.album_name = t.album_name AND t2.album_artist IS t.album_artist) AS total_plays
        FROM plays p
        JOIN tracks t ON t.item_id = p.item_id
        WHERE p.played_at >= @start AND p.played_at < @end
          AND t.album_name IS NOT NULL{yearClause}
        GROUP BY t.album_name, t.album_artist
        ORDER BY period_plays {dir}, t.album_name ASC
        LIMIT @limit;";

    private static string BottomAlbumsSql(string dir, string yearClause) => $@"
        SELECT '' AS item_id,
               COALESCE(t.album_name, '(Sin album)') AS name,
               t.album_artist AS subtitle,
               COUNT(p.id) AS period_plays,
               (SELECT COUNT(*) FROM plays p2 JOIN tracks t2 ON t2.item_id = p2.item_id WHERE t2.album_name IS t.album_name AND t2.album_artist IS t.album_artist) AS total_plays
        FROM tracks t
        LEFT JOIN plays p ON p.item_id = t.item_id AND p.played_at >= @start AND p.played_at < @end
        WHERE t.album_name IS NOT NULL{yearClause}
        GROUP BY t.album_name, t.album_artist
        ORDER BY period_plays {dir}, total_plays ASC, MIN(t.first_seen) ASC, t.album_name ASC
        LIMIT @limit;";

    private static string TopGenresSql(string dir, string yearClause) => $@"
        SELECT '' AS item_id,
               tg.genre AS name,
               NULL AS subtitle,
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
               NULL AS subtitle,
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
