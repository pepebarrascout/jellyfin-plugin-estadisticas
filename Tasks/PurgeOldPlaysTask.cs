using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Estadisticas.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Estadisticas.Tasks;

/// <summary>
/// Scheduled task: purge plays older than 14 months and refresh yearly aggregates
/// in the historical DB. Should run once a month.
///
/// Appears in Dashboard > Scheduled Tasks under the "Estadisticas" category.
/// No default trigger — the user configures the schedule from Jellyfin's UI.
/// </summary>
public sealed class PurgeOldPlaysTask : IScheduledTask
{
    private readonly HistoricalArchiveService _archive;
    private readonly ILogger<PurgeOldPlaysTask> _logger;

    public PurgeOldPlaysTask(HistoricalArchiveService archive, ILogger<PurgeOldPlaysTask> logger)
    {
        _archive = archive;
        _logger = logger;
    }

    public string Name => "Purgar reproducciones antiguas (>14 meses) y actualizar historico";

    public string Key => "EstadisticasPurgeOldPlays";

    public string Description =>
        "Elimina las reproducciones con mas de 14 meses de antiguedad de la base de datos principal " +
        "del plugin (sin tocar la base de datos de Jellyfin) y actualiza el archivo historico anual. " +
        "Se recomienda ejecutarlo una vez al mes.";

    public string Category => "Estadisticas";

    public bool IsHidden => false;

    public bool IsEnabled => true;

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Jellyfin's TaskTriggerInfoType has no MonthlyTrigger, so we use DailyTrigger
        // and check the day-of-month inside ExecuteAsync. Runs at 03:00 daily; on days
        // other than the 1st of the month it returns immediately.
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
        };
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        // Jellyfin has no monthly trigger; we run daily and bail unless today is the 1st.
        var today = DateTime.Now;
        if (today.Day != 1)
        {
            _logger.LogDebug("Purge task: not the 1st of the month (today={Day}), skipping", today.Day);
            return;
        }

        _logger.LogInformation("Starting monthly purge + historical archive maintenance");
        progress.Report(10);

        try
        {
            // Run the (synchronous) maintenance on a background thread to avoid blocking
            // Jellyfin's task scheduler thread for too long with large libraries.
            await Task.Run(() => _archive.RunMonthlyMaintenance(), cancellationToken).ConfigureAwait(false);
            progress.Report(100);
            _logger.LogInformation("Monthly purge completed");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Monthly purge was cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during monthly purge");
            throw;
        }
    }
}
