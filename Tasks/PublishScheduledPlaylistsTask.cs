using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Estadisticas.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Estadisticas.Tasks;

/// <summary>
/// Scheduled task: check for due scheduled playlists and publish them.
/// Should run frequently (e.g., every 15-30 minutes) so that schedules fire close to
/// their configured time. The task itself only does cheap DB checks; the actual
/// playlist generation happens in <see cref="PlaylistSchedulerService.RunOnceAsync"/>.
/// </summary>
public sealed class PublishScheduledPlaylistsTask : IScheduledTask
{
    private readonly PlaylistSchedulerService _scheduler;
    private readonly ILogger<PublishScheduledPlaylistsTask> _logger;

    public PublishScheduledPlaylistsTask(PlaylistSchedulerService scheduler, ILogger<PublishScheduledPlaylistsTask> logger)
    {
        _scheduler = scheduler;
        _logger = logger;
    }

    public string Name => "Publicar listas de reproduccion programadas";

    public string Key => "EstadisticasPublishScheduledPlaylists";

    public string Description =>
        "Revisa las listas de reproduccion programadas y publica las que esten vencidas " +
        "(reemplazando el contenido de la lista existente en Jellyfin). " +
        "Se recomienda ejecutarlo cada 15-30 minutos.";

    public string Category => "Estadisticas";

    public bool IsHidden => false;

    public bool IsEnabled => true;

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Default: every 1 minute.
        // The task only does a cheap DB check (SELECT WHERE next_run <= now).
        // If nothing is due, it returns immediately. Playlists are only published
        // at the exact moment their schedule says so (e.g. Monday 08:00), not
        // on every tick. The 1-minute granularity makes the actual publish time
        // accurate to within ~60 seconds of the configured schedule.
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromMinutes(1).Ticks
        };
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var due = _scheduler.GetDue();
        if (due.Count == 0)
        {
            _logger.LogDebug("No scheduled playlists due");
            return;
        }

        _logger.LogInformation("{Count} scheduled playlist(s) due", due.Count);
        var i = 0;
        foreach (var sp in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _scheduler.RunOnceAsync(sp, cancellationToken).ConfigureAwait(false);
            i++;
            progress.Report((double)i / due.Count * 100);
        }
    }
}
