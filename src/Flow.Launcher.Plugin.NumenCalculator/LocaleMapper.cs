using System.Globalization;

namespace Flow.Launcher.Plugin.NumenCalculator;

/// <summary>
/// Maps a .NET culture name to the POSIX-ish locale names Numen expects. Never throws, never empty.
/// </summary>
internal static class LocaleMapper
{
    private const string Fallback = "en_US";
    private static readonly char[] LocaleSeparators = ['.', '@'];

    /// <summary>
    /// Composes the Numen locale for the chosen separator style using the system culture's region.
    /// </summary>
    /// <remarks>
    /// libnumen derives number punctuation from the language portion of a <c>std::locale</c> name and
    /// the implicit-currency target from the first 2-letter tag. Composing a punctuation anchor
    /// language with the system region decouples them: a German user asking for "." and "," gets
    /// <c>ja_DE</c> (dot punctuation, EUR target).
    ///
    /// The anchors are <c>ja</c> (dot decimal, comma group), <c>nl</c> (comma decimal, dot group),
    /// and <c>fr</c> (comma decimal, space group), chosen because MSVC keeps their punctuation
    /// stable for every region. <c>en</c> and <c>de</c> are not safe: <c>en_DE</c> renders German
    /// punctuation and <c>de_CH</c>/<c>de_AT</c> change the group separator.
    /// </remarks>
    public static string ToNumenLocale(NumberSeparatorStyle style, string? cultureName = null)
    {
        string name = cultureName?.Trim() ?? string.Empty;
        if (name.Length == 0)
            name = CultureInfo.CurrentCulture.Name;

        string? region = TrySplit(name, out _, out var mappedRegion) ? mappedRegion : null;

        return style switch
        {
            NumberSeparatorStyle.DotComma => $"ja_{region ?? "US"}",
            NumberSeparatorStyle.CommaDot => $"nl_{region ?? "DE"}",
            NumberSeparatorStyle.CommaSpace => $"fr_{region ?? "FR"}",
            _ => Map(name),
        };
    }

    private static string Map(string name)
    {
        if (!TrySplit(name, out string language, out string? region))
            return Fallback;

        return region is null ? language : $"{language}_{region}";
    }

    private static bool TrySplit(string name, out string language, out string? region)
    {
        language = string.Empty;
        region = null;

        if (string.IsNullOrWhiteSpace(name))
            return false;

        int cut = name.IndexOfAny(LocaleSeparators);
        if (cut >= 0)
            name = name[..cut];

        string[] tags = name.Replace('-', '_').Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (tags.Length == 0)
            return false;

        string candidate = tags[0].ToLowerInvariant();
        if (!IsAsciiLetters(candidate))
            return false;

        language = candidate;

        for (int index = 1; index < tags.Length; index++)
        {
            string tag = tags[index];

            // Drop script subtags (Hans, Hant, Latn, Cyrl, ...).
            if (tag.Length == 4 && IsAsciiLetters(tag))
                continue;

            // Drop 3-digit UN M49 region tags.
            if (tag.Length == 3 && IsAsciiDigits(tag))
                continue;

            // Keep the first 2-letter region and stop.
            if (tag.Length == 2 && IsAsciiLetters(tag))
            {
                region = tag.ToUpperInvariant();
                break;
            }

            // Variants and anything else after the language are dropped.
        }

        return true;
    }

    private static bool IsAsciiLetters(string value)
    {
        if (value.Length == 0)
            return false;

        foreach (char character in value)
        {
            bool isLower = character >= 'a' && character <= 'z';
            bool isUpper = character >= 'A' && character <= 'Z';
            if (!isLower && !isUpper)
                return false;
        }

        return true;
    }

    private static bool IsAsciiDigits(string value)
    {
        foreach (char character in value)
        {
            if (character is < '0' or > '9')
                return false;
        }

        return true;
    }
}
