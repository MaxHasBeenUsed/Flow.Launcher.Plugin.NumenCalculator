using System.Text;

namespace Flow.Launcher.Plugin.NumenCalculator;

/// <summary>
/// Conservative activation gate for the global Flow query box (spec §8). It decides whether a
/// search box string is worth sending to Numen; it is not a second parser.
/// </summary>
internal static class Gating
{
    private const int MinimumQueryLength = 3;

    private const string NonNumericSignals = "+-*/%^&|~<>=():";
    private const string TrailingBinaryOperators = "+-*/^&|<>=,";
    private const string EqualityPrefixes = "!<>=";

    private static readonly HashSet<string> SignalWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // English number words.
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
        "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen",
        "nineteen", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety",
        "hundred", "thousand", "million", "billion", "trillion",

        // Temporal words.
        "time", "now", "today", "tomorrow", "yesterday", "date", "ago", "until", "since",
        "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday",
        "mon", "tue", "tues", "wed", "thu", "thur", "thurs", "fri", "sat", "sun",
        "january", "february", "march", "april", "may", "june", "july", "august", "september",
        "october", "november", "december",
        "jan", "feb", "mar", "apr", "jun", "jul", "aug", "sep", "sept", "oct", "nov", "dec",
    };

    private static readonly Dictionary<string, string> PhraseRewrites =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["what time is it"] = "time",
            ["what's the time"] = "time",
            ["what is the time"] = "time",
            ["what time is it now"] = "time",
            ["current time"] = "time",
            ["time is it"] = "time",

            ["what is the date"] = "today",
            ["what's the date"] = "today",
            ["what is today's date"] = "today",
            ["what's today's date"] = "today",
            ["what day is it"] = "today",
            ["what day is today"] = "today",
            ["todays date"] = "today",
            ["today's date"] = "today",
        };

    /// <summary>
    /// Gates a raw Flow search string.
    /// <paramref name="query"/> is the trimmed user text (what the row displays);
    /// <paramref name="expression"/> is the normalized text sent to Numen.
    /// Both outputs are empty when the method returns <see langword="false"/>.
    /// </summary>
    public static bool TryGetGatedQuery(string? search, out string query, out string expression)
    {
        query = string.Empty;
        expression = string.Empty;

        if (search is null)
            return false;

        var trimmed = search.Trim();
        if (trimmed.Length < MinimumQueryLength)
            return false;

        if (!HasNoLineBreakOrTopLevelSemicolon(trimmed))
            return false;

        if (LooksLikePathOrUrl(trimmed))
            return false;

        var normalized = NormalizePhrase(trimmed);

        if (!HasNumericOrTemporalSignal(normalized))
            return false;

        if (!HasNonNumericSignal(normalized))
            return false;

        if (!HasWellFormedEquality(normalized))
            return false;

        if (!HasBalancedBrackets(normalized))
            return false;

        if (!HasEvenQuoteCount(normalized))
            return false;

        if (EndsWithBinaryOperator(normalized))
            return false;

        query = trimmed;
        expression = normalized;
        return true;
    }

    private static bool HasNoLineBreakOrTopLevelSemicolon(string value)
    {
        var depth = 0;
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\r' or '\n':
                    return false;
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    if (depth > 0)
                        depth--;
                    break;
                case ';' when depth == 0:
                    return false;
            }
        }

        return true;
    }

    private static bool LooksLikePathOrUrl(string value)
    {
        if (value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':')
            return true;

        return value.Contains('\\') || value.Contains("://", StringComparison.Ordinal);
    }

    private static string NormalizePhrase(string trimmed)
    {
        var collapsed = CollapseWhitespace(trimmed);
        var candidate = collapsed.EndsWith('?') ? collapsed[..^1].TrimEnd() : collapsed;

        return PhraseRewrites.TryGetValue(candidate, out var replacement) ? replacement : trimmed;
    }

    private static string CollapseWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        var spacePending = false;

        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch))
            {
                spacePending = builder.Length > 0;
                continue;
            }

            if (spacePending)
            {
                builder.Append(' ');
                spacePending = false;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    private static bool HasNumericOrTemporalSignal(string value)
    {
        var tokenStart = -1;

        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];

            if (char.IsAsciiDigit(ch))
                return true;

            if (char.IsLetter(ch))
            {
                if (tokenStart < 0)
                    tokenStart = i;
                continue;
            }

            if (tokenStart >= 0)
            {
                if (SignalWords.Contains(value[tokenStart..i]))
                    return true;
                tokenStart = -1;
            }
        }

        return tokenStart >= 0 && SignalWords.Contains(value[tokenStart..]);
    }

    private static bool HasNonNumericSignal(string value)
    {
        foreach (var ch in value)
        {
            if (char.IsAsciiLetter(ch) || NonNumericSignals.Contains(ch))
                return true;
        }

        return false;
    }

    private static bool HasWellFormedEquality(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '=')
                continue;

            var precededByOperator = i > 0 && EqualityPrefixes.Contains(value[i - 1]);
            var followedByEquals = i + 1 < value.Length && value[i + 1] == '=';

            if (!precededByOperator && !followedByEquals)
                return false;
        }

        return true;
    }

    private static bool HasBalancedBrackets(string value)
    {
        if (value.Length == 0)
            return true;

        var stack = new char[value.Length];
        var depth = 0;

        foreach (var ch in value)
        {
            switch (ch)
            {
                case '(' or '[' or '{':
                    stack[depth++] = ch;
                    break;
                case ')' or ']' or '}':
                    if (depth == 0 || !IsMatchingPair(stack[--depth], ch))
                        return false;
                    break;
            }
        }

        return depth == 0;
    }

    private static bool IsMatchingPair(char opening, char closing) =>
        (opening, closing) is ('(', ')') or ('[', ']') or ('{', '}');

    private static bool HasEvenQuoteCount(string value)
    {
        var singles = 0;
        var doubles = 0;

        foreach (var ch in value)
        {
            if (ch == '\'')
                singles++;
            else if (ch == '"')
                doubles++;
        }

        return (singles & 1) == 0 && (doubles & 1) == 0;
    }

    private static bool EndsWithBinaryOperator(string value)
    {
        for (var i = value.Length - 1; i >= 0; i--)
        {
            if (char.IsWhiteSpace(value[i]))
                continue;

            return TrailingBinaryOperators.Contains(value[i]);
        }

        return false;
    }
}
