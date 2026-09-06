using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.Estadisticas.Data;
using Jellyfin.Plugin.Estadisticas.Models;

namespace Jellyfin.Plugin.Estadisticas.Services;

/// <summary>
/// Runs Top 25 and Bottom 25 queries against the plugin's SQLite DB.
/// All windows exclude the current period (current week / month / year).
///
/// Bottom queries use LEFT JOIN to include tracks/artists/genres/albums with 0 plays in
/// the window. Tie-breaking for Bottom:
///   1. period_plays ASC
///   2. total_plays ASC
///   3. name ASC
/// This makes the ranking stable and reproducible.
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
    /// Run a Top/Bottom 25 query.
    /// </summary>
    public List<QueryResultRow> Query(
        QueryDimension dimension,
        QueryDirection direction,
        QueryWindow window,
        int limit = 25)
    {
        var (start, end) = TimeWindow.GetRange(window);
        var startStr = start.ToString("o");
        var endStr = end.ToString("o");
        var dirClause = direction == QueryDirection.Top ? "DESC" : "ASC";

        using var conn = _db.OpenMain();
        using var cmd = conn.CreateCommand();

        switch (dimension)
        {
            case QueryDimension.Songs:
                cmd.CommandText = direction == QueryDirection.Top
                    ? TopSongsSql(dirClause)
                    : BottomSongsSql(dirClause);
                break;
            case QueryDimension.Artists:
                cmd.CommandText = direction == QueryDirection.Top
                    ? TopArtistsSql(dirClause)
                    : BottomArtistsSql(dirClause);
                break;
            case QueryDimension.Albums:
                cmd.CommandText = direction == QueryDirection.Top
                    ? TopAlbumsSql(dirClause)
                    : BottomAlbumsSql(dirClause);
                break;
            case QueryDimension.Genres:
                cmd.CommandText = direction == QueryDirection.Top
                    ? TopGenresSql(dirClause)
                    : BottomGenresSql(dirClause);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(dimension));
        }

        cmd.Parameters.AddWithValue("@start", startStr);
        cmd.Parameters.AddWithValue("@end", endStr);
        cmd.Parameters.AddWithValue("@limit", limit);

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
            "Query: {Dir} {Dim} window={Window} limit={Limit} -> {Count} rows",
            direction, dimension, window, limit, results.Count);
        return results;
    }

    /// <summary>
    /// Returns ONLY the Jellyfin ItemIds for the query result.
    /// Used when building a playlist (we only need item ids, not the display rows).
    /// For non-Songs dimensions (Artists/Albums/Genres), we resolve the matching songs
    /// by descending play count within the window, then take the top `limit` distinct songs.
    /// </summary>
    public List<string> GetItemIdsForPlaylist(
        QueryDimension dimension,
        QueryDirection direction,
        QueryWindow window,
        int limit = 25)
    {
        var (start, end) = TimeWindow.GetRange(window);
        var startStr = start.ToString("o");
        var endStr = end.ToString("o");
        var dirClause = direction == QueryDirection.Top ? "DESC" : "ASC";

        using var conn = _db.OpenMain();
        using var cmd = conn.CreateCommand();

        // For Songs: straightforward — return item_ids in ranking order.
        // For other dimensions: take the top N entities (artists/albums/genres) by their
        // ranking, then for each entity take its 1 most-played song in the window.
        // This gives a playlist that "represents" the ranking without duplicating songs
        // across entities (a song can belong to multiple genres; we only include it once).
        //
        // For v0.0.0.1 we keep this simple: for Songs dimension, return the ranked songs;
        // for other dimensions, return the most-played songs within the window filtered by
        // the top entities of that dimension.

        if (dimension == QueryDimension.Songs)
        {
            cmd.CommandText = direction == QueryDirection.Top
                ? TopSongsSql(dirClause)
                : BottomSongsSql(dirClause);
            cmd.Parameters.AddWithValue("@start", startStr);
            cmd.Parameters.AddWithValue("@end", endStr);
            cmd.Parameters.AddWithValue("@limit", limit);

            var ids = new List<string>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (!reader.IsDBNull(0)) ids.Add(reader.GetString(0));
            }
            return ids;
        }

        // For aggregated dimensions: first get the top entities, then resolve songs.
        // We do this in C# to keep SQL readable.
        var entityNames = new List<string>();
        if (dimension == QueryDimension.Artists)
        {
            cmd.CommandText = direction == QueryDirection.Top
                ? TopArtistsSql(dirClause)
                : BottomArtistsSql(dirClause);
        }
        else if (dimension == QueryDimension.Albums)
        {
            cmd.CommandText = direction == QueryDirection.Top
                ? TopAlbumsSql(dirClause)
                : BottomAlbumsSql(dirClause);
        }
        else // Genres
        {
            cmd.CommandText = direction == QueryDirection.Top
                ? TopGenresSql(dirClause)
                : BottomGenresSql(dirClause);
        }
        cmd.Parameters.AddWithValue("@start", startStr);
        cmd.Parameters.AddWithValue("@end", endStr);
        cmd.Parameters.AddWithValue("@limit", limit);

        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                if (!reader.IsDBNull(1)) entityNames.Add(reader.GetString(1));
            }
        }

        // For each entity, find its best song in the window.
        // For Bottom: "best song" means the song with the FEWEST plays in the window
        // (we want the playlist to represent the Bottom-of-the-ranking entity, so we
        // pick the song that exemplifies "least listened"). For Top: most plays.
        var resultIds = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var orderClause = direction == QueryDirection.Top ? "DESC" : "ASC";

        foreach (var entity in entityNames)
        {
            using var songCmd = conn.CreateCommand();
            songCmd.Parameters.AddWithValue("@start", startStr);
            songCmd.Parameters.AddWithValue("@end", endStr);
            songCmd.Parameters.AddWithValue("@entity", entity);

            if (dimension == QueryDimension.Artists)
            {
                songCmd.CommandText = $@"
                    SELECT t.item_id
                    FROM track_artists ta
                    JOIN tracks t ON t.item_id = ta.item_id
                    LEFT JOIN plays p ON p.item_id = t.item_id AND p.played_at >= @start AND p.played_at < @end
                    WHERE ta.artist = @entity
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
                    WHERE t.album_name = @entity
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
                    WHERE tg.genre = @entity
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

    // ---- SQL templates ----

    private static string TopSongsSql(string dir) => $@"
        SELECT t.item_id,
               t.name,
               COALESCE(t.album_artist || ' - ' || t.album_name, t.album_name, t.album_artist, '') AS subtitle,
               COUNT(p.id) AS period_plays,
               (SELECT COUNT(*) FROM plays p2 WHERE p2.item_id = t.item_id) AS total_plays
        FROM plays p
        JOIN tracks t ON t.item_id = p.item_id
        WHERE p.played_at >= @start AND p.played_at < @end
        GROUP BY t.item_id, t.name, t.album_artist, t.album_name
        ORDER BY period_plays {dir}, t.name ASC
        LIMIT @limit;";

    private static string BottomSongsSql(string dir) => $@"
        SELECT t.item_id,
               t.name,
               COALESCE(t.album_artist || ' - ' || t.album_name, t.album_name, t.album_artist, '') AS subtitle,
               COUNT(p.id) AS period_plays,
               (SELECT COUNT(*) FROM plays p2 WHERE p2.item_id = t.item_id) AS total_plays
        FROM tracks t
        LEFT JOIN plays p ON p.item_id = t.item_id AND p.played_at >= @start AND p.played_at < @end
        GROUP BY t.item_id, t.name, t.album_artist, t.album_name
        ORDER BY period_plays {dir}, total_plays ASC, t.name ASC
        LIMIT @limit;";

    private static string TopArtistsSql(string dir) => $@"
        SELECT '' AS item_id,
               ta.artist AS name,
               NULL AS subtitle,
               COUNT(p.id) AS period_plays,
               (SELECT COUNT(*) FROM plays p2 JOIN track_artists ta2 ON ta2.item_id = p2.item_id WHERE ta2.artist = ta.artist) AS total_plays
        FROM plays p
        JOIN track_artists ta ON ta.item_id = p.item_id
        WHERE p.played_at >= @start AND p.played_at < @end
        GROUP BY ta.artist
        ORDER BY period_plays {dir}, ta.artist ASC
        LIMIT @limit;";

    private static string BottomArtistsSql(string dir) => $@"
        SELECT '' AS item_id,
               ta.artist AS name,
               NULL AS subtitle,
               COUNT(p.id) AS period_plays,
               (SELECT COUNT(*) FROM plays p2 JOIN track_artists ta2 ON ta2.item_id = p2.item_id WHERE ta2.artist = ta.artist) AS total_plays
        FROM track_artists ta
        LEFT JOIN plays p ON p.item_id = ta.item_id AND p.played_at >= @start AND p.played_at < @end
        GROUP BY ta.artist
        ORDER BY period_plays {dir}, total_plays ASC, ta.artist ASC
        LIMIT @limit;";

    private static string TopAlbumsSql(string dir) => $@"
        SELECT '' AS item_id,
               COALESCE(t.album_name, '(Sin album)') AS name,
               t.album_artist AS subtitle,
               COUNT(p.id) AS period_plays,
               (SELECT COUNT(*) FROM plays p2 JOIN tracks t2 ON t2.item_id = p2.item_id WHERE t2.album_name = t.album_name AND t2.album_artist IS t.album_artist) AS total_plays
        FROM plays p
        JOIN tracks t ON t.item_id = p.item_id
        WHERE p.played_at >= @start AND p.played_at < @end
          AND t.album_name IS NOT NULL
        GROUP BY t.album_name, t.album_artist
        ORDER BY period_plays {dir}, t.album_name ASC
        LIMIT @limit;";

    private static string BottomAlbumsSql(string dir) => $@"
        SELECT '' AS item_id,
               COALESCE(t.album_name, '(Sin album)') AS name,
               t.album_artist AS subtitle,
               COUNT(p.id) AS period_plays,
               (SELECT COUNT(*) FROM plays p2 JOIN tracks t2 ON t2.item_id = p2.item_id WHERE t2.album_name IS t.album_name AND t2.album_artist IS t.album_artist) AS total_plays
        FROM tracks t
        LEFT JOIN plays p ON p.item_id = t.item_id AND p.played_at >= @start AND p.played_at < @end
        WHERE t.album_name IS NOT NULL
        GROUP BY t.album_name, t.album_artist
        ORDER BY period_plays {dir}, total_plays ASC, t.album_name ASC
        LIMIT @limit;";

    private static string TopGenresSql(string dir) => $@"
        SELECT '' AS item_id,
               tg.genre AS name,
               NULL AS subtitle,
               COUNT(p.id) AS period_plays,
               (SELECT COUNT(*) FROM plays p2 JOIN track_genres tg2 ON tg2.item_id = p2.item_id WHERE tg2.genre = tg.genre) AS total_plays
        FROM plays p
        JOIN track_genres tg ON tg.item_id = p.item_id
        WHERE p.played_at >= @start AND p.played_at < @end
        GROUP BY tg.genre
        ORDER BY period_plays {dir}, tg.genre ASC
        LIMIT @limit;";

    private static string BottomGenresSql(string dir) => $@"
        SELECT '' AS item_id,
               tg.genre AS name,
               NULL AS subtitle,
               COUNT(p.id) AS period_plays,
               (SELECT COUNT(*) FROM plays p2 JOIN track_genres tg2 ON tg2.item_id = p2.item_id WHERE tg2.genre = tg.genre) AS total_plays
        FROM track_genres tg
        LEFT JOIN plays p ON p.item_id = tg.item_id AND p.played_at >= @start AND p.played_at < @end
        GROUP BY tg.genre
        ORDER BY period_plays {dir}, total_plays ASC, tg.genre ASC
        LIMIT @limit;";
}
