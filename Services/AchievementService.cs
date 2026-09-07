using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Estadisticas.Data;
using Jellyfin.Plugin.Estadisticas.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Estadisticas.Services;

/// <summary>
/// Calculates achievements and levels based on the user's listening history.
///
/// Achievements are organized in categories:
/// - Volume: total plays, total distinct songs, total distinct artists
/// - Time: total listening time (approximated via duration_ms)
/// - Diversity: distinct genres, distinct albums
/// - Loyalty: plays of top genre, plays of top artist
/// - Streak: consecutive days with at least 1 play (future, needs daily aggregation)
///
/// Levels are earned by period:
/// - Monthly: Bronze (50 plays), Silver (200), Gold (500), Platinum (1000), Diamond (2000)
/// - Semester: same thresholds × 6
/// - Yearly: same thresholds × 12
///
/// All thresholds are configurable in the ACHIEVEMENTS dictionary below.
/// </summary>
public sealed class AchievementService
{
    private readonly SqliteDb _db;
    private readonly ILogger<AchievementService> _logger;

    public AchievementService(SqliteDb db, ILogger<AchievementService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Returns all achievements with their current progress and earned status.
    /// Called by the /Achievements endpoint and the "Logros" tab.
    /// </summary>
    public List<AchievementInfo> GetAllAchievements()
    {
        var result = new List<AchievementInfo>();
        if (!_db.IsInitialized) return result;

        try
        {
            var stats = GetGlobalStats();
            var levels = CalculateLevels(stats);

            // Volume achievements
            result.Add(new AchievementInfo
            {
                Id = "plays_100",
                Category = "Volumen",
                Name = "Oyente Casual",
                Description = "100 reproducciones totales",
                Icon = "music_note",
                Current = stats.TotalPlays,
                Target = 100,
                Earned = stats.TotalPlays >= 100
            });
            result.Add(new AchievementInfo
            {
                Id = "plays_1000",
                Category = "Volumen",
                Name = "Oyente Frecuente",
                Description = "1,000 reproducciones totales",
                Icon = "music_note",
                Current = stats.TotalPlays,
                Target = 1000,
                Earned = stats.TotalPlays >= 1000
            });
            result.Add(new AchievementInfo
            {
                Id = "plays_5000",
                Category = "Volumen",
                Name = "Audiófilo",
                Description = "5,000 reproducciones totales",
                Icon = "music_note",
                Current = stats.TotalPlays,
                Target = 5000,
                Earned = stats.TotalPlays >= 5000
            });
            result.Add(new AchievementInfo
            {
                Id = "plays_10000",
                Category = "Volumen",
                Name = "Melómano",
                Description = "10,000 reproducciones totales",
                Icon = "music_note",
                Current = stats.TotalPlays,
                Target = 10000,
                Earned = stats.TotalPlays >= 10000
            });

            // Distinct songs
            result.Add(new AchievementInfo
            {
                Id = "songs_100",
                Category = "Diversidad",
                Name = "Explorador",
                Description = "100 canciones distintas",
                Icon = "explore",
                Current = stats.DistinctSongs,
                Target = 100,
                Earned = stats.DistinctSongs >= 100
            });
            result.Add(new AchievementInfo
            {
                Id = "songs_1000",
                Category = "Diversidad",
                Name = "Viajero Musical",
                Description = "1,000 canciones distintas",
                Icon = "explore",
                Current = stats.DistinctSongs,
                Target = 1000,
                Earned = stats.DistinctSongs >= 1000
            });
            result.Add(new AchievementInfo
            {
                Id = "songs_5000",
                Category = "Diversidad",
                Name = "Coleccionista",
                Description = "5,000 canciones distintas",
                Icon = "explore",
                Current = stats.DistinctSongs,
                Target = 5000,
                Earned = stats.DistinctSongs >= 5000
            });

            // Distinct artists
            result.Add(new AchievementInfo
            {
                Id = "artists_50",
                Category = "Diversidad",
                Name = "Descubridor",
                Description = "50 artistas distintos",
                Icon = "person",
                Current = stats.DistinctArtists,
                Target = 50,
                Earned = stats.DistinctArtists >= 50
            });
            result.Add(new AchievementInfo
            {
                Id = "artists_200",
                Category = "Diversidad",
                Name = "Cazatalentos",
                Description = "200 artistas distintos",
                Icon = "person",
                Current = stats.DistinctArtists,
                Target = 200,
                Earned = stats.DistinctArtists >= 200
            });

            // Distinct genres
            result.Add(new AchievementInfo
            {
                Id = "genres_10",
                Category = "Diversidad",
                Name = "Paladar Amplio",
                Description = "10 géneros distintos",
                Icon = "category",
                Current = stats.DistinctGenres,
                Target = 10,
                Earned = stats.DistinctGenres >= 10
            });
            result.Add(new AchievementInfo
            {
                Id = "genres_25",
                Category = "Diversidad",
                Name = "Sibarita",
                Description = "25 géneros distintos",
                Icon = "category",
                Current = stats.DistinctGenres,
                Target = 25,
                Earned = stats.DistinctGenres >= 25
            });

            // Distinct albums
            result.Add(new AchievementInfo
            {
                Id = "albums_50",
                Category = "Diversidad",
                Name = "Arcalista",
                Description = "50 álbumes distintos",
                Icon = "album",
                Current = stats.DistinctAlbums,
                Target = 50,
                Earned = stats.DistinctAlbums >= 50
            });
            result.Add(new AchievementInfo
            {
                Id = "albums_200",
                Category = "Diversidad",
                Name = "Bibliotecario",
                Description = "200 álbumes distintos",
                Icon = "album",
                Current = stats.DistinctAlbums,
                Target = 200,
                Earned = stats.DistinctAlbums >= 200
            });

            // Listening time (approximated)
            var hoursListened = stats.TotalDurationMs / 3600000.0;
            result.Add(new AchievementInfo
            {
                Id = "time_10h",
                Category = "Tiempo",
                Name = "Primeras 10 horas",
                Description = "10 horas escuchadas",
                Icon = "schedule",
                Current = (long)hoursListened,
                Target = 10,
                Earned = hoursListened >= 10,
                Unit = "h"
            });
            result.Add(new AchievementInfo
            {
                Id = "time_100h",
                Category = "Tiempo",
                Name = "Centenario",
                Description = "100 horas escuchadas",
                Icon = "schedule",
                Current = (long)hoursListened,
                Target = 100,
                Earned = hoursListened >= 100,
                Unit = "h"
            });
            result.Add(new AchievementInfo
            {
                Id = "time_500h",
                Category = "Tiempo",
                Name = "Quinientos",
                Description = "500 horas escuchadas",
                Icon = "schedule",
                Current = (long)hoursListened,
                Target = 500,
                Earned = hoursListened >= 500,
                Unit = "h"
            });
            result.Add(new AchievementInfo
            {
                Id = "time_1000h",
                Category = "Tiempo",
                Name = "Mil Horas",
                Description = "1,000 horas escuchadas",
                Icon = "schedule",
                Current = (long)hoursListened,
                Target = 1000,
                Earned = hoursListened >= 1000,
                Unit = "h"
            });

            // Levels (monthly, semester, yearly)
            result.AddRange(levels);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calculating achievements");
        }

        return result;
    }

    /// <summary>
    /// Calculates level achievements for the current month, semester, and year.
    /// Levels: Bronze, Silver, Gold, Platinum, Diamond (escalating play counts).
    /// </summary>
    private List<AchievementInfo> CalculateLevels(GlobalStats stats)
    {
        var result = new List<AchievementInfo>();

        // Monthly levels
        var monthPlays = stats.PlaysThisMonth;
        result.Add(new AchievementInfo
        {
            Id = "month_bronze",
            Category = "Nivel Mensual",
            Name = "Bronce Mensual",
            Description = "50 reproducciones este mes",
            Icon = "military_tech",
            Current = monthPlays,
            Target = 50,
            Earned = monthPlays >= 50
        });
        result.Add(new AchievementInfo
        {
            Id = "month_silver",
            Category = "Nivel Mensual",
            Name = "Plata Mensual",
            Description = "200 reproducciones este mes",
            Icon = "military_tech",
            Current = monthPlays,
            Target = 200,
            Earned = monthPlays >= 200
        });
        result.Add(new AchievementInfo
        {
            Id = "month_gold",
            Category = "Nivel Mensual",
            Name = "Oro Mensual",
            Description = "500 reproducciones este mes",
            Icon = "military_tech",
            Current = monthPlays,
            Target = 500,
            Earned = monthPlays >= 500
        });
        result.Add(new AchievementInfo
        {
            Id = "month_platinum",
            Category = "Nivel Mensual",
            Name = "Platino Mensual",
            Description = "1,000 reproducciones este mes",
            Icon = "military_tech",
            Current = monthPlays,
            Target = 1000,
            Earned = monthPlays >= 1000
        });
        result.Add(new AchievementInfo
        {
            Id = "month_diamond",
            Category = "Nivel Mensual",
            Name = "Diamante Mensual",
            Description = "2,000 reproducciones este mes",
            Icon = "military_tech",
            Current = monthPlays,
            Target = 2000,
            Earned = monthPlays >= 2000
        });

        // Semester levels (current 6-month period)
        var semPlays = stats.PlaysThisSemester;
        result.Add(new AchievementInfo
        {
            Id = "semester_bronze",
            Category = "Nivel Semestral",
            Name = "Bronce Semestral",
            Description = "300 reproducciones este semestre",
            Icon = "military_tech",
            Current = semPlays,
            Target = 300,
            Earned = semPlays >= 300
        });
        result.Add(new AchievementInfo
        {
            Id = "semester_silver",
            Category = "Nivel Semestral",
            Name = "Plata Semestral",
            Description = "1,200 reproducciones este semestre",
            Icon = "military_tech",
            Current = semPlays,
            Target = 1200,
            Earned = semPlays >= 1200
        });
        result.Add(new AchievementInfo
        {
            Id = "semester_gold",
            Category = "Nivel Semestral",
            Name = "Oro Semestral",
            Description = "3,000 reproducciones este semestre",
            Icon = "military_tech",
            Current = semPlays,
            Target = 3000,
            Earned = semPlays >= 3000
        });
        result.Add(new AchievementInfo
        {
            Id = "semester_platinum",
            Category = "Nivel Semestral",
            Name = "Platino Semestral",
            Description = "6,000 reproducciones este semestre",
            Icon = "military_tech",
            Current = semPlays,
            Target = 6000,
            Earned = semPlays >= 6000
        });

        // Yearly levels (current calendar year)
        var yearPlays = stats.PlaysThisYear;
        result.Add(new AchievementInfo
        {
            Id = "year_bronze",
            Category = "Nivel Anual",
            Name = "Bronce Anual",
            Description = "600 reproducciones este año",
            Icon = "military_tech",
            Current = yearPlays,
            Target = 600,
            Earned = yearPlays >= 600
        });
        result.Add(new AchievementInfo
        {
            Id = "year_silver",
            Category = "Nivel Anual",
            Name = "Plata Anual",
            Description = "2,400 reproducciones este año",
            Icon = "military_tech",
            Current = yearPlays,
            Target = 2400,
            Earned = yearPlays >= 2400
        });
        result.Add(new AchievementInfo
        {
            Id = "year_gold",
            Category = "Nivel Anual",
            Name = "Oro Anual",
            Description = "6,000 reproducciones este año",
            Icon = "military_tech",
            Current = yearPlays,
            Target = 6000,
            Earned = yearPlays >= 6000
        });
        result.Add(new AchievementInfo
        {
            Id = "year_platinum",
            Category = "Nivel Anual",
            Name = "Platino Anual",
            Description = "12,000 reproducciones este año",
            Icon = "military_tech",
            Current = yearPlays,
            Target = 12000,
            Earned = yearPlays >= 12000
        });
        result.Add(new AchievementInfo
        {
            Id = "year_diamond",
            Category = "Nivel Anual",
            Name = "Diamante Anual",
            Description = "24,000 reproducciones este año",
            Icon = "military_tech",
            Current = yearPlays,
            Target = 24000,
            Earned = yearPlays >= 24000
        });

        return result;
    }

    /// <summary>
    /// Gathers global stats needed for achievement calculations.
    /// </summary>
    private GlobalStats GetGlobalStats()
    {
        var stats = new GlobalStats();
        using var conn = _db.OpenMain();

        // Total plays
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM plays;";
            stats.TotalPlays = (long)cmd.ExecuteScalar();
        }

        // Distinct songs
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(DISTINCT item_id) FROM plays;";
            stats.DistinctSongs = (long)cmd.ExecuteScalar();
        }

