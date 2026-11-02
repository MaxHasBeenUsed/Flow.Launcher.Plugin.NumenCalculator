using System.Globalization;

namespace Flow.Launcher.Plugin.NumenCalculator;

/// <summary>
/// Renders a <see cref="DateTimePart"/> with a user-supplied .NET date pattern. The pattern is
/// printed exactly as written; no relative shortening and no automatic time-zone suffix is added.
/// Returns <c>null</c> instead of throwing.
/// </summary>
internal static class DateOutputFormatter
{
    private const long MaxOffsetSeconds = 14 * 3600;

    public static string? Format(DateTimePart part, string pattern)
    {
        if (part is null || string.IsNullOrWhiteSpace(pattern))
            return null;

        try
        {
            int nanos = Math.Clamp(part.Nanos, 0, 999_999_999);
            DateTimeOffset instant = DateTimeOffset.FromUnixTimeSeconds(part.UnixSeconds)
                .AddTicks(nanos / 100);

            DateTimeOffset value;
            TimeZoneInfo? zone = ResolveZone(part.TimezoneName);
            if (zone is not null)
            {
                value = TimeZoneInfo.ConvertTime(instant, zone);
            }
            else if (part.OffsetSeconds is not 0 &&
                     Math.Abs((long)part.OffsetSeconds) <= MaxOffsetSeconds)
            {
                value = instant.ToOffset(TimeSpan.FromSeconds(part.OffsetSeconds));
            }
            else
            {
                value = TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.Local);
            }

            return value.ToString(pattern, CultureInfo.CurrentCulture);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static TimeZoneInfo? ResolveZone(string? timezoneName)
    {
        if (string.IsNullOrWhiteSpace(timezoneName))
            return null;

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezoneName);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
