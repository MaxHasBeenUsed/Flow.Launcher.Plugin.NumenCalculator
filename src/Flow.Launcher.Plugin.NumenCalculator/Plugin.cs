using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Controls;
using Flow.Launcher.Plugin;

namespace Flow.Launcher.Plugin.NumenCalculator;

/// <summary>
/// Flow Launcher entry point: one gated evaluation per query (spec §7), a settings panel (spec §10),
/// and context-menu copy actions for the single result row (spec §9).
/// </summary>
public sealed class Plugin : IAsyncPlugin, IContextMenu, ISettingProvider, IPluginI18n
{
    private static readonly TimeSpan SlowQueryThreshold = TimeSpan.FromMilliseconds(50);

    private PluginInitContext? _context;
    private IPublicAPI? _api;
    private PluginSettings _settings = new();
    private NumenInterop? _native;
    private ResultFactory? _resultFactory;
    private RateService? _rateService;
    private readonly SemaphoreSlim _evaluationGate = new(1, 1);
    private bool _available;
    private bool _warnedInvalidTimezone;

    /// <summary>
    /// Test-only override of the directory used to resolve <c>numen_c.dll</c>. Production uses the
    /// plugin assembly directory.
    /// </summary>
    internal string? PluginDirectoryOverride { get; set; }

    internal PluginSettings Settings => _settings;

    internal IPublicAPI? Api => _api;

    /// <summary>Test seam replacing the native evaluation; production leaves this null.</summary>
    internal Func<string, NumenEvalOptions, NumenEvaluation>? EvaluationOverride { get; set; }

    internal bool IsAvailable
    {
        get => _available;
        set => _available = value;
    }

    internal PluginInitContext? Context => _context;

    public Task InitAsync(PluginInitContext context)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(context);

            // 1. Store the Flow context.
            _context = context;
            _api = context.API;

            // 2. Load Flow-owned settings; a missing file yields a default instance.
            _settings = _api.LoadSettingJsonStorage<PluginSettings>() ?? new PluginSettings();
            NormalizeSettings(_settings);

            _resultFactory = new ResultFactory(_api);

            // 3-4. Resolve the plugin-local native DLL, verify the ABI, then warm the engine once.
            var pluginDirectory = PluginDirectoryOverride
                ?? Path.GetDirectoryName(typeof(Plugin).Assembly.Location)
                ?? AppContext.BaseDirectory;

            NativeMethods.Initialize(pluginDirectory);
            NumenInterop.Initialize(pluginDirectory);
            _native = new NumenInterop();
            _api.LogDebug(nameof(Plugin), $"numen engine {_native.EngineVersion}");

            var warmup = _native.Evaluate("now", new NumenEvalOptions());
            if (warmup.Status != NumenStatus.Ok)
            {
                throw new InvalidOperationException(
                    $"numen warm-up failed: {warmup.Error ?? warmup.Status.ToString()}");
            }

            // 5. Rate cache load plus optional startup refresh; never awaited on the query path.
            _rateService = new RateService(
                new HttpRateSource(),
                new FileRateCache(),
                new NativeRateSink(_native),
                new SystemRateClock(),
                new PeriodicRateTimer(),
                _api,
                _settings);
            _rateService.Initialize();