        // Distinct artists
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(DISTINCT artist) FROM track_artists WHERE item_id IN (SELECT DISTINCT item_id FROM plays);";
            stats.DistinctArtists = (long)cmd.ExecuteScalar();
        }

        // Distinct genres
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(DISTINCT genre) FROM track_genres WHERE item_id IN (SELECT DISTINCT item_id FROM plays);";
            stats.DistinctGenres = (long)cmd.ExecuteScalar();
        }

        // Distinct albums
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(DISTINCT album_name) FROM tracks WHERE item_id IN (SELECT DISTINCT item_id FROM plays) AND album_name IS NOT NULL;";
            stats.DistinctAlbums = (long)cmd.ExecuteScalar();
        }

        // Total listening time (approximated: sum of duration_ms for all played tracks)
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COALESCE(SUM(t.duration_ms), 0) FROM plays p JOIN tracks t ON t.item_id = p.item_id WHERE t.duration_ms IS NOT NULL;";
            stats.TotalDurationMs = (long)cmd.ExecuteScalar();
        }

        // Plays this month (server local calendar)
        var now = ServerClock.NowLocal();
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0);
        var monthStartUtc = ServerClock.ToUtc(monthStart).ToString("o");
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM plays WHERE played_at >= @s;";
            cmd.Parameters.AddWithValue("@s", monthStartUtc);
            stats.PlaysThisMonth = (long)cmd.ExecuteScalar();
        }

        // Plays this semester (6 months back from start of current month)
        var semStart = monthStart.AddMonths(-6);
        var semStartUtc = ServerClock.ToUtc(semStart).ToString("o");
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM plays WHERE played_at >= @s;";
            cmd.Parameters.AddWithValue("@s", semStartUtc);
            stats.PlaysThisSemester = (long)cmd.ExecuteScalar();
        }

        // Plays this year (calendar year, server local)
        var yearStart = new DateTime(now.Year, 1, 1, 0, 0, 0);
        var yearStartUtc = ServerClock.ToUtc(yearStart).ToString("o");
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM plays WHERE played_at >= @s;";
            cmd.Parameters.AddWithValue("@s", yearStartUtc);
            stats.PlaysThisYear = (long)cmd.ExecuteScalar();
        }

        return stats;
    }

    private sealed class GlobalStats
    {
        public long TotalPlays { get; set; }
        public long DistinctSongs { get; set; }
        public long DistinctArtists { get; set; }
        public long DistinctGenres { get; set; }
        public long DistinctAlbums { get; set; }
        public long TotalDurationMs { get; set; }
        public long PlaysThisMonth { get; set; }
        public long PlaysThisSemester { get; set; }
        public long PlaysThisYear { get; set; }
    }
}

/// <summary>
/// Represents a single achievement or level with its current progress.
/// </summary>
public sealed class AchievementInfo
{
    public string Id { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Icon { get; set; } = "music_note";
    public long Current { get; set; }
    public long Target { get; set; }
    public bool Earned { get; set; }
    public string Unit { get; set; } = "";
    public double Progress => Target > 0 ? Math.Min(100.0, 100.0 * Current / Target) : 0;
}
