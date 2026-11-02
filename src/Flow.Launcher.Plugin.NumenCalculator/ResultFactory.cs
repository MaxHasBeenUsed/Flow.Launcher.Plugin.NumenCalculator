using System.Globalization;
using Flow.Launcher.Plugin;

namespace Flow.Launcher.Plugin.NumenCalculator;

/// <summary>
/// Immutable description of the one row a successful evaluation produces, consumed by the
/// context menu so it does not have to re-evaluate the expression.
/// </summary>
internal sealed record ResultPayload(
    string Expression,
    string DisplayText,
    string? UnformattedNumber,
    string? UnitText,
    ValueKind Kind);

/// <summary>
/// Builds the single Flow result row for a successful Numen evaluation (spec §9).
/// Never produces error rows; callers only invoke <see cref="Create"/> on <see cref="NumenStatus.Ok"/>.
/// </summary>
internal sealed class ResultFactory
{
    internal const string IconPath = "Images\\calculator.png";
    internal const int ResultScore = 300;

    private readonly IPublicAPI _api;

    public ResultFactory(IPublicAPI api)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api = api;
    }

    public Result Create(string userQuery, NumenEvaluation result, string? displayOverride = null)
    {
        ArgumentNullException.ThrowIfNull(userQuery);
        ArgumentNullException.ThrowIfNull(result);

        var expression = userQuery.Trim();
        var displayText = displayOverride ?? result.DisplayText ?? string.Empty;
        var unformattedNumber = result.Kind == ValueKind.Number
            ? result.NumberValue.ToString("R", CultureInfo.InvariantCulture)
            : null;

        return new Result
        {
            Title = displayText,
            SubTitle = expression,
            IcoPath = IconPath,
            Score = ResultScore,
            CopyText = displayText,
            ContextData = new ResultPayload(
                expression,
                displayText,
                unformattedNumber,
                result.UnitRender ?? result.UnitRaw,
                result.Kind),
            Action = _ => CopyToClipboard(displayText),
        };
    }

    /// <summary>
    /// Copies <paramref name="text"/> through Flow and reports success; never throws.
    /// Shared with the context-menu rows.
    /// </summary>
    internal bool CopyToClipboard(string text)
    {
        try
        {
            _api.CopyToClipboard(text);
            return true;
        }
        catch (Exception ex)
        {
            _api.LogDebug(nameof(ResultFactory), $"copy failed: {ex.Message}");
            return false;
        }
    }
}
