using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Estadisticas.Data;
using Jellyfin.Plugin.Estadisticas.Models;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.Estadisticas.Services;

/// <summary>
/// Background hosted service that subscribes to Jellyfin playback events and records
/// each valid play (after the 20-second threshold) into the plugin's own SQLite database.
///
/// Only audio items are tracked. The plugin never writes to Jellyfin's database.
///
/// Tracking logic:
/// - On PlaybackStart: create a per-session tracker (keyed by user+item).
/// - On PlaybackProgress: if position ticks &gt;= 20s AND not already recorded for this session,
///   insert a row into `plays`, mark the tracker as recorded.
/// - On PlaybackStop: clean up the tracker.
///
/// A "session" here is a single user+item pair; if the same user plays the same song twice
/// (back-to-back), each play counts separately because the tracker is reset on stop.
/// </summary>
public sealed class PlaybackTrackerService : IHostedService, IDisposable
{
    /// <summary>20 seconds in ticks (1 tick = 100 ns = 1e-7 s).</summary>
    private const long ScrobbleThresholdTicks = 20L * TimeSpan.TicksPerSecond;

    private readonly ILogger<PlaybackTrackerService> _logger;
    private readonly ISessionManager _sessionManager;
    private readonly SqliteDb _db;
    private readonly ILibraryManager _libraryManager;

    /// <summary>Key = "userId|itemId"; value = tracker state.</summary>
    private readonly ConcurrentDictionary<string, PlaybackTracker> _trackers = new();

