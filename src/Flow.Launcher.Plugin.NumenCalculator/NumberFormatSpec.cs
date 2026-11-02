namespace Flow.Launcher.Plugin.NumenCalculator;

/// <summary>
/// Supported decimal/grouping separator combinations (spec §10). The style selects the language
/// Numen sees; the system region still selects the implicit-currency target.
/// </summary>
public enum NumberSeparatorStyle
{
    System = 0,
    DotComma = 1,
    CommaDot = 2,
    CommaSpace = 3,
}

/// <summary>
/// Validates the user-entered separator pair. Empty means "follow the system locale"; otherwise only
/// the three unambiguous pairs are accepted. Never throws.
/// </summary>
internal static class NumberFormatSpec
{
    internal static bool TryNormalize(
        string? decimalSeparator,
        string? groupSeparator,
        out string normalizedDecimal,
        out string normalizedGroup,
        out NumberSeparatorStyle style)
    {
        normalizedDecimal = "";
        normalizedGroup = "";
        style = NumberSeparatorStyle.System;

        string decimalText = decimalSeparator?.Trim() ?? "";
        string groupText = groupSeparator?.Trim() ?? "";
        bool groupIsWhitespace = groupSeparator is not null &&
                                  groupSeparator.Length > 0 &&
                                  string.IsNullOrWhiteSpace(groupSeparator);

        // Both fields empty (or whitespace only) means the system locale decides everything.
        if (decimalText.Length == 0 && groupText.Length == 0)
            return true;

        if (decimalText.Length == 0)
            return false;

        if (decimalText == "." && groupText == ",")
        {
            normalizedDecimal = ".";
            normalizedGroup = ",";
            style = NumberSeparatorStyle.DotComma;
            return true;
        }

        if (decimalText == "," && groupText == ".")
        {
            normalizedDecimal = ",";
            normalizedGroup = ".";
            style = NumberSeparatorStyle.CommaDot;
            return true;
        }

        if (decimalText == "," && groupIsWhitespace)
        {
            normalizedDecimal = ",";
            normalizedGroup = " ";
            style = NumberSeparatorStyle.CommaSpace;
            return true;
        }

        // One-sided, identical, or otherwise ambiguous pairs are rejected.
        return false;
    }
}
