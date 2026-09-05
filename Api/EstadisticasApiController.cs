using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Estadisticas.Models;
using Jellyfin.Plugin.Estadisticas.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Estadisticas.Api;

/// <summary>
/// API endpoints called from the dashboard configuration page.
///
/// Routes are prefixed with /Plugins/Estadisticas and require an admin user
/// (Jellyfin itself enforces this for plugin API calls).
/// </summary>
[ApiController]
[Route("Plugins/Estadisticas")]
public sealed class EstadisticasApiController : ControllerBase
{
    private readonly ILogger<EstadisticasApiController> _logger;
    private readonly StatisticsService _stats;
    private readonly PlaylistSchedulerService _scheduler;
    private readonly PlaylistPublisherService _publisher;

    public EstadisticasApiController(
        ILogger<EstadisticasApiController> logger,
        StatisticsService stats,
        PlaylistSchedulerService scheduler,
        PlaylistPublisherService publisher)
    {
        _logger = logger;
        _stats = stats;
        _scheduler = scheduler;
        _publisher = publisher;
    }

    /// <summary>Get a Top/Bottom 25 result for the given parameters.</summary>
    [HttpGet("Query")]
    public ActionResult Query(
        [FromQuery] string dimension,
        [FromQuery] string direction,
        [FromQuery] string window,
        [FromQuery] int limit = 25)
    {
        try
        {
            var dim = Enum.Parse<QueryDimension>(dimension, true);
            var dir = Enum.Parse<QueryDirection>(direction, true);
            var win = TimeWindow.ParseCode(window);
            var rows = _stats.Query(dim, dir, win, limit);
            return Ok(new
            {
                success = true,
                dimension = dim.ToString(),
                direction = dir.ToString(),
                window = TimeWindow.Code(win),
                windowLabel = TimeWindow.Label(win),
                rangeStart = TimeWindow.GetRange(win).Start.ToString("o"),
                rangeEnd = TimeWindow.GetRange(win).End.ToString("o"),
                rows
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Query error");
            return Ok(new { success = false, error = ex.Message });
        }
    }

    /// <summary>Create a one-shot playlist from a query. Returns the new Jellyfin playlist id.</summary>
    [HttpPost("CreatePlaylist")]
    public async Task<ActionResult> CreatePlaylist(
        [FromQuery] string name,
        [FromQuery] string dimension,
        [FromQuery] string direction,
        [FromQuery] string window,
        [FromQuery] int limit = 25)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(name))
                return Ok(new { success = false, error = "Falta el nombre de la playlist." });

            var dim = Enum.Parse<QueryDimension>(dimension, true);
            var dir = Enum.Parse<QueryDirection>(direction, true);
            var win = TimeWindow.ParseCode(window);

            var itemIds = _stats.GetItemIdsForPlaylist(dim, dir, win, limit);
            if (itemIds.Count == 0)
                return Ok(new { success = false, error = "La consulta no produjo resultados." });

            var adminId = ResolveAdminId();
            if (adminId == Guid.Empty)
                return Ok(new { success = false, error = "No se encontro un usuario administrador." });

            var playlistId = await _publisher.PublishAsync(name, null, itemIds, adminId).ConfigureAwait(false);
            return Ok(new { success = true, playlistId, itemCount = itemIds.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CreatePlaylist error");
            return Ok(new { success = false, error = ex.Message });
        }
    }

    /// <summary>List all scheduled playlists.</summary>
    [HttpGet("ScheduledPlaylists")]
    public ActionResult ListScheduled()
    {
        try
        {
            var list = _scheduler.ListAll();
            return Ok(new { success = true, items = list });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ListScheduled error");
            return Ok(new { success = false, error = ex.Message });
        }
    }

    /// <summary>Create a new scheduled playlist.</summary>
    [HttpPost("ScheduledPlaylists")]
    public ActionResult CreateScheduled([FromBody] ScheduledPlaylistDto dto)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dto?.Name))
                return Ok(new { success = false, error = "Falta el nombre." });

            var sp = MapDto(dto);
            var id = _scheduler.Insert(sp);
            return Ok(new { success = true, id });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CreateScheduled error");
            return Ok(new { success = false, error = ex.Message });
        }
    }

    /// <summary>Update an existing scheduled playlist.</summary>
    [HttpPut("ScheduledPlaylists/{id}")]
    public ActionResult UpdateScheduled(long id, [FromBody] ScheduledPlaylistDto dto)
    {
        try
        {
            var existing = _scheduler.GetById(id);
            if (existing == null) return Ok(new { success = false, error = "No encontrado." });
            var sp = MapDto(dto, existing);
            sp.Id = id;
            _scheduler.Update(sp);
            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UpdateScheduled error");
            return Ok(new { success = false, error = ex.Message });
        }
    }

    /// <summary>Delete a scheduled playlist.</summary>
    [HttpDelete("ScheduledPlaylists/{id}")]
    public ActionResult DeleteScheduled(long id)
    {
        try
        {
            _scheduler.Delete(id);
            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DeleteScheduled error");
            return Ok(new { success = false, error = ex.Message });
        }
    }

    /// <summary>Trigger an immediate run of a scheduled playlist (regardless of schedule).</summary>
    [HttpPost("ScheduledPlaylists/{id}/Run")]
    public async Task<ActionResult> RunScheduled(long id)
    {
        try
        {
            var sp = _scheduler.GetById(id);
            if (sp == null) return Ok(new { success = false, error = "No encontrado." });
            await _scheduler.RunOnceAsync(sp).ConfigureAwait(false);
            return Ok(new { success = true, lastRun = sp.LastRun, nextRun = sp.NextRun });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RunScheduled error");
            return Ok(new { success = false, error = ex.Message });
        }
    }

    private static ScheduledPlaylist MapDto(ScheduledPlaylistDto dto, ScheduledPlaylist? existing = null)
    {
        var sp = existing ?? new ScheduledPlaylist();
        sp.Name = dto.Name!;
        sp.JellyfinPlaylistId = dto.JellyfinPlaylistId ?? sp.JellyfinPlaylistId;
        sp.QueryDimension = string.IsNullOrWhiteSpace(dto.QueryDimension) ? nameof(QueryDimension.Songs) : dto.QueryDimension;
        sp.QueryDirection = string.IsNullOrWhiteSpace(dto.QueryDirection) ? nameof(QueryDirection.Top) : dto.QueryDirection;
        sp.QueryWindow = string.IsNullOrWhiteSpace(dto.QueryWindow) ? "12m" : dto.QueryWindow;
        sp.Limit = dto.Limit > 0 ? dto.Limit : 25;
        sp.Frequency = string.IsNullOrWhiteSpace(dto.Frequency) ? nameof(ScheduleFrequency.Daily) : dto.Frequency;
        sp.TimeOfDay = string.IsNullOrWhiteSpace(dto.TimeOfDay) ? "08:00" : dto.TimeOfDay;
        sp.DayOfWeek = dto.DayOfWeek;
        sp.DayOfMonth = dto.DayOfMonth;
        sp.MonthAndDay = dto.MonthAndDay;
        sp.Enabled = dto.Enabled ?? true;
        return sp;
    }

    private Guid ResolveAdminId()
    {
        try
        {
            var userManager = HttpContext.RequestServices.GetService(typeof(MediaBrowser.Controller.Library.IUserManager))
                as MediaBrowser.Controller.Library.IUserManager;
            if (userManager == null) return Guid.Empty;
            foreach (var u in userManager.GetUsers())
                if (IsAdminUser(u))
                    return u.Id;
            foreach (var u in userManager.GetUsers())
                return u.Id;
        }
        catch { }
        return Guid.Empty;
    }

    private static bool IsAdminUser(Jellyfin.Database.Implementations.Entities.User u)
    {
        try
        {
            return u.Permissions?.Any(p => p.Kind == Jellyfin.Database.Implementations.Enums.PermissionKind.IsAdministrator && p.Value) ?? false;
        }
        catch { return false; }
    }

    /// <summary>DTO for create/update scheduled playlist.</summary>
    public sealed class ScheduledPlaylistDto
    {
        public string? Name { get; set; }
        public string? JellyfinPlaylistId { get; set; }
        public string? QueryDimension { get; set; }
        public string? QueryDirection { get; set; }
        public string? QueryWindow { get; set; }
        public int Limit { get; set; }
        public string? Frequency { get; set; }
        public string? TimeOfDay { get; set; }
        public int? DayOfWeek { get; set; }
        public int? DayOfMonth { get; set; }
        public string? MonthAndDay { get; set; }
        public bool? Enabled { get; set; }
    }
}
