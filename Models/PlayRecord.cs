using System;

namespace Jellyfin.Plugin.Estadisticas.Models;

/// <summary>
/// A single play event recorded in the plugin's SQLite database.
/// One row per actual playback that met the 20-second threshold.
/// </summary>
public sealed class PlayRecord
{
    /// <summary>Database row id.</summary>
    public long Id { get; set; }

    /// <summary>Jellyfin ItemId (Guid string) of the played audio item.</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>UTC ISO 8601 timestamp when the play occurred.</summary>
    public string PlayedAt { get; set; } = string.Empty;

    /// <summary>Jellyfin user id who played the item.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Client name (e.g. "Jellyfin Web", "Finamp", "Sonixd").</summary>
    public string? Client { get; set; }

    /// <summary>Device name (e.g. "Chrome - Windows", "Android Phone").</summary>
    public string? Device { get; set; }

    /// <summary>Convenience: parse PlayedAt back to DateTime (UTC).</summary>
    public DateTime PlayedAtUtc => DateTime.Parse(PlayedAt, null, System.Globalization.DateTimeStyles.RoundtripKind);
}
