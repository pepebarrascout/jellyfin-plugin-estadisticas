using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Estadisticas.Configuration;

/// <summary>
/// Plugin configuration persisted as XML by Jellyfin's BasePlugin mechanism.
/// v0.0.0.1 has no user-tunable settings beyond defaults, but the class exists for
/// future extension (e.g., scrobble threshold, retention months, ignored libraries).
/// </summary>
public sealed class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Scrobble threshold in seconds. A play must reach this position before being recorded.
    /// Default 20s per the project spec. Future versions may expose this in the dashboard.
    /// </summary>
    public int ScrobbleThresholdSeconds { get; set; } = 20;

    /// <summary>
    /// Retention period in months for the main DB. Plays older than this are purged monthly.
    /// Default 14 per the project spec.
    /// </summary>
    public int RetentionMonths { get; set; } = 14;

    /// <summary>
    /// Whether to record plays from non-admin users. Currently false (admin-only as per spec).
    /// Reserved for future use.
    /// </summary>
    public bool RecordAllUsers { get; set; } = false;
}
