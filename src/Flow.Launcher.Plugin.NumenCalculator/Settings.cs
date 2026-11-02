using System.Collections.Generic;
using System.Linq;

namespace Flow.Launcher.Plugin.NumenCalculator;

/// <summary>
/// User-visible settings persisted by Flow through <see cref="IPublicAPI.LoadSettingJsonStorage{T}"/> (spec §10).
/// </summary>
public sealed class PluginSettings
{
    /// <summary>Empty means the system locale; otherwise "." or ",".</summary>
    public string DecimalSeparator { get; set; } = "";

    /// <summary>Empty means the system locale; otherwise "," "." or a single space.</summary>
    public string GroupSeparator { get; set; } = "";

    /// <summary>Empty or "local" means the system culture, "iso" a neutral ISO string, else a .NET pattern.</summary>
    public string DateFormat { get; set; } = "";

    public string Timezone { get; set; } = "";

    public bool ImplicitCurrencyConversion { get; set; } = true;

    public bool ImplicitUnitConversion { get; set; } = true;

    public bool ImplicitTimezoneConversion { get; set; } = true;

    public string RateEndpoint { get; set; } = RateService.DefaultEndpoint;

    public bool RefreshRatesOnStartup { get; set; } = true;
}

/// <summary>
/// The distinct IANA time-zone identifiers offered by the settings panel and accepted by the
/// query path. Windows identifiers are never passed to Numen (spec §10).
/// </summary>
internal static class TimeZoneCatalog
{
    private static readonly Lazy<IReadOnlyList<string>> CachedIds = new(Build, isThreadSafe: true);

    /// <summary>Distinct IANA identifiers, ordinal-sorted.</summary>
    internal static IReadOnlyList<string> IanaIds => CachedIds.Value;

    internal static bool Contains(string ianaId) =>
        !string.IsNullOrWhiteSpace(ianaId) &&
        CachedIds.Value.Contains(ianaId, StringComparer.Ordinal);

    private static IReadOnlyList<string> Build()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var zone in TimeZoneInfo.GetSystemTimeZones())
        {
            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var ianaId) &&
                !string.IsNullOrWhiteSpace(ianaId))
            {
                ids.Add(ianaId);
            }
            else if (zone.HasIanaId)
            {
                ids.Add(zone.Id);
            }
        }

        return ids.OrderBy(id => id, StringComparer.Ordinal).ToArray();
    }
}
