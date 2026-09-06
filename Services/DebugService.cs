using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Estadisticas.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Estadisticas.Services;

/// <summary>
/// Provides diagnostic and test-data-seeding capabilities for the plugin.
///
/// Designed for development and testing: lets you verify the plugin is working
/// WITHOUT having to wait 2 weeks for the smallest time window to fill up with
/// real plays.
///
/// SeedTestData() inserts a configurable number of synthetic plays distributed
/// across all 6 time windows (2w, 1m, 3m, 6m, 12m, last_year) so that every
/// Top 25 / Bottom 25 query returns realistic results immediately.
///
/// All seeded tracks use synthetic ItemIds (Guid.NewGuid().ToString()) that
/// do NOT exist in Jellyfin's library, so creating playlists from them will
/// yield empty playlists (no items resolve). This is intentional: the seed
/// is for testing the statistics queries, not the playlist publishing.
/// </summary>
public sealed class DebugService
{
    private readonly SqliteDb _db;
    private readonly ILogger<DebugService> _logger;

    public DebugService(SqliteDb db, ILogger<DebugService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Returns a diagnostic snapshot of the plugin's DB state.
    /// Useful to confirm the plugin is actually capturing plays.
    /// </summary>
    public Dictionary<string, object> GetStatus()
    {
        var result = new Dictionary<string, object>
        {
            ["dbInitialized"] = _db.IsInitialized,
            ["serverTimeUtc"] = DateTime.UtcNow.ToString("o"),
            ["serverTimeLocal"] = DateTime.Now.ToString("o"),
        };

        if (!_db.IsInitialized)
        {
            result["error"] = "SQLite DB not initialized. Check Jellyfin logs for the original error.";
            return result;
        }

        try
        {
            using var conn = _db.OpenMain();
            result["tracksCount"] = Count(conn, "tracks");
            result["trackArtistsCount"] = Count(conn, "track_artists");
            result["trackGenresCount"] = Count(conn, "track_genres");
            result["playsCount"] = Count(conn, "plays");
            result["scheduledPlaylistsCount"] = Count(conn, "scheduled_playlists");

            // Earliest and latest play
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT MIN(played_at), MAX(played_at) FROM plays;";
                using var r = cmd.ExecuteReader();
                if (r.Read())
                {
                    result["earliestPlay"] = r.IsDBNull(0) ? null : r.GetString(0);
                    result["latestPlay"] = r.IsDBNull(1) ? null : r.GetString(1);
                }
            }

            // Per-window counts
            var windowCounts = new Dictionary<string, long>();
            foreach (var w in new[] { "2w", "1m", "3m", "6m", "12m", "last_year" })
            {
                try
                {
                    var win = Models.TimeWindow.ParseCode(w);
                    var (start, end) = Models.TimeWindow.GetRange(win);
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT COUNT(*) FROM plays WHERE played_at >= @s AND played_at < @e;";
                    cmd.Parameters.AddWithValue("@s", start.ToString("o"));
                    cmd.Parameters.AddWithValue("@e", end.ToString("o"));
                    windowCounts[w] = (long)cmd.ExecuteScalar()!;
                }
                catch
                {
                    windowCounts[w] = -1;
                }
            }
            result["playsPerWindow"] = windowCounts;
        }
        catch (Exception ex)
        {
            result["error"] = "Failed to read DB: " + ex.Message;
        }

        return result;
    }

