using System;

namespace Jellyfin.Plugin.Estadisticas.Models;

/// <summary>
/// Helper that converts a <see cref="QueryWindow"/> into a concrete UTC date range
/// [Start, End) respecting the rule "do not include the current period".
///
/// Week = Monday 00:00 to Sunday 23:59:59 (ISO 8601 week: Monday is day 1).
/// Month = calendar month.
/// Year = calendar year.
/// All bounds are inclusive at the start, exclusive at the end (half-open interval)
/// to make SQL BETWEEN-style queries unambiguous. End is exclusive.
/// </summary>
public static class TimeWindow
{
    /// <summary>
    /// Returns the (start, end) UTC DateTime pair for the given window relative to "now".
    /// End is EXCLUSIVE: the SQL filter should be `played_at &gt;= start AND played_at &lt; end`.
    /// </summary>
    public static (DateTime Start, DateTime End) GetRange(QueryWindow window)
        => GetRange(window, DateTime.UtcNow);

    /// <summary>
    /// Internal overload that accepts a reference "now" for testability.
    /// </summary>
    public static (DateTime Start, DateTime End) GetRange(QueryWindow window, DateTime nowUtc)
    {
        // Work in UTC but interpret "calendar periods" in UTC terms (consistent for the
        // whole server). For typical home servers the user's local calendar ~= UTC dates
        // close enough; if needed, this can be re-parameterized later.
        var now = nowUtc;

        switch (window)
        {
            case QueryWindow.TwoWeeks:
            {
                // Find Monday of the current week (UTC). 0=Sunday in DayOfWeek, so adjust.
                var daysSinceMonday = ((int)now.DayOfWeek + 6) % 7; // Mon=0, Tue=1, ..., Sun=6
                var thisMonday = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc).AddDays(-daysSinceMonday);
                var end = thisMonday; // exclusive: anything before this Monday
                var start = end.AddDays(-14); // 2 complete weeks before
                return (start, end);
            }

            case QueryWindow.OneMonth:
            {
                // First day of current month at 00:00 UTC.
                var firstOfThisMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                var end = firstOfThisMonth;
                var start = end.AddMonths(-1);
                return (start, end);
            }

            case QueryWindow.ThreeMonths:
            {
                var firstOfThisMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                var end = firstOfThisMonth;
                var start = end.AddMonths(-3);
                return (start, end);
            }

            case QueryWindow.SixMonths:
            {
                var firstOfThisMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                var end = firstOfThisMonth;
                var start = end.AddMonths(-6);
                return (start, end);
            }

            case QueryWindow.TwelveMonths:
            {
                var firstOfThisMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                var end = firstOfThisMonth;
                var start = end.AddMonths(-12);
                return (start, end);
            }

            case QueryWindow.LastYear:
            {
                var firstOfThisYear = new DateTime(now.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                var end = firstOfThisYear;
                var start = end.AddYears(-1);
                return (start, end);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(window), window, "Unknown window");
        }
    }

    /// <summary>Human-readable label in Spanish for a window.</summary>
    public static string Label(QueryWindow w) => w switch
    {
        QueryWindow.TwoWeeks => "Ultimas 2 semanas",
        QueryWindow.OneMonth => "Ultimo mes",
        QueryWindow.ThreeMonths => "Ultimos 3 meses",
        QueryWindow.SixMonths => "Ultimos 6 meses",
        QueryWindow.TwelveMonths => "Ultimos 12 meses",
        QueryWindow.LastYear => "Año anterior",
        _ => w.ToString()
    };

    /// <summary>Short code for serialization (DB, API).</summary>
    public static string Code(QueryWindow w) => w switch
    {
        QueryWindow.TwoWeeks => "2w",
        QueryWindow.OneMonth => "1m",
        QueryWindow.ThreeMonths => "3m",
        QueryWindow.SixMonths => "6m",
        QueryWindow.TwelveMonths => "12m",
        QueryWindow.LastYear => "last_year",
        _ => w.ToString()
    };

    /// <summary>Parse a code back to the enum. Throws if unknown.</summary>
    public static QueryWindow ParseCode(string code) => code switch
    {
        "2w" => QueryWindow.TwoWeeks,
        "1m" => QueryWindow.OneMonth,
        "3m" => QueryWindow.ThreeMonths,
        "6m" => QueryWindow.SixMonths,
        "12m" => QueryWindow.TwelveMonths,
        "last_year" => QueryWindow.LastYear,
        _ => throw new ArgumentException($"Unknown window code: {code}")
    };
}
