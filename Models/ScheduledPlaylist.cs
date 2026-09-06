using System;

namespace Jellyfin.Plugin.Estadisticas.Models;

/// <summary>
/// Frequency at which a scheduled playlist is republished.
/// </summary>
public enum ScheduleFrequency
{
    /// <summary>Every day at the configured time.</summary>
    Daily,

    /// <summary>On a specific day of the week (0=Monday..6=Sunday) at the configured time.</summary>
    Weekly,

    /// <summary>On a specific day of the month (1..28) at the configured time. Days 29-31 are skipped on months that don't have them.</summary>
    Monthly,

    /// <summary>On a specific month-and-day (MM-DD) at the configured time.</summary>
    Yearly
}

/// <summary>
/// Represents a scheduled playlist entry stored in the plugin DB.
/// Each entry maps a (query, window) pair to a Jellyfin playlist that is republished
/// on a schedule. The playlist content is REPLACED on each run (same playlist id, new items).
/// </summary>
public sealed class ScheduledPlaylist
{
    /// <summary>Database row id.</summary>
    public long Id { get; set; }

    /// <summary>User-facing playlist name (also used as Jellyfin playlist name).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Jellyfin playlist Guid (string). Null if the playlist hasn't been created yet.</summary>
    public string? JellyfinPlaylistId { get; set; }

    /// <summary>Query dimension (Songs/Artists/Albums/Genres).</summary>
    public string QueryDimension { get; set; } = nameof(Models.QueryDimension.Songs);

    /// <summary>Query direction (Top/Bottom).</summary>
    public string QueryDirection { get; set; } = nameof(Models.QueryDirection.Top);

    /// <summary>Query window code (2w, 1m, 3m, 6m, 12m, last_year).</summary>
    public string QueryWindow { get; set; } = "12m";

    /// <summary>Maximum number of items in the playlist (default 25).</summary>
    public int Limit { get; set; } = 25;

    /// <summary>Schedule frequency.</summary>
    public string Frequency { get; set; } = nameof(ScheduleFrequency.Daily);

    /// <summary>Time of day in "HH:mm" (24h, server local time).</summary>
    public string TimeOfDay { get; set; } = "08:00";

    /// <summary>Day of week 0=Mon..6=Sun (used when Frequency == Weekly).</summary>
    public int? DayOfWeek { get; set; }

    /// <summary>Day of month 1..28 (used when Frequency == Monthly).</summary>
    public int? DayOfMonth { get; set; }

    /// <summary>Month-and-day "MM-DD" (used when Frequency == Yearly).</summary>
    public string? MonthAndDay { get; set; }

    /// <summary>Whether this schedule is active.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Last run timestamp (UTC ISO 8601) or null.</summary>
    public string? LastRun { get; set; }

    /// <summary>Next scheduled run timestamp (UTC ISO 8601) or null.</summary>
    public string? NextRun { get; set; }

    /// <summary>Creation timestamp (UTC ISO 8601).</summary>
    public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("o");

    /// <summary>Last update timestamp (UTC ISO 8601).</summary>
    public string UpdatedAt { get; set; } = DateTime.UtcNow.ToString("o");
}
