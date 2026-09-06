namespace Jellyfin.Plugin.Estadisticas.Models;

/// <summary>
/// Aggregation dimension for Top/Bottom queries.
/// </summary>
public enum QueryDimension
{
    /// <summary>Aggregate by individual song.</summary>
    Songs,

    /// <summary>Aggregate by artist (multi-valued: a song with 2 artists counts in both).</summary>
    Artists,

    /// <summary>Aggregate by album (album_artist + album_name).</summary>
    Albums,

    /// <summary>Aggregate by genre (multi-valued: a song with 2 genres counts in both).</summary>
    Genres
}

/// <summary>
/// Time window for Top/Bottom queries. All windows EXCLUDE the current period
/// (current week for 2w, current month for the monthly windows, current year for last_year).
/// </summary>
public enum QueryWindow
{
    /// <summary>Last 2 complete weeks (Mon-Sun). Excludes current week.</summary>
    TwoWeeks,

    /// <summary>Last complete calendar month. Excludes current month.</summary>
    OneMonth,

    /// <summary>Last 3 complete calendar months. Excludes current month.</summary>
    ThreeMonths,

    /// <summary>Last 6 complete calendar months. Excludes current month.</summary>
    SixMonths,

    /// <summary>Last 12 complete calendar months. Excludes current month.</summary>
    TwelveMonths,

    /// <summary>Last complete calendar year. Excludes current year.</summary>
    LastYear
}

/// <summary>
/// Direction of the ranking.
/// </summary>
public enum QueryDirection
{
    /// <summary>Most listened (descending by play count).</summary>
    Top,

    /// <summary>Least listened (ascending by play count, includes 0-play items).</summary>
    Bottom
}

/// <summary>
/// A single row in a Top/Bottom result table.
/// </summary>
public sealed class QueryResultRow
{
    /// <summary>1-based rank in the result set.</summary>
    public int Rank { get; set; }

    /// <summary>For Songs: the Jellyfin ItemId. Empty for aggregated dimensions.</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>Display name (song title, artist name, album name, or genre).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Secondary text (e.g. "Artist - Album" for songs, "Artist" for albums).</summary>
    public string? Subtitle { get; set; }

    /// <summary>Play count within the queried window.</summary>
    public long PlayCount { get; set; }

    /// <summary>Total play count (all time, in the plugin DB). Used as tie-breaker for Bottom queries.</summary>
    public long TotalPlayCount { get; set; }
}
