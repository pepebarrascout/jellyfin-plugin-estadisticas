using System.Collections.Generic;

namespace Jellyfin.Plugin.Estadisticas.Models;

/// <summary>
/// Cached metadata for a single audio track in the plugin's database.
/// Updated on every play event (in case Jellyfin metadata changed).
/// Genres and Artists are multi-valued and stored in separate tables.
/// </summary>
public sealed class TrackInfo
{
    /// <summary>Jellyfin ItemId (Guid string). Primary key.</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>Song title.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Album artist (the "AlbumArtist" Jellyfin field, single value).</summary>
    public string? AlbumArtist { get; set; }

    /// <summary>Album name.</summary>
    public string? AlbumName { get; set; }

    /// <summary>Song duration in milliseconds.</summary>
    public long? DurationMs { get; set; }

    /// <summary>
    /// Year of the song (from Jellyfin's ProductionYear field). Captured so that
    /// future versions can filter by year (e.g. "most listened of 1982"). Nullable
    /// because not all songs have a year in their metadata.
    /// </summary>
    public int? Year { get; set; }

    /// <summary>File path on disk (for debugging / deduplication if needed).</summary>
    public string? FilePath { get; set; }

    /// <summary>Multi-valued artist list (e.g. "Artist A", "Artist B" for a feat.).</summary>
    public List<string> Artists { get; set; } = new();

    /// <summary>Multi-valued genre list (e.g. "Rock", "Metal").</summary>
    public List<string> Genres { get; set; } = new();
}