    public PlaybackTrackerService(
        ILogger<PlaybackTrackerService> logger,
        ISessionManager sessionManager,
        SqliteDb db,
        ILibraryManager libraryManager)
    {
        _logger = logger;
        _sessionManager = sessionManager;
        _db = db;
        _libraryManager = libraryManager;
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart += OnPlaybackStart;
        _sessionManager.PlaybackProgress += OnPlaybackProgress;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        _logger.LogInformation("PlaybackTrackerService started (scrobble threshold = 20s, audio-only)");
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        _trackers.Clear();
        _logger.LogInformation("PlaybackTrackerService stopped");
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _trackers.Clear();
        GC.SuppressFinalize(this);
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        try
        {
            if (e.Item == null || e.Users == null || e.Users.Count == 0) return;

            // Filter: only Audio items (no video, no books, etc.)
            if (!IsAudioItem(e.Item)) return;

            var user = e.Users[0];
            var key = TrackerKey(user.Id.ToString(), e.Item.Id.ToString());
            var tracker = new PlaybackTracker
            {
                UserId = user.Id.ToString(),
                ItemId = e.Item.Id.ToString(),
                Client = e.DeviceName, // best-effort; SessionManager enriches this
                Device = e.DeviceName,
                Recorded = false
            };

            _trackers[key] = tracker;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OnPlaybackStart error");
        }
    }

    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
    {
        try
        {
            if (e.Item == null || e.Users == null || e.Users.Count == 0) return;
            if (!IsAudioItem(e.Item)) return;

            var user = e.Users[0];
            var key = TrackerKey(user.Id.ToString(), e.Item.Id.ToString());
            if (!_trackers.TryGetValue(key, out var tracker)) return;
            if (tracker.Recorded) return;

            var positionTicks = e.PlaybackPositionTicks ?? 0;
            if (positionTicks < ScrobbleThresholdTicks) return;

            // Threshold met: record the play.
            tracker.Recorded = true;
            RecordPlay(tracker, e.Item, positionTicks);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OnPlaybackProgress error");
        }
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        try
        {
            if (e.Item == null || e.Users == null || e.Users.Count == 0) return;
            if (!IsAudioItem(e.Item)) return;

            var user = e.Users[0];
            var key = TrackerKey(user.Id.ToString(), e.Item.Id.ToString());

            if (_trackers.TryRemove(key, out var tracker))
            {
                // If threshold was reached during progress, it's already recorded.
                // Otherwise, check final position; if it crossed the threshold, record now.
                if (!tracker.Recorded)
                {
                    var positionTicks = e.PlaybackPositionTicks ?? 0;
                    if (positionTicks >= ScrobbleThresholdTicks)
                    {
                        RecordPlay(tracker, e.Item, positionTicks);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OnPlaybackStopped error");
        }
    }

    /// <summary>
    /// Inserts a play row + upserts the track metadata. Single transaction.
    /// Uses INSERT OR IGNORE for the track (since it might already exist from a previous play)
    /// and INSERT OR REPLACE for the artists/genres (to refresh on metadata changes).
    /// </summary>
    private void RecordPlay(PlaybackTracker tracker, MediaBrowser.Controller.Entities.BaseItem item, long positionTicks)
    {
        try
        {
            // Resolve audio metadata
            var audioItem = item as MediaBrowser.Controller.Entities.Audio.Audio;
            var name = item.Name ?? "(unknown)";
            // AlbumArtist is on MusicAlbum (parent); AlbumArtists is IReadOnlyList<string> on Audio itself.
            var albumArtistsList = audioItem?.AlbumArtists;
            var albumArtist = audioItem?.AlbumEntity?.AlbumArtist ??
                              (albumArtistsList != null && albumArtistsList.Count > 0 ? albumArtistsList[0] : null);
            var albumName = audioItem?.AlbumEntity?.Name ?? audioItem?.Album;
            var durationMs = item.RunTimeTicks.HasValue ? (long)(item.RunTimeTicks.Value / TimeSpan.TicksPerMillisecond) : (long?)null;
            var filePath = item.Path;
            // Year: capture from ProductionYear (year the song/album was released).
            var year = item.ProductionYear;
            // Cache Jellyfin ItemIds for album and artist (for cover art in Portadas tab)
            var albumItemId = audioItem?.AlbumEntity?.Id.ToString() ?? null;
            var artistItemId = audioItem?.AlbumEntity?.MusicArtist?.Id.ToString() ?? null;

            // Multi-valued artists and genres
            var artists = audioItem?.Artists?.ToList() ?? new();
            // Fallback: if Artists is empty but AlbumArtist is set, use it
            if (artists.Count == 0 && !string.IsNullOrWhiteSpace(albumArtist))
            {
                artists.Add(albumArtist);
            }

            var genres = item.Genres?.ToList() ?? new();

            var nowUtc = DateTime.UtcNow.ToString("o");
            var playedAt = nowUtc;

            using var conn = _db.OpenMain();
            using var tx = conn.BeginTransaction();

            // Upsert track
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    INSERT INTO tracks (item_id, name, album_artist, album_name, duration_ms, file_path, year, album_item_id, artist_item_id, first_seen, last_updated)
                    VALUES (@id, @name, @aa, @an, @dur, @fp, @year, @aitemid, @artitemid, @now, @now)
                    ON CONFLICT(item_id) DO UPDATE SET
                        name = excluded.name,
                        album_artist = excluded.album_artist,
                        album_name = excluded.album_name,
                        duration_ms = excluded.duration_ms,
                        file_path = excluded.file_path,
                        year = excluded.year,
                        album_item_id = COALESCE(excluded.album_item_id, tracks.album_item_id),
                        artist_item_id = COALESCE(excluded.artist_item_id, tracks.artist_item_id),
                        last_updated = excluded.last_updated;";
                AddParam(cmd, "@id", tracker.ItemId);
                AddParam(cmd, "@name", name);
                AddParam(cmd, "@aa", (object?)albumArtist ?? DBNull.Value);
                AddParam(cmd, "@an", (object?)albumName ?? DBNull.Value);
                AddParam(cmd, "@dur", (object?)durationMs ?? DBNull.Value);
                AddParam(cmd, "@fp", (object?)filePath ?? DBNull.Value);
                AddParam(cmd, "@year", (object?)year ?? DBNull.Value);
                AddParam(cmd, "@aitemid", (object?)albumItemId ?? DBNull.Value);
                AddParam(cmd, "@artitemid", (object?)artistItemId ?? DBNull.Value);
                AddParam(cmd, "@now", nowUtc);
                cmd.ExecuteNonQuery();
            }

            // Refresh artists: delete + re-insert (cheap, ensures correctness on metadata changes)
            using (var delCmd = conn.CreateCommand())
            {
                delCmd.Transaction = tx;
                delCmd.CommandText = "DELETE FROM track_artists WHERE item_id = @id;";
                AddParam(delCmd, "@id", tracker.ItemId);
                delCmd.ExecuteNonQuery();
            }
            foreach (var artist in artists.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                using var ins = conn.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = "INSERT OR IGNORE INTO track_artists (item_id, artist) VALUES (@id, @a);";
                AddParam(ins, "@id", tracker.ItemId);
                AddParam(ins, "@a", artist);
                ins.ExecuteNonQuery();
            }

            // Refresh genres
            using (var delCmd = conn.CreateCommand())
            {
                delCmd.Transaction = tx;
                delCmd.CommandText = "DELETE FROM track_genres WHERE item_id = @id;";
                AddParam(delCmd, "@id", tracker.ItemId);
                delCmd.ExecuteNonQuery();
            }
            foreach (var genre in genres.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                using var ins = conn.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = "INSERT OR IGNORE INTO track_genres (item_id, genre) VALUES (@id, @g);";
                AddParam(ins, "@id", tracker.ItemId);
                AddParam(ins, "@g", genre);
                ins.ExecuteNonQuery();
            }

            // Insert play row.
            // NOTE: We do NOT save position_ms (how long the user actually listened).
            // For future "total listening time" queries, we approximate by summing
            // the track's full duration_ms (already captured in the tracks table).
            // This avoids an extra column per play and keeps writes minimal —
            // important on USB storage to reduce wear-leveling pressure.
            using (var playCmd = conn.CreateCommand())
            {
                playCmd.Transaction = tx;
                playCmd.CommandText = @"
                    INSERT INTO plays (item_id, played_at, user_id, client, device)
                    VALUES (@id, @at, @u, @c, @d);";
                AddParam(playCmd, "@id", tracker.ItemId);
                AddParam(playCmd, "@at", playedAt);
                AddParam(playCmd, "@u", tracker.UserId);
                AddParam(playCmd, "@c", (object?)tracker.Client ?? DBNull.Value);
                AddParam(playCmd, "@d", (object?)tracker.Device ?? DBNull.Value);
                playCmd.ExecuteNonQuery();
            }

            tx.Commit();

            _logger.LogInformation(
                "Recorded play: {Song} | {Artists} | {Album} | client={Client} | pos={PosSec:F1}s",
                name, string.Join("; ", artists), albumName, tracker.Client ?? "?",
                positionTicks / (double)TimeSpan.TicksPerSecond);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RecordPlay error for item {ItemId}", tracker.ItemId);
        }
    }

    private static bool IsAudioItem(MediaBrowser.Controller.Entities.BaseItem item)
    {
        // Audio item class lives in MediaBrowser.Controller.Entities.Audio.
        // We accept any Audio-derived item (Audio, AudioBook, etc.) as long as it's audio-only.
        return item is MediaBrowser.Controller.Entities.Audio.Audio;
    }

    private static string TrackerKey(string userId, string itemId) => $"{userId}|{itemId}";

    private static void AddParam(SqliteCommand cmd, string name, object value)
    {
        cmd.Parameters.AddWithValue(name, value);
    }

    private sealed class PlaybackTracker
    {
        public string UserId { get; set; } = string.Empty;
        public string ItemId { get; set; } = string.Empty;
        public string? Client { get; set; }
        public string? Device { get; set; }
        public bool Recorded { get; set; }
    }
}