            _available = true;
        }
        catch (Exception ex)
        {
            // 6. Fail closed: log once, tell the user once, never throw out of InitAsync.
            _available = false;
            LogInitFailure(ex);
        }

        return Task.CompletedTask;
    }

    public async Task<List<Result>> QueryAsync(Query query, CancellationToken token)
    {
        try
        {
            if (!_available ||
                !Gating.TryGetGatedQuery(query.Search, out var userQuery, out var expression) ||
                token.IsCancellationRequested)
            {
                return [];
            }

            var stopwatch = Stopwatch.StartNew();
            try
            {
                await _evaluationGate.WaitAsync(token);
            }
            catch (OperationCanceledException)
            {
                return [];
            }

            try
            {
                if (token.IsCancellationRequested || _resultFactory is null)
                    return [];

                NumenEvaluation? result;
                if (EvaluationOverride is not null)
                {
                    result = EvaluationOverride(expression, BuildOptions());
                }
                else if (_native is not null)
                {
                    result = _native.Evaluate(expression, BuildOptions());
                }
                else
                {
                    return [];
                }

                if (token.IsCancellationRequested)
                    return [];

                if (result.Status != NumenStatus.Ok)
                {
                    LogDebug($"numen: {result.Error}");
                    return [];
                }

                return [_resultFactory.Create(userQuery, result, CustomDateDisplay(result))];
            }
            finally
            {
                _evaluationGate.Release();

                if (stopwatch.Elapsed > SlowQueryThreshold)
                    LogWarn($"query took {stopwatch.Elapsed.TotalMilliseconds:F1} ms: {userQuery}");
            }
        }
        catch (Exception ex)
        {
            LogDebug($"query failed: {ex}");
            return [];
        }
    }

    public List<Result> LoadContextMenus(Result selectedResult)
    {
        try
        {
            if (selectedResult?.ContextData is not ResultPayload payload)
                return [];

            var rows = new List<Result>(capacity: 5);
            AddRow(payload.DisplayText, "flowlauncher_plugin_numen_calculator_context_copy_answer");

            if (payload.Kind == ValueKind.Number &&
                !string.IsNullOrEmpty(payload.UnformattedNumber))
            {
                AddRow(payload.UnformattedNumber, "flowlauncher_plugin_numen_calculator_context_copy_unformatted_answer");
            }

            AddRow(payload.Expression, "flowlauncher_plugin_numen_calculator_context_copy_expression");
            AddRow(
                $"{payload.Expression} = {payload.DisplayText}",
                "flowlauncher_plugin_numen_calculator_context_copy_expression_and_answer");

            if (!string.IsNullOrEmpty(payload.UnitText))
                AddRow(payload.UnitText, "flowlauncher_plugin_numen_calculator_context_copy_unit");

            void AddRow(string text, string labelKey)
            {
                rows.Add(new Result
                {
                    Title = Translate(labelKey),
                    SubTitle = text,
                    IcoPath = ResultFactory.IconPath,
                    Action = _ => _resultFactory?.CopyToClipboard(text) ?? false,
                });
            }

            return rows;
        }
        catch (Exception ex)
        {
            LogDebug($"context menu failed: {ex}");
            return [];
        }
    }

    public Control CreateSettingPanel() => new SettingsControl(this);

    public string GetTranslatedPluginTitle() =>
        Translate("flowlauncher_plugin_numen_calculator_plugin_name");

    public string GetTranslatedPluginDescription() =>
        Translate("flowlauncher_plugin_numen_calculator_plugin_description");

    public void OnCultureInfoChanged(CultureInfo newCulture)
    {
        // UI strings are translated on demand through IPublicAPI.GetTranslation.
    }

    internal void SaveSettings()
    {
        try
        {
            _api?.SaveSettingJsonStorage<PluginSettings>();
        }
        catch (Exception ex)
        {
            LogDebug($"settings save failed: {ex}");
        }
    }

    internal async Task<RateRefreshResult> RefreshRatesAsync()
    {
        if (_rateService is null)
            return new RateRefreshResult(Applied: false, Persisted: false, Error: null);

        return await _rateService.RefreshAsync();
    }

    private NumenEvalOptions BuildOptions()
    {
        NumberFormatSpec.TryNormalize(
            _settings.DecimalSeparator,
            _settings.GroupSeparator,
            out _,
            out _,
            out var separatorStyle);
        DateFormatSpec.TryNormalize(_settings.DateFormat, out _, out var dateMode);

        return new NumenEvalOptions
        {
            Locale = LocaleMapper.ToNumenLocale(separatorStyle, CultureInfo.CurrentCulture.Name),
            DateLocale = LocaleMapper.ToNumenLocale(
                NumberSeparatorStyle.System,
                CultureInfo.CurrentCulture.Name),
            NeutralDateFormat = dateMode is DateOutputMode.Iso or DateOutputMode.Custom,
            Strict = true,
            ImplicitCurrency = _settings.ImplicitCurrencyConversion,
            ImplicitUnit = _settings.ImplicitUnitConversion,
            ImplicitTimezone = _settings.ImplicitTimezoneConversion,
            Timezone = ResolveTimezone(_settings.Timezone),
        };
    }

    /// <summary>
    /// Renders a custom date pattern over the engine's date result. Returns null when the row should
    /// keep the engine text (system/ISO formatting or a rendering failure).
    /// </summary>
    private string? CustomDateDisplay(NumenEvaluation result)
    {
        if (result.Kind != ValueKind.DateTime || result.DateTime is null)
            return null;

        DateFormatSpec.TryNormalize(_settings.DateFormat, out string pattern, out var mode);
        return mode == DateOutputMode.Custom
            ? DateOutputFormatter.Format(result.DateTime, pattern)
            : null;
    }

    private string? ResolveTimezone(string? setting)
    {
        if (string.IsNullOrWhiteSpace(setting))
            return null;

        if (TimeZoneCatalog.Contains(setting))
            return setting;

        if (!_warnedInvalidTimezone)
        {
            _warnedInvalidTimezone = true;
            LogWarn($"ignoring unavailable timezone '{setting}'; using the system timezone");
        }

        return null;
    }

    private string Translate(string key)
    {
        try
        {
            var translation = _api?.GetTranslation(key);
            return string.IsNullOrWhiteSpace(translation) ? key : translation!;
        }
        catch (Exception)
        {
            return key;
        }
    }

    private void LogInitFailure(Exception exception)
    {
        try
        {
            _api?.LogWarn(nameof(Plugin), $"init failed: {exception}");
        }
        catch (Exception)
        {
            // Logging must not defeat the fail-closed guarantee.
        }

        try
        {
            _api?.ShowMsgError(
                Translate("flowlauncher_plugin_numen_calculator_plugin_name"),
                Translate("flowlauncher_plugin_numen_calculator_error_init"));
        }
        catch (Exception)
        {
            // A failing dialog must not throw out of InitAsync.
        }
    }

    private void LogDebug(string message)
    {
        try
        {
            _api?.LogDebug(nameof(Plugin), message);
        }
        catch (Exception)
        {
            // Diagnostics are best-effort.
        }
    }

    private void LogWarn(string message)
    {
        try
        {
            _api?.LogWarn(nameof(Plugin), message);
        }
        catch (Exception)
        {
            // Diagnostics are best-effort.
        }
    }

    private static void NormalizeSettings(PluginSettings settings)
    {
        if (!NumberFormatSpec.TryNormalize(
                settings.DecimalSeparator,
                settings.GroupSeparator,
                out string normalizedDecimal,
                out string normalizedGroup,
                out _))
        {
            normalizedDecimal = "";
            normalizedGroup = "";
        }

        settings.DecimalSeparator = normalizedDecimal;
        settings.GroupSeparator = normalizedGroup;

        if (!DateFormatSpec.TryNormalize(settings.DateFormat, out string normalizedDate, out _))
            normalizedDate = "";

        settings.DateFormat = normalizedDate;

        settings.Timezone ??= "";

        if (string.IsNullOrWhiteSpace(settings.RateEndpoint))
            settings.RateEndpoint = new PluginSettings().RateEndpoint;
    }

    /// <summary>
    /// Adapts the managed native wrapper to the rate service's sink contract. The service works in
    /// terms of a rate-entry list; Numen takes a dictionary.
    /// </summary>
    private sealed class NativeRateSink : IRateSink
    {
        private readonly NumenInterop _native;

        internal NativeRateSink(NumenInterop native) => _native = native;

        public int SetRates(IReadOnlyList<RateEntry> rates)
        {
            var unitsPerUsd = new Dictionary<string, double>(rates.Count, StringComparer.Ordinal);
            foreach (var rate in rates)
                unitsPerUsd[rate.Code] = rate.UnitsPerUsd;

            return (int)_native.SetRates(unitsPerUsd);
        }
    }
}
