using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Estadisticas.Data;
using Jellyfin.Plugin.Estadisticas.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Estadisticas.Services;

/// <summary>
/// Manages the `scheduled_playlists` table: CRUD + due-time evaluation.
///
/// The actual publishing is done by <see cref="PlaylistPublisherService"/>; this service
/// only decides WHICH entries are due and WHICH ItemIds to put in them.
///
/// Schedule semantics (server local time, NOT UTC):
/// - Daily:    every day at TimeOfDay
/// - Weekly:   on DayOfWeek (0=Mon..6=Sun) at TimeOfDay
/// - Monthly:  on DayOfMonth (1..28; 29-31 are clamped to 28 to avoid skipped months) at TimeOfDay
/// - Yearly:   on MonthAndDay (MM-DD) at TimeOfDay
///
/// next_run is computed in UTC for easy comparison with DateTime.UtcNow.
/// </summary>
public sealed class PlaylistSchedulerService
{
    private readonly SqliteDb _db;
    private readonly ILogger<PlaylistSchedulerService> _logger;
    private readonly StatisticsService _stats;
    private readonly PlaylistPublisherService _publisher;
    private readonly Func<Guid> _adminUserIdProvider;

    public PlaylistSchedulerService(
        SqliteDb db,
        ILogger<PlaylistSchedulerService> logger,
        StatisticsService stats,
        PlaylistPublisherService publisher,
        Func<Guid> adminUserIdProvider)
    {
        _db = db;
        _logger = logger;
        _stats = stats;
        _publisher = publisher;
        _adminUserIdProvider = adminUserIdProvider;
    }

    /// <summary>Lists all scheduled playlists, ordered by name.</summary>
    public List<ScheduledPlaylist> ListAll()
    {
        var list = new List<ScheduledPlaylist>();
        using var conn = _db.OpenMain();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM scheduled_playlists ORDER BY name COLLATE NOCASE;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(MapReader(reader));
        return list;
    }

    /// <summary>Get one by id. Null if not found.</summary>
    public ScheduledPlaylist? GetById(long id)
    {
        using var conn = _db.OpenMain();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM scheduled_playlists WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? MapReader(reader) : null;
    }

    /// <summary>Insert a new scheduled playlist. Returns the new row id.</summary>
    public long Insert(ScheduledPlaylist sp)
    {
        sp.CreatedAt = DateTime.UtcNow.ToString("o");
        sp.UpdatedAt = sp.CreatedAt;
        sp.NextRun = ComputeNextRunUtc(sp);

        using var conn = _db.OpenMain();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT INTO scheduled_playlists
                (name, jellyfin_playlist_id, query_dimension, query_direction, query_window, genre, playlist_limit,
                 frequency, time_of_day, day_of_week, day_of_month, month_and_day,
                 enabled, last_run, next_run, created_at, updated_at)
            VALUES
                (@name, @jpl, @qd, @qdir, @qw, @genre, @limit,
                 @freq, @tod, @dow, @dom, @md,
                 @en, @lr, @nr, @ca, @ua);
            SELECT last_insert_rowid();";
        BindScheduled(cmd, sp);
        var id = (long)cmd.ExecuteScalar()!;
        tx.Commit();
        return id;
    }

