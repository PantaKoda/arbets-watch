using System.Globalization;

namespace ArbetsWatch.Core.Time;

/// <summary>
/// Conversions between UTC instants and the Stockholm wall-clock text used by JobStream
/// (see docs/api-contracts.md, "Time semantics"). Internally everything is UTC.
/// </summary>
public static class SwedishTime
{
    /// <summary>Bumped when conversion rules change, so stored checkpoints can be re-validated.</summary>
    public const int AdapterVersion = 1;

    public static TimeZoneInfo Zone { get; } = FindStockholm();

    /// <summary>
    /// Parses an offset-free <c>YYYY-MM-DDTHH:MM:SS</c> value as Stockholm wall-clock time.
    /// In the repeated autumn hour the earlier (summer-time) instant is chosen, or the later one when
    /// <paramref name="laterInRepeatedHour"/> is set (removal dates, see docs/api-contracts.md). A time inside the
    /// spring gap is read with the standard (winter) offset. Returns null for missing or unparseable text.
    /// </summary>
    public static DateTimeOffset? ParseLocal(string? text, bool laterInRepeatedHour = false)
    {
        if (string.IsNullOrWhiteSpace(text) ||
            !DateTime.TryParseExact(text, ["yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss.FFFFFFF"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            return null;
        }

        return FromLocal(local, laterInRepeatedHour);
    }

    public static DateTimeOffset FromLocal(DateTime local, bool laterInRepeatedHour = false)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        TimeSpan offset;
        if (Zone.IsAmbiguousTime(local))
        {
            // The larger offset (summer time) is the earlier instant.
            var offsets = Zone.GetAmbiguousTimeOffsets(local);
            offset = laterInRepeatedHour ? offsets.Min() : offsets.Max();
        }
        else if (Zone.IsInvalidTime(local))
        {
            offset = Zone.BaseUtcOffset;
        }
        else
        {
            offset = Zone.GetUtcOffset(local);
        }

        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    public static DateTimeOffset ToLocal(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, Zone);

    /// <summary>
    /// Formats a query bound as Stockholm wall-clock time with the explicit offset for that instant,
    /// e.g. <c>2026-10-06T19:00:00+02:00</c>. Sub-second precision is dropped (the API truncates it).
    /// </summary>
    public static string FormatQueryBound(DateTimeOffset instant) =>
        ToLocal(FloorToSecond(instant)).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    public static DateTimeOffset FloorToSecond(DateTimeOffset instant) =>
        new(instant.UtcTicks - (instant.UtcTicks % TimeSpan.TicksPerSecond), TimeSpan.Zero);

    private static TimeZoneInfo FindStockholm()
    {
        // .NET resolves IANA IDs on Windows through ICU; the Windows ID is a fallback for invariant/NLS setups.
        foreach (var id in new[] { "Europe/Stockholm", "W. Europe Standard Time" })
        {
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone))
            {
                return zone;
            }
        }

        throw new TimeZoneNotFoundException("The Europe/Stockholm time zone is not available on this system.");
    }
}
