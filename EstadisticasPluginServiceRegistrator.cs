using System;
using System.Linq;
using Jellyfin.Plugin.Estadisticas.Data;
using Jellyfin.Plugin.Estadisticas.Services;
using Jellyfin.Plugin.Estadisticas.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Estadisticas;

/// <summary>
/// Registers plugin services with Jellyfin's DI container.
/// Discovered by Jellyfin via reflection (must implement IPluginServiceRegistrator)
/// and must be a separate class from <see cref="EstadisticasPlugin"/>.
/// </summary>
public sealed class EstadisticasPluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // SQLite database manager (singleton: holds connection strings, initializes schema once).
        // CRITICAL: Initialize() is wrapped in try-catch so that a schema error never crashes
        // Jellyfin's startup. If initialization fails, the plugin is degraded (queries will throw)
        // but the server keeps running. The error is logged prominently.
        serviceCollection.AddSingleton<SqliteDb>(sp =>
        {
            var appPaths = sp.GetRequiredService<IApplicationPaths>();
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SqliteDb>>();
            var pluginDataPath = System.IO.Path.Combine(appPaths.DataPath, "plugins", "estadisticas");
            var db = new SqliteDb(pluginDataPath, logger);
            try
            {
                db.Initialize(); // create schema on first run
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Estadisticas: SQLite initialization FAILED. Plugin will be non-functional " +
                    "but Jellyfin will continue. Error: {Message}", ex.Message);
                // Do NOT rethrow — swallow so Jellyfin can start.
            }
            return db;
        });

        // Statistics service (transient — cheap to construct, just runs SQL).
        serviceCollection.AddTransient<StatisticsService>();

        // Playlist publisher (transient).
        serviceCollection.AddTransient<PlaylistPublisherService>();

        // Playlist scheduler (singleton — in-memory state is minimal but singleton is safe).
        serviceCollection.AddSingleton<PlaylistSchedulerService>(sp =>
        {
            var db = sp.GetRequiredService<SqliteDb>();
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PlaylistSchedulerService>>();
            var stats = sp.GetRequiredService<StatisticsService>();
            var publisher = sp.GetRequiredService<PlaylistPublisherService>();

            // Resolve admin user id lazily via the IUserManager.
            Func<Guid> adminProvider = () =>
            {
                try
                {
                    var userManager = sp.GetRequiredService<IUserManager>();
                    foreach (var u in userManager.GetUsers())
                    {
                        if (IsAdminUser(u)) return u.Id;
                    }
                    // Fallback: first user
                    foreach (var u in userManager.GetUsers())
                        return u.Id;
                }
                catch
                {
                    // swallow; will be retried on next call
                }
                return Guid.Empty;
            };

            return new PlaylistSchedulerService(db, logger, stats, publisher, adminProvider);
        });

        // Historical archive service (transient).
        serviceCollection.AddTransient<HistoricalArchiveService>();

        // Playback tracker: hosted service that subscribes to playback events.
        serviceCollection.AddHostedService<PlaybackTrackerService>();

        // Scheduled tasks
        serviceCollection.AddSingleton<MediaBrowser.Model.Tasks.IScheduledTask, PurgeOldPlaysTask>();
        serviceCollection.AddSingleton<MediaBrowser.Model.Tasks.IScheduledTask, PublishScheduledPlaylistsTask>();
    }

    /// <summary>
    /// Returns true if the given Jellyfin user is an administrator.
    /// Uses the Permissions navigation collection (the modern EF Core User class
    /// in Jellyfin 10.11+ does not expose a HasPermission method directly).
    /// </summary>
    private static bool IsAdminUser(Jellyfin.Database.Implementations.Entities.User u)
    {
        try
        {
            return u.Permissions?.Any(p =>
                p.Kind == Jellyfin.Database.Implementations.Enums.PermissionKind.IsAdministrator
                && p.Value) ?? false;
        }
        catch
        {
            return false;
        }
    }
}