    /// <summary>
    /// Inserts synthetic plays spread across all 6 time windows. Returns a summary.
    /// Default: 200 synthetic tracks, 1500 plays distributed so each window has ~200.
    /// </summary>
    public Dictionary<string, object> SeedTestData(int trackCount = 50, int playsPerTrack = 30)
    {
        var result = new Dictionary<string, object>();

        if (!_db.IsInitialized)
        {
            result["success"] = false;
            result["error"] = "SQLite DB not initialized. Cannot seed.";
            return result;
        }

        try
        {
            // Synthetic genre/artist pool
            var genres = new[] { "Rock", "Metal", "Jazz", "Electronic", "Hip-Hop", "Classical", "Reggae", "Pop" };
            var artists = new[] { "The Testers", "Mock Band", "Synthetic Sound", "Demo Crew", "Fake Artists", "Placeholder Project" };
            var albums = new[] { "Test Album Vol 1", "Mock Sessions", "Synthetic Dreams", "Demo Collection", "Placeholder LP" };
            var clients = new[] { "Jellyfin Web", "Finamp", "Sonixd", "Feishin" };

            var rnd = new Random(42); // deterministic seed for reproducibility
            var now = DateTime.UtcNow;
            var tracks = new List<(string itemId, string name, string artist, string album, string genre, long durationMs)>();

            using (var conn = _db.OpenMain())
            {
                using var tx = conn.BeginTransaction();

                // Insert tracks
                for (int i = 0; i < trackCount; i++)
                {
                    var itemId = Guid.NewGuid().ToString();
                    var name = $"Test Song {i + 1:D3}";
                    var artist = artists[rnd.Next(artists.Length)];
                    var album = albums[rnd.Next(albums.Length)];
                    var genre = genres[rnd.Next(genres.Length)];
                    var dur = 180_000L + rnd.Next(120_000); // 3-5 min in ms

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = @"INSERT INTO tracks (item_id, name, album_artist, album_name, duration_ms, file_path, first_seen, last_updated)
                            VALUES (@id, @n, @aa, @an, @d, @fp, @now, @now);";
                        cmd.Parameters.AddWithValue("@id", itemId);
                        cmd.Parameters.AddWithValue("@n", name);
                        cmd.Parameters.AddWithValue("@aa", artist);
                        cmd.Parameters.AddWithValue("@an", album);
                        cmd.Parameters.AddWithValue("@d", dur);
                        cmd.Parameters.AddWithValue("@fp", $"/fake/path/{itemId}.mp3");
                        cmd.Parameters.AddWithValue("@now", now.ToString("o"));
                        cmd.ExecuteNonQuery();
                    }

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = "INSERT OR IGNORE INTO track_artists (item_id, artist) VALUES (@id, @a);";
                        cmd.Parameters.AddWithValue("@id", itemId);
                        cmd.Parameters.AddWithValue("@a", artist);
                        cmd.ExecuteNonQuery();
                    }

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = "INSERT OR IGNORE INTO track_genres (item_id, genre) VALUES (@id, @g);";
                        cmd.Parameters.AddWithValue("@id", itemId);
                        cmd.Parameters.AddWithValue("@g", genre);
                        cmd.ExecuteNonQuery();
                    }

                    tracks.Add((itemId, name, artist, album, genre, dur));
                }

