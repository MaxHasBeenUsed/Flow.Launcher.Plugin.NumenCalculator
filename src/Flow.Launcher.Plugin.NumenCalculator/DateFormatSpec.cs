using System.Globalization;

namespace Flow.Launcher.Plugin.NumenCalculator;

/// <summary>
/// How date results are rendered: by the system culture, as a neutral ISO string, or by a custom
/// .NET pattern handled by <see cref="DateOutputFormatter"/>.
/// </summary>
internal enum DateOutputMode
{
    Local = 0,
    Iso = 1,
    Custom = 2,
}

/// <summary>
/// Validates the user-entered date format setting. Empty or "local" means the system culture,
/// "iso" means a neutral ISO string, anything else is a custom .NET date pattern. Never throws.
/// </summary>
internal static class DateFormatSpec
{
    private const int MaxPatternLength = 64;

    internal static bool TryNormalize(string? input, out string normalized, out DateOutputMode mode)
    {
        normalized = "";
        mode = DateOutputMode.Local;

        if (string.IsNullOrWhiteSpace(input))
            return true;

        string pattern = input.Trim();

        if (string.Equals(pattern, "local", StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.Equals(pattern, "iso", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "iso";
            mode = DateOutputMode.Iso;
            return true;
        }

        if (pattern.Length > MaxPatternLength ||
            pattern.Contains('\r') ||
            pattern.Contains('\n'))
        {
            return false;
        }

        try
        {
            // A pattern is valid when the framework can format an instant with it.
            _ = DateTimeOffset.UnixEpoch.ToString(pattern, CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return false;
        }

        normalized = pattern;
        mode = DateOutputMode.Custom;
        return true;
    }
}