    /// <summary>Update an existing scheduled playlist.</summary>
    public void Update(ScheduledPlaylist sp)
    {
        sp.UpdatedAt = DateTime.UtcNow.ToString("o");
        sp.NextRun = ComputeNextRunUtc(sp);

        using var conn = _db.OpenMain();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            UPDATE scheduled_playlists SET
                name = @name,
                jellyfin_playlist_id = @jpl,
                query_dimension = @qd,
                query_direction = @qdir,
                query_window = @qw,
                genre = @genre,
                playlist_limit = @limit,
                frequency = @freq,
                time_of_day = @tod,
                day_of_week = @dow,
                day_of_month = @dom,
                month_and_day = @md,
                enabled = @en,
                last_run = @lr,
                next_run = @nr,
                updated_at = @ua
            WHERE id = @id;";
        BindScheduled(cmd, sp);
        cmd.Parameters.AddWithValue("@id", sp.Id);
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    /// <summary>Delete a scheduled playlist by id. Does NOT delete the Jellyfin playlist itself.</summary>
    public void Delete(long id)
    {
        using var conn = _db.OpenMain();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM scheduled_playlists WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Find all entries whose NextRun is in the past (or null) and that are enabled.
    /// Called by <see cref="Tasks.PublishScheduledPlaylistsTask"/>.
    /// </summary>
    public List<ScheduledPlaylist> GetDue()
    {
        var nowUtc = DateTime.UtcNow.ToString("o");
        var list = new List<ScheduledPlaylist>();
        using var conn = _db.OpenMain();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT * FROM scheduled_playlists
            WHERE enabled = 1 AND (next_run IS NULL OR next_run <= @now)
            ORDER BY next_run NULLS FIRST;";
        cmd.Parameters.AddWithValue("@now", nowUtc);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(MapReader(reader));
        return list;
    }

    /// <summary>
    /// Run a single scheduled playlist now: query stats, publish to Jellyfin, update last_run + next_run.
    /// </summary>
    public async Task RunOnceAsync(ScheduledPlaylist sp, CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("Running scheduled playlist '{Name}' (id={Id})", sp.Name, sp.Id);

            var dim = Enum.Parse<QueryDimension>(sp.QueryDimension, true);
            var dir = Enum.Parse<QueryDirection>(sp.QueryDirection, true);
            var win = TimeWindow.ParseCode(sp.QueryWindow);

            // v0.0.0.7: scheduled lists are song-based. Dimension=Genres uses the
            // configured genre filter (e.g. "Top 50 de Rock"). Artists/Albums are
            // legacy (kept for old configurations, no longer offered in the UI).
            List<string> itemIds;
            if (dim == QueryDimension.Artists || dim == QueryDimension.Albums)
            {
                itemIds = _stats.GetItemIdsForPlaylist(dim, dir, win, sp.Limit);
            }
            else
            {
                var ascending = dir == QueryDirection.Bottom;
                itemIds = _stats.GetScheduledItemIds(dim, win, sp.Limit, ascending, sp.Genre);
            }
            var adminId = _adminUserIdProvider();

            var playlistId = await _publisher.PublishAsync(
                sp.Name, sp.JellyfinPlaylistId, itemIds, adminId).ConfigureAwait(false);

            sp.JellyfinPlaylistId = playlistId;
            sp.LastRun = DateTime.UtcNow.ToString("o");
            sp.NextRun = ComputeNextRunUtc(sp);
            Update(sp);

            _logger.LogInformation("Scheduled playlist '{Name}' published with {Count} items (playlist={Pid})",
                sp.Name, itemIds.Count, playlistId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error running scheduled playlist '{Name}' (id={Id})", sp.Name, sp.Id);
            // Still update last_run + next_run so we don't retry immediately on every tick
            sp.LastRun = DateTime.UtcNow.ToString("o");
            sp.NextRun = ComputeNextRunUtc(sp);
            Update(sp);
        }
    }

    /// <summary>
    /// Compute the next run time (UTC) for a scheduled playlist based on its frequency
    /// and the current server local time. Returns null if frequency is invalid.
    /// </summary>
    internal static string? ComputeNextRunUtc(ScheduledPlaylist sp)
    {
        if (!TimeSpan.TryParse(sp.TimeOfDay, out var timeOfDay)) return null;
        var nowLocal = DateTime.Now;
        var todayTarget = nowLocal.Date.Add(timeOfDay);

        DateTime nextLocal;
        switch (Enum.Parse<ScheduleFrequency>(sp.Frequency, true))
        {
            case ScheduleFrequency.Daily:
                nextLocal = todayTarget <= nowLocal ? todayTarget.AddDays(1) : todayTarget;
                break;

            case ScheduleFrequency.Weekly:
                if (!sp.DayOfWeek.HasValue) return null;
                var targetDow = sp.DayOfWeek.Value; // 0=Mon..6=Sun
                var currentDow = ((int)nowLocal.DayOfWeek + 6) % 7; // 0=Mon..6=Sun
                var daysToAdd = (targetDow - currentDow + 7) % 7;
                nextLocal = nowLocal.Date.AddDays(daysToAdd).Add(timeOfDay);
                if (nextLocal <= nowLocal) nextLocal = nextLocal.AddDays(7);
                break;

            case ScheduleFrequency.Monthly:
                var dom = Math.Clamp(sp.DayOfMonth ?? 1, 1, 28);
                var candidateMonth = nowLocal.Month;
                var candidateYear = nowLocal.Year;
                DateTime candidate;
                try
                {
                    candidate = new DateTime(candidateYear, candidateMonth, dom, timeOfDay.Hours, timeOfDay.Minutes, 0);
                }
                catch
                {
                    candidate = new DateTime(candidateYear, candidateMonth, 28, timeOfDay.Hours, timeOfDay.Minutes, 0);
                }
                if (candidate <= nowLocal)
                {
                    var nextMonth = candidate.AddMonths(1);
                    candidate = new DateTime(nextMonth.Year, nextMonth.Month, Math.Min(dom, DateTime.DaysInMonth(nextMonth.Year, nextMonth.Month)), timeOfDay.Hours, timeOfDay.Minutes, 0);
                }
                nextLocal = candidate;
                break;

            case ScheduleFrequency.Yearly:
                if (string.IsNullOrWhiteSpace(sp.MonthAndDay) || sp.MonthAndDay.Length != 5) return null;
                var parts = sp.MonthAndDay.Split('-');
                if (parts.Length != 2 || !int.TryParse(parts[0], out var mm) || !int.TryParse(parts[1], out var dd)) return null;
                mm = Math.Clamp(mm, 1, 12);
                dd = Math.Clamp(dd, 1, DateTime.DaysInMonth(nowLocal.Year, mm));
                try
                {
                    candidate = new DateTime(nowLocal.Year, mm, dd, timeOfDay.Hours, timeOfDay.Minutes, 0);
                }
                catch
                {
                    return null;
                }
                if (candidate <= nowLocal) candidate = candidate.AddYears(1);
                nextLocal = candidate;
                break;

            default:
                return null;
        }

        // Convert local to UTC for storage/comparison
        var utc = DateTime.SpecifyKind(nextLocal, DateTimeKind.Local).ToUniversalTime();
        return utc.ToString("o");
    }

    private static ScheduledPlaylist MapReader(SqliteDataReader r)
    {
        return new ScheduledPlaylist
        {
            Id = r.GetInt64(r.GetOrdinal("id")),
            Name = r.GetString(r.GetOrdinal("name")),
            JellyfinPlaylistId = r.IsDBNull(r.GetOrdinal("jellyfin_playlist_id")) ? null : r.GetString(r.GetOrdinal("jellyfin_playlist_id")),
            QueryDimension = r.GetString(r.GetOrdinal("query_dimension")),
            QueryDirection = r.GetString(r.GetOrdinal("query_direction")),
            QueryWindow = r.GetString(r.GetOrdinal("query_window")),
            Genre = r.IsDBNull(r.GetOrdinal("genre")) ? null : r.GetString(r.GetOrdinal("genre")),
            Limit = r.GetInt32(r.GetOrdinal("playlist_limit")),
            Frequency = r.GetString(r.GetOrdinal("frequency")),
            TimeOfDay = r.GetString(r.GetOrdinal("time_of_day")),
            DayOfWeek = r.IsDBNull(r.GetOrdinal("day_of_week")) ? null : r.GetInt32(r.GetOrdinal("day_of_week")),
            DayOfMonth = r.IsDBNull(r.GetOrdinal("day_of_month")) ? null : r.GetInt32(r.GetOrdinal("day_of_month")),
            MonthAndDay = r.IsDBNull(r.GetOrdinal("month_and_day")) ? null : r.GetString(r.GetOrdinal("month_and_day")),
            Enabled = r.GetInt64(r.GetOrdinal("enabled")) != 0,
            LastRun = r.IsDBNull(r.GetOrdinal("last_run")) ? null : r.GetString(r.GetOrdinal("last_run")),
            NextRun = r.IsDBNull(r.GetOrdinal("next_run")) ? null : r.GetString(r.GetOrdinal("next_run")),
            CreatedAt = r.GetString(r.GetOrdinal("created_at")),
            UpdatedAt = r.GetString(r.GetOrdinal("updated_at"))
        };
    }

    private static void BindScheduled(SqliteCommand cmd, ScheduledPlaylist sp)
    {
        cmd.Parameters.AddWithValue("@name", sp.Name);
        cmd.Parameters.AddWithValue("@jpl", (object?)sp.JellyfinPlaylistId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@qd", sp.QueryDimension);
        cmd.Parameters.AddWithValue("@qdir", sp.QueryDirection);
        cmd.Parameters.AddWithValue("@qw", sp.QueryWindow);
        cmd.Parameters.AddWithValue("@genre", (object?)sp.Genre ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@limit", sp.Limit);
        cmd.Parameters.AddWithValue("@freq", sp.Frequency);
        cmd.Parameters.AddWithValue("@tod", sp.TimeOfDay);
        cmd.Parameters.AddWithValue("@dow", (object?)sp.DayOfWeek ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@dom", (object?)sp.DayOfMonth ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@md", (object?)sp.MonthAndDay ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@en", sp.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@lr", (object?)sp.LastRun ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@nr", (object?)sp.NextRun ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ca", sp.CreatedAt);
        cmd.Parameters.AddWithValue("@ua", sp.UpdatedAt);
    }
}