                tx.Commit();
            }

            // Insert plays distributed across all 6 time windows
            // Strategy: for each track, distribute playsPerTrack plays so that:
            //   - 10% in last 2 weeks (must be in complete weeks, not current week)
            //   - 15% in last month (excluding current month)
            //   - 20% in last 3 months (excluding current month)
            //   - 20% in last 6 months (excluding current month)
            //   - 20% in last 12 months (excluding current month)
            //   - 15% in last year (excluding current year)
            long totalPlays = 0;
            using (var conn = _db.OpenMain())
            {
                using var tx = conn.BeginTransaction();

                foreach (var t in tracks)
                {
                    for (int p = 0; p < playsPerTrack; p++)
                    {
                        DateTime playedAt = PickDateForPlay(rnd, now, p, playsPerTrack);

                        using var cmd = conn.CreateCommand();
                        cmd.Transaction = tx;
                        cmd.CommandText = @"INSERT INTO plays (item_id, played_at, user_id, client, device)
                            VALUES (@id, @at, @u, @c, @d);";
                        cmd.Parameters.AddWithValue("@id", t.itemId);
                        cmd.Parameters.AddWithValue("@at", playedAt.ToString("o"));
                        cmd.Parameters.AddWithValue("@u", "debug-seed-user");
                        cmd.Parameters.AddWithValue("@c", clients[rnd.Next(clients.Length)]);
                        cmd.Parameters.AddWithValue("@d", "DebugSeeder");
                        cmd.ExecuteNonQuery();
                        totalPlays++;
                    }
                }

                tx.Commit();
            }

            result["success"] = true;
            result["tracksInserted"] = trackCount;
            result["playsInserted"] = totalPlays;
            result["note"] = "Synthetic data inserted. Use the Top 25 / Bottom 25 tabs to query. " +
                             "NOTE: track ItemIds are synthetic Guids that do NOT exist in Jellyfin, " +
                             "so creating playlists from this data will yield empty playlists.";
            _logger.LogInformation("Seeded {Tracks} tracks and {Plays} plays for testing", trackCount, totalPlays);
        }
        catch (Exception ex)
        {
            result["success"] = false;
            result["error"] = ex.Message;
            _logger.LogError(ex, "SeedTestData failed");
        }

        return result;
    }

    /// <summary>
    /// Picks a UTC DateTime for a synthetic play such that plays are distributed
    /// across all 6 query windows. The bucket is chosen based on (p / total) so
    /// the distribution is deterministic and proportional.
    /// </summary>
    private static DateTime PickDateForPlay(Random rnd, DateTime nowUtc, int p, int total)
    {
        double frac = (double)p / Math.Max(1, total);
        DateTime result;

        if (frac < 0.10)
        {
            // Last 2 complete weeks (Mon-Sun), excluding current week
            var daysSinceMonday = ((int)nowUtc.DayOfWeek + 6) % 7;
            var thisMonday = nowUtc.Date.AddDays(-daysSinceMonday);
            var endExclusive = thisMonday;
            var startInclusive = endExclusive.AddDays(-14);
            var span = (endExclusive - startInclusive).TotalSeconds;
            result = startInclusive.AddSeconds(rnd.NextDouble() * span);
        }
        else if (frac < 0.25)
        {
            // Last complete calendar month
            var firstOfThisMonth = new DateTime(nowUtc.Year, nowUtc.Month, 1);
            var endExclusive = firstOfThisMonth;
            var startInclusive = endExclusive.AddMonths(-1);
            var span = (endExclusive - startInclusive).TotalSeconds;
            result = startInclusive.AddSeconds(rnd.NextDouble() * span);
        }
        else if (frac < 0.45)
        {
            // Last 3 complete calendar months
            var firstOfThisMonth = new DateTime(nowUtc.Year, nowUtc.Month, 1);
            var endExclusive = firstOfThisMonth;
            var startInclusive = endExclusive.AddMonths(-3);
            var span = (endExclusive - startInclusive).TotalSeconds;
            result = startInclusive.AddSeconds(rnd.NextDouble() * span);
        }
        else if (frac < 0.65)
        {
            // Last 6 complete calendar months
            var firstOfThisMonth = new DateTime(nowUtc.Year, nowUtc.Month, 1);
            var endExclusive = firstOfThisMonth;
            var startInclusive = endExclusive.AddMonths(-6);
            var span = (endExclusive - startInclusive).TotalSeconds;
            result = startInclusive.AddSeconds(rnd.NextDouble() * span);
        }
        else if (frac < 0.85)
        {
            // Last 12 complete calendar months
            var firstOfThisMonth = new DateTime(nowUtc.Year, nowUtc.Month, 1);
            var endExclusive = firstOfThisMonth;
            var startInclusive = endExclusive.AddMonths(-12);
            var span = (endExclusive - startInclusive).TotalSeconds;
            result = startInclusive.AddSeconds(rnd.NextDouble() * span);
        }
        else
        {
            // Last complete calendar year
            var firstOfThisYear = new DateTime(nowUtc.Year, 1, 1);
            var endExclusive = firstOfThisYear;
            var startInclusive = endExclusive.AddYears(-1);
            var span = (endExclusive - startInclusive).TotalSeconds;
            result = startInclusive.AddSeconds(rnd.NextDouble() * span);
        }

        return DateTime.SpecifyKind(result, DateTimeKind.Utc);
    }

    /// <summary>
    /// Wipes all plays, track_artists, track_genres and tracks from the main DB.
    /// Scheduled playlists are KEPT (the user may have configured real ones).
    /// Returns the number of rows deleted.
    /// </summary>
    public Dictionary<string, object> ClearAllPlays()
    {
        var result = new Dictionary<string, object>();

        if (!_db.IsInitialized)
        {
            result["success"] = false;
            result["error"] = "SQLite DB not initialized.";
            return result;
        }

        try
        {
            using var conn = _db.OpenMain();
            using var tx = conn.BeginTransaction();
            long plays, artists, genres, tracks;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM plays;";
                plays = cmd.ExecuteNonQuery();
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM track_artists;";
                artists = cmd.ExecuteNonQuery();
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM track_genres;";
                genres = cmd.ExecuteNonQuery();
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM tracks;";
                tracks = cmd.ExecuteNonQuery();
            }
            tx.Commit();

            result["success"] = true;
            result["deletedPlays"] = plays;
            result["deletedTrackArtists"] = artists;
            result["deletedTrackGenres"] = genres;
            result["deletedTracks"] = tracks;
            _logger.LogInformation("Cleared all plays and tracks (kept {Sched} scheduled playlists)",
                Count(conn, "scheduled_playlists"));
        }
        catch (Exception ex)
        {
            result["success"] = false;
            result["error"] = ex.Message;
        }

        return result;
    }

    private static long Count(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table};";
        return (long)cmd.ExecuteScalar()!;
    }
}
