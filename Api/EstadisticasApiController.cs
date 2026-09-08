using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Estadisticas.Data;
using Jellyfin.Plugin.Estadisticas.Models;
using Jellyfin.Plugin.Estadisticas.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
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
    private readonly SqliteDb _db;
    private readonly StatisticsService _stats;
    private readonly PlaylistSchedulerService _scheduler;
    private readonly PlaylistPublisherService _publisher;
    private readonly DebugService _debug;
    private readonly AchievementService _achievements;
    private readonly HistoricalArchiveService _historical;
    private readonly ChartService _charts;

    public EstadisticasApiController(
        ILogger<EstadisticasApiController> logger,
        SqliteDb db,
        StatisticsService stats,
        PlaylistSchedulerService scheduler,
        PlaylistPublisherService publisher,
        DebugService debug,
        AchievementService achievements,
        HistoricalArchiveService historical,
        ChartService charts)
    {
        _logger = logger;
        _db = db;
        _stats = stats;
        _scheduler = scheduler;
        _publisher = publisher;
        _debug = debug;
        _achievements = achievements;
        _historical = historical;
        _charts = charts;
    }

    /// <summary>
    /// Helper that returns a clear error if the SQLite DB failed to initialize.
    /// All endpoints that touch the DB should call this first.
    /// </summary>
    private bool EnsureDbReady(out ActionResult error)
    {
        if (!_db.IsInitialized)
        {
            error = Ok(new { success = false, error = "La base de datos SQLite del plugin no se inicializo. Revisa los logs de Jellyfin para ver el error original." });
            return false;
        }
        error = Ok(new { });
        return true;
    }

    /// <summary>Get a Top/Bottom 50 result for the given parameters.</summary>
    [HttpGet("Query")]
    public ActionResult Query(
        [FromQuery] string dimension,
        [FromQuery] string direction,
        [FromQuery] string window,
        [FromQuery] int limit = 50,
        [FromQuery] int? year = null)
    {
        if (!EnsureDbReady(out var err)) return err;
        try
        {
            var dim = Enum.Parse<QueryDimension>(dimension, true);
            var dir = Enum.Parse<QueryDirection>(direction, true);
            var win = TimeWindow.ParseCode(window);
            var rows = _stats.Query(dim, dir, win, limit, year);
            // INCLUSIVE display boundaries (server-local dates) shown in the UI,
            // e.g. "01-Ago-2026 a 31-Ago-2026" (the internal SQL range stays half-open).
            var (displayStart, displayEnd) = TimeWindow.GetDisplayRange(win);
            return Ok(new
            {
                success = true,
                dimension = dim.ToString(),
                direction = dir.ToString(),
                window = TimeWindow.Code(win),
                windowLabel = TimeWindow.Label(win),
                rangeStart = displayStart.ToString("yyyy-MM-dd"),
                rangeEnd = displayEnd.ToString("yyyy-MM-dd"),
                yearFilter = year,
                rows,
                rowCount = rows.Count
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
        [FromQuery] int limit = 50,
        [FromQuery] int? year = null)
    {
        if (!EnsureDbReady(out var err)) return err;
        try
        {
            if (string.IsNullOrWhiteSpace(name))
                return Ok(new { success = false, error = "Falta el nombre de la playlist." });

            var dim = Enum.Parse<QueryDimension>(dimension, true);
            var dir = Enum.Parse<QueryDirection>(direction, true);
            var win = TimeWindow.ParseCode(window);

            var itemIds = _stats.GetItemIdsForPlaylist(dim, dir, win, limit, year);
            if (itemIds.Count == 0)
                return Ok(new { success = false, error = "La consulta no produjo resultados. Prueba con otra ventana temporal o dimension." });

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

    /// <summary>
    /// Returns diagnostic info about the plugin DB: row counts, server time and
    /// per-window play counts. Displayed in the "Resumen" tab.
    /// </summary>
    [HttpGet("Debug/Status")]
    public ActionResult DebugStatus()
    {
        try
        {
            var status = _debug.GetStatus();
            return Ok(new { success = true, status });
        }
        catch (Exception ex)
        {
            return Ok(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Wipes all plays, track_artists, track_genres and tracks from the main DB.
    /// Scheduled playlists are KEPT (the user may have configured real ones).
    /// </summary>
    [HttpPost("Debug/ClearAll")]
    public ActionResult DebugClearAll()
    {
        try
        {
            var result = _debug.ClearAllPlays();
            return Ok(result);
        }
        catch (Exception ex)
        {
            return Ok(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Distinct genres known to the plugin DB. Used by the scheduled-playlists
    /// form so the user can pick ONE genre (e.g. "Rock") when the dimension is Genres.
    /// </summary>
    [HttpGet("Genres")]
    public ActionResult GetGenres()
    {
        if (!EnsureDbReady(out var err)) return err;
        try
        {
            var genres = _stats.GetAllGenres();
            return Ok(new { success = true, genres });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetGenres error");
            return Ok(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Distinct release years known to the plugin DB (v0.0.0.8). Used by the
    /// UI to populate the year selector. Ordered descending (newest first).
    /// </summary>
    [HttpGet("Years")]
    public ActionResult GetYears()
    {
        if (!EnsureDbReady(out var err)) return err;
        try
        {
            var years = _stats.GetAllYears();
            return Ok(new { success = true, years });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetYears error");
            return Ok(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Total listening time (ms) by genre and time window (v0.0.0.8).
    /// Used by the "Resumen" tab to show the "Tiempo total escuchado" table.
    /// Approximation: sums the FULL duration of each track played in the window.
    /// </summary>
    [HttpGet("ListeningTime")]
    public ActionResult GetListeningTime()
    {
        if (!EnsureDbReady(out var err)) return err;
        try
        {
            var data = _stats.GetListeningTimeByGenrePerWindow();
            return Ok(new { success = true, data });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetListeningTime error");
            return Ok(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Returns all achievements with their current progress and earned status (v0.0.0.9).
    /// Displayed in the "Logros" tab.
    /// </summary>
    [HttpGet("Achievements")]
    public ActionResult GetAchievements()
    {
        if (!EnsureDbReady(out var err)) return err;
        try
        {
            var achievements = _achievements.GetAllAchievements();
            var earned = achievements.Count(a => a.Earned);
            var total = achievements.Count;
            return Ok(new { success = true, achievements, earnedCount = earned, totalCount = total });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetAchievements error");
            return Ok(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Returns the list of years available in the historical DB (v0.0.0.9).
    /// </summary>
    [HttpGet("Historical/Years")]

    // ====== CHARTS ENDPOINTS (v0.0.0.13) ======

    /// <summary>Plays per day for timeline chart.</summary>
    [HttpGet("Charts/Timeline")]
    public ActionResult ChartsTimeline([FromQuery] string window = "12m")
    {
        if (!EnsureDbReady(out var err)) return err;
        try
        {
            var win = TimeWindow.ParseCode(window);
            var data = _charts.GetTimeline(win);
            return Ok(new { success = true, data });
        }
        catch (Exception ex) { return Ok(new { success = false, error = ex.Message }); }
    }

    /// <summary>Plays per day for heatmap (last N months).</summary>
    [HttpGet("Charts/Heatmap")]
    public ActionResult ChartsHeatmap([FromQuery] int months = 12)
    {
        if (!EnsureDbReady(out var err)) return err;
        try
        {
            var data = _charts.GetHeatmap(months);
            return Ok(new { success = true, data });
        }
        catch (Exception ex) { return Ok(new { success = false, error = ex.Message }); }
    }

    /// <summary>Plays by hour of day (0-23).</summary>
    [HttpGet("Charts/ByHour")]
    public ActionResult ChartsByHour([FromQuery] string window = "12m")
    {
        if (!EnsureDbReady(out var err)) return err;
        try
        {
            var win = TimeWindow.ParseCode(window);
            var data = _charts.GetByHour(win);
            return Ok(new { success = true, data });
        }
        catch (Exception ex) { return Ok(new { success = false, error = ex.Message }); }
    }

    /// <summary>Plays by day of week (0=Mon..6=Sun).</summary>
    [HttpGet("Charts/ByDayOfWeek")]
    public ActionResult ChartsByDayOfWeek([FromQuery] string window = "12m")
    {
        if (!EnsureDbReady(out var err)) return err;
        try
        {
            var win = TimeWindow.ParseCode(window);
            var data = _charts.GetByDayOfWeek(win);
            return Ok(new { success = true, data });
        }
        catch (Exception ex) { return Ok(new { success = false, error = ex.Message }); }
    }

    /// <summary>Top 10 genres + Others for treemap.</summary>
    [HttpGet("Charts/GenreTreemap")]
    public ActionResult ChartsGenreTreemap([FromQuery] string window = "12m")
    {
        if (!EnsureDbReady(out var err)) return err;
        try
        {
            var win = TimeWindow.ParseCode(window);
            var data = _charts.GetGenreTreemap(win);
            return Ok(new { success = true, data });
        }
        catch (Exception ex) { return Ok(new { success = false, error = ex.Message }); }
    }

    /// <summary>Artist bubbles: plays vs distinct songs vs duration.</summary>
    [HttpGet("Charts/ArtistBubbles")]
    public ActionResult ChartsArtistBubbles([FromQuery] string window = "12m")
    {
        if (!EnsureDbReady(out var err)) return err;
        try
        {
            var win = TimeWindow.ParseCode(window);
            var data = _charts.GetArtistBubbles(win);
            return Ok(new { success = true, data });
        }
        catch (Exception ex) { return Ok(new { success = false, error = ex.Message }); }
    }

    /// <summary>Current and longest consecutive-days streak.</summary>
    [HttpGet("Charts/Streak")]
    public ActionResult ChartsStreak()
    {
        if (!EnsureDbReady(out var err)) return err;
        try
        {
            var data = _charts.GetStreak();
            return Ok(new { success = true, data });
        }
        catch (Exception ex) { return Ok(new { success = false, error = ex.Message }); }
    }

    [HttpGet("Historical/Years")]
    public ActionResult GetHistoricalYears()
    {
        if (!EnsureDbReady(out var err)) return err;
        try
        {
            var years = _historical.GetAvailableYears();
            return Ok(new { success = true, years });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetHistoricalYears error");
            return Ok(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Returns the historical aggregates for a specific year (v0.0.0.9).
    /// Includes top songs, artists, genres, and albums for that year.
    /// </summary>
    [HttpGet("Historical/Year/{year}")]
    public ActionResult GetHistoricalYear(int year)
    {
        if (!EnsureDbReady(out var err)) return err;
        try
        {
            var data = _historical.GetYearSummary(year);
            return Ok(new { success = true, year, data });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetHistoricalYear error");
            return Ok(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Returns a comparison of all available years side by side (v0.0.0.9).
    /// Useful for the "Histórico" tab to show year-over-year evolution.
    /// </summary>
    [HttpGet("Historical/Compare")]
    public ActionResult GetHistoricalComparison()
    {
        if (!EnsureDbReady(out var err)) return err;
        try
        {
            var data = _historical.GetYearComparison();
            return Ok(new { success = true, data });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetHistoricalComparison error");
            return Ok(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Forces the generation of yearly historical aggregates from the main DB.
    /// Useful after importing data, so the "Histórico" tab has data without
    /// waiting for the monthly purge task to run.
    /// </summary>
    [HttpPost("Historical/Regenerate")]
    public ActionResult RegenerateHistorical()
    {
        if (!EnsureDbReady(out var err)) return err;
        try
        {
            _historical.RegenerateAggregates();
            var years = _historical.GetAvailableYears();
            return Ok(new { success = true, yearsGenerated = years, message = $"Histórico regenerado. Años disponibles: {string.Join(", ", years)}" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RegenerateHistorical error");
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
        sp.Genre = string.IsNullOrWhiteSpace(dto.Genre) ? null : dto.Genre.Trim();
        sp.Year = dto.Year > 0 ? dto.Year : null;
        sp.Limit = dto.Limit > 0 ? dto.Limit : 50;
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

    // ====== PUBLIC API (v0.0.0.9) ======
    // These endpoints are designed for external consumption (web pages, scripts, etc.).
    // They return read-only data in formats suitable for publishing.
    // NOTE: Jellyfin still enforces authentication on /Plugins/* routes by default.
    // For truly public access, configure a reverse proxy to bypass auth for these
    // specific paths, or use the /Public/Stats endpoint which returns a compact JSON.

    /// <summary>
    /// Returns a compact JSON snapshot of the user's listening stats, suitable for
    /// publishing on a web page. Includes: total plays, top 10 songs/artists/genres
    /// per window, and listening time by genre.
    /// </summary>
    [HttpGet("Public/Stats")]
    public ActionResult PublicStats()
    {
        if (!EnsureDbReady(out var err)) return err;
        try
        {
            var result = new Dictionary<string, object>();
            result["generatedAt"] = DateTime.UtcNow.ToString("o");
            result["serverTimeLocal"] = ServerClock.NowLocal().ToString("o");

            // Global counts
            using (var conn = _db.OpenMain())
            {
                result["totalPlays"] = Count(conn, "plays");
                result["totalTracks"] = Count(conn, "tracks");
                result["totalArtists"] = Count(conn, "track_artists", "DISTINCT artist");
                result["totalGenres"] = Count(conn, "track_genres", "DISTINCT genre");
            }

            // Top 10 per window per dimension
            var windows = new[] { QueryWindow.TwoWeeks, QueryWindow.OneMonth, QueryWindow.ThreeMonths, QueryWindow.SixMonths, QueryWindow.TwelveMonths, QueryWindow.LastYear };
            var topData = new Dictionary<string, object>();
            foreach (var w in windows)
            {
                var wCode = TimeWindow.Code(w);
                var wData = new Dictionary<string, object>();
                wData["label"] = TimeWindow.Label(w);
                wData["topSongs"] = _stats.Query(QueryDimension.Songs, QueryDirection.Top, w, 10);
                wData["topArtists"] = _stats.Query(QueryDimension.Artists, QueryDirection.Top, w, 10);
                wData["topGenres"] = _stats.Query(QueryDimension.Genres, QueryDirection.Top, w, 10);
                topData[wCode] = wData;
            }
            result["topByWindow"] = topData;

            // Listening time by genre per window
            result["listeningTimeByGenre"] = _stats.GetListeningTimeByGenrePerWindow();

            // Achievements summary
            var achievements = _achievements.GetAllAchievements();
            result["achievements"] = new
            {
                earned = achievements.Count(a => a.Earned),
                total = achievements.Count,
                earnedList = achievements.Where(a => a.Earned).Select(a => new { a.Id, a.Name, a.Category }).ToList()
            };

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PublicStats error");
            return Ok(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Returns an RSS 2.0 feed of the user's most recent "musical milestones":
    /// top songs per month for the last 12 months. Suitable for publishing
    /// in an RSS reader or embedding in a web page.
    /// </summary>
    [HttpGet("Public/Rss")]
    [Produces("application/rss+xml")]
    public ActionResult PublicRss()
    {
        if (!EnsureDbReady(out var err)) return err;
        try
        {
            var now = DateTime.UtcNow.ToString("R");
            var sb = new System.Text.StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.Append("<rss version=\"2.0\">");
            sb.Append("<channel>");
            sb.Append("<title>Estadísticas de Música - Jellyfin</title>");
            sb.Append("<description>Top de canciones más escuchadas por ventana temporal</description>");
            sb.Append("<link>").Append(Url.Action("PublicRss")?.Replace("/Public/Rss", "") ?? "").Append("</link>");
            sb.Append("<lastBuildDate>").Append(now).Append("</lastBuildDate>");
            sb.Append("<pubDate>").Append(now).Append("</pubDate>");

            var windows = new[] { QueryWindow.TwoWeeks, QueryWindow.OneMonth, QueryWindow.ThreeMonths, QueryWindow.SixMonths, QueryWindow.TwelveMonths, QueryWindow.LastYear };
            foreach (var w in windows)
            {
                var rows = _stats.Query(QueryDimension.Songs, QueryDirection.Top, w, 10);
                if (rows.Count == 0) continue;

                var label = TimeWindow.Label(w);
                var (start, end) = TimeWindow.GetRange(w);
                sb.Append("<item>");
                sb.Append("<title>Top 10 canciones — ").Append(EscapeXml(label)).Append("</title>");
                sb.Append("<description>Periodo: ").Append(EscapeXml(start.ToString("yyyy-MM-dd"))).Append(" a ").Append(EscapeXml(end.ToString("yyyy-MM-dd"))).Append("</description>");
                sb.Append("<pubDate>").Append(end.ToString("R")).Append("</pubDate>");
                sb.Append("<guid isPermaLink=\"false\">estadisticas-").Append(TimeWindow.Code(w)).Append("-").Append(end.ToString("yyyyMMdd")).Append("</guid>");

                var htmlList = new System.Text.StringBuilder();
                htmlList.Append("<![CDATA[<ol>");
                foreach (var r in rows)
                {
                    htmlList.Append("<li>").Append(EscapeHtml(r.Name));
                    if (!string.IsNullOrEmpty(r.Artist)) htmlList.Append(" — ").Append(EscapeHtml(r.Artist));
                    htmlList.Append(" (").Append(r.PlayCount).Append(" reproducciones)</li>");
                }
                htmlList.Append("</ol>]]></description>");
                sb.Append(htmlList);
                sb.Append("</item>");
            }

            sb.Append("</channel></rss>");
            return Content(sb.ToString(), "application/rss+xml", System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PublicRss error");
            return Content($"<rss><channel><title>Error</title><description>{EscapeXml(ex.Message)}</description></channel></rss>", "application/rss+xml");
        }
    }

    private static long Count(SqliteConnection conn, string table, string expr = "*")
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT({expr}) FROM {table};";
        return (long)cmd.ExecuteScalar();
    }

    private static string EscapeXml(string s) => s?.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;") ?? "";
    private static string EscapeHtml(string s) => s?.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;") ?? "";

    /// <summary>DTO for create/update scheduled playlist.</summary>
    public sealed class ScheduledPlaylistDto
    {
        public string? Name { get; set; }
        public string? JellyfinPlaylistId { get; set; }
        public string? QueryDimension { get; set; }
        public string? QueryDirection { get; set; }
        public string? QueryWindow { get; set; }
        public string? Genre { get; set; }
        public int? Year { get; set; }
        public int Limit { get; set; }
        public string? Frequency { get; set; }
        public string? TimeOfDay { get; set; }
        public int? DayOfWeek { get; set; }
        public int? DayOfMonth { get; set; }
        public string? MonthAndDay { get; set; }
        public bool? Enabled { get; set; }
    }
}
