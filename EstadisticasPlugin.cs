using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.Estadisticas.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Estadisticas;

/// <summary>
/// Main entry point for the Jellyfin Estadisticas plugin.
///
/// Responsibilities:
/// - Hold the singleton instance reference (used by services and API controller).
/// - Provide the dashboard configuration page (IHasWebPages).
/// - Persist plugin configuration (BasePlugin&lt;PluginConfiguration&gt;).
///
/// The plugin does NOT modify Jellyfin's database. All statistics are stored in its own
/// SQLite databases located in &lt;JellyfinDataPath&gt;/plugins/estadisticas/.
/// </summary>
public sealed class EstadisticasPlugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly ILogger<EstadisticasPlugin> _logger;

    /// <summary>
    /// Plugin unique identifier. MUST match:
    /// - Properties/AssemblyInfo.cs assembly Guid
    /// - meta.json "guid"
    /// - manifest.json "guid"
    /// </summary>
    public static readonly Guid PluginGuid = Guid.Parse("8a7b6c5d-4e3f-4a2b-9c1d-0e1f2a3b4c5d");

    /// <summary>Singleton reference to the running plugin instance.</summary>
    public static EstadisticasPlugin? Instance { get; private set; }

    /// <summary>Absolute path to the plugin's data directory (where SQLite DBs live).</summary>
    public string DataPath { get; }

    public EstadisticasPlugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        ILogger<EstadisticasPlugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        _logger = logger;
        DataPath = System.IO.Path.Combine(applicationPaths.DataPath, "plugins", "estadisticas");
        System.IO.Directory.CreateDirectory(DataPath);
        _logger.LogInformation("Estadisticas plugin loaded (v{Version}). DataPath={Path}", Version, DataPath);
    }

    public override string Name => "Estadisticas";

    public override Guid Id => PluginGuid;

    public override string Description =>
        "Estadisticas de musica independientes: Top 50 y Bottom 50 por cancion, artista, album y genero " +
        "en ventanas de 2 semanas, 1, 3, 6 y 12 meses, y ano anterior. Base de datos SQLite propia. " +
        "Purga automatica a los 14 meses con archivo historico anual.";

    /// <summary>
    /// Returns the dashboard configuration page (single page with tabs for Top 50, Bottom 50,
    /// scheduled playlists and Resumen). Served from the embedded resource Configuration/config.html.
    /// </summary>
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Configuration.config.html",
                    GetType().Namespace),
                EnableInMainMenu = false
            }
        };
    }
}
