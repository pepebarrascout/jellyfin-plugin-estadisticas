using System;

namespace Jellyfin.Plugin.Estadisticas.Models;

/// <summary>
/// Reloj de pared del servidor. El servidor trabaja con el horario
/// America/Guatemala (UTC-6, sin horario de verano). Si la base de datos de
/// zonas horarias no está disponible, se usa la zona del proceso como respaldo.
/// </summary>
public static class ServerClock
{
    private static readonly TimeZoneInfo Tz = ResolveTimeZone();

    /// <summary>Zona horaria del servidor (America/Guatemala o respaldo local).</summary>
    public static TimeZoneInfo Zone => Tz;

    /// <summary>Fecha/hora actual en hora de pared del servidor.</summary>
    public static DateTime NowLocal() => TimeZoneInfo.ConvertTime(DateTime.UtcNow, Tz);

    /// <summary>
    /// Convierte una fecha/hora de pared (sin tipo) al instante UTC equivalente,
    /// interpretándola en la zona horaria del servidor.
    /// </summary>
    public static DateTime ToUtc(DateTime wallTime)
        => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(wallTime, DateTimeKind.Unspecified), Tz);

    /// <summary>Offset (TimeSpan) de la zona del servidor respecto a UTC para un instante dado.</summary>
    public static TimeSpan UtcOffset(DateTime utc) => Tz.GetUtcOffset(utc);

    private static TimeZoneInfo ResolveTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("America/Guatemala"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Local; }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.Local; }
    }
}

/// <summary>
/// Helper that converts a <see cref="QueryWindow"/> into a concrete UTC date range
/// [Start, End) respecting the rule "do not include the current period".
///
/// Week  = Sunday 00:00 to Saturday 23:59:59 (server wall clock). The current
///         (possibly still in-progress) week is excluded.
/// Month = calendar month.
/// Year  = calendar year.
/// All bounds are inclusive at the start, exclusive at the end (half-open interval)
/// to make SQL BETWEEN-style queries unambiguous. End is exclusive.
///
/// Calendar boundaries are computed on the SERVER wall clock (America/Guatemala)
/// and then converted to UTC instants, so "today", "this week" and "this month"
/// match what the user sees on the server, regardless of the process timezone.
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
    /// Internal overload that accepts a reference "now" (UTC) for testability.
    /// </summary>
    public static (DateTime Start, DateTime End) GetRange(QueryWindow window, DateTime nowUtc)
    {
        var now = TimeZoneInfo.ConvertTime(nowUtc, ServerClock.Zone); // server wall clock

        switch (window)
        {
            case QueryWindow.TwoWeeks:
            {
                // Week runs Sunday -> Saturday. The current week (even if only one day
                // old) is excluded. If today is Friday, the current week is
                // Sunday->Friday and we return the TWO COMPLETE WEEKS before it:
                // [Sunday two weeks ago, Saturday of last week].
                var daysSinceSunday = (int)now.DayOfWeek; // Sunday=0 .. Saturday=6
                var thisSunday = now.Date.AddDays(-daysSinceSunday);
                var endExclusive = thisSunday;            // = Sunday 00:00 local
                var start = thisSunday.AddDays(-14);      // = Sunday 00:00, two weeks earlier
                return (ServerClock.ToUtc(start), ServerClock.ToUtc(endExclusive));
            }

            case QueryWindow.OneMonth:
            {
                // Full previous calendar month: 01-Ago 00:00 .. 01-Sep 00:00 (exclusive),
                // which is exactly all of August (28/29/30/31 depending on the month).
                var firstOfThisMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0);
                var start = firstOfThisMonth.AddMonths(-1);
                return (ServerClock.ToUtc(start), ServerClock.ToUtc(firstOfThisMonth));
            }

            case QueryWindow.ThreeMonths:
            {
                var firstOfThisMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0);
                var start = firstOfThisMonth.AddMonths(-3);
                return (ServerClock.ToUtc(start), ServerClock.ToUtc(firstOfThisMonth));
            }

            case QueryWindow.SixMonths:
            {
                var firstOfThisMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0);
                var start = firstOfThisMonth.AddMonths(-6);
                return (ServerClock.ToUtc(start), ServerClock.ToUtc(firstOfThisMonth));
            }

            case QueryWindow.TwelveMonths:
            {
                var firstOfThisMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0);
                var start = firstOfThisMonth.AddMonths(-12);
                return (ServerClock.ToUtc(start), ServerClock.ToUtc(firstOfThisMonth));
            }

            case QueryWindow.LastYear:
            {
                // Full previous calendar year: 01-Ene 00:00 .. 01-Ene siguiente (exclusive).
                var firstOfThisYear = new DateTime(now.Year, 1, 1, 0, 0, 0);
                var start = firstOfThisYear.AddYears(-1);
                return (ServerClock.ToUtc(start), ServerClock.ToUtc(firstOfThisYear));
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(window), window, "Unknown window");
        }
    }

    /// <summary>
    /// INCLUSIVE display boundaries (server-local calendar dates) shown in the UI,
    /// e.g. "01-Ago-2026 a 31-Ago-2026" or "01-Ene-2025 a 31-Dic-2025".
    /// The SQL range stays half-open internally; only the presentation is inclusive.
    /// </summary>
    public static (DateTime DisplayStart, DateTime DisplayEnd) GetDisplayRange(QueryWindow window)
        => GetDisplayRange(window, DateTime.UtcNow);

    /// <summary>Testable overload (accepts a reference UTC "now").</summary>
    public static (DateTime DisplayStart, DateTime DisplayEnd) GetDisplayRange(QueryWindow window, DateTime nowUtc)
    {
        var (startUtc, endUtcExclusive) = GetRange(window, nowUtc);
        var startLocal = TimeZoneInfo.ConvertTime(startUtc, ServerClock.Zone).Date;

        // All our exclusive ends are local midnights, so the last included DAY is
        // always (endExclusiveLocal - 1 day). e.g. end = 01-Sep 00:00 -> last day = 31-Ago.
        var endExclusiveLocal = TimeZoneInfo.ConvertTime(endUtcExclusive, ServerClock.Zone);
        var displayEnd = endExclusiveLocal.Date.AddDays(-1);

        return (startLocal, displayEnd);
    }

    /// <summary>Human-readable label in Spanish for a window.</summary>
    public static string Label(QueryWindow w) => w switch
    {
        QueryWindow.TwoWeeks => "Últimas 2 semanas",
        QueryWindow.OneMonth => "Último mes",
        QueryWindow.ThreeMonths => "Últimos 3 meses",
        QueryWindow.SixMonths => "Últimos 6 meses",
        QueryWindow.TwelveMonths => "Últimos 12 meses",
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
