using System.Globalization;
using System.IO;
using System.Reflection;
using Flow.Launcher.Plugin;
using Moq;
using Xunit;

namespace Flow.Launcher.Plugin.NumenCalculator.Tests;

public sealed class PluginTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task InitAsync_MissingPluginDirectory_FailsClosedAndShowsOneError()
    {
        var api = CreateApi();
        var plugin = CreateUninitializedPlugin();
        var evaluations = 0;
        plugin.EvaluationOverride = (_, _) =>
        {
            evaluations++;
            return OkNumber(4);
        };

        await plugin.InitAsync(Context(api)).WaitAsync(TestTimeout);

        Assert.False(plugin.IsAvailable);
        api.Verify(a => a.ShowMsgError(It.IsAny<string>(), It.IsAny<string>()), Times.Once);

        var rows = await plugin.QueryAsync(CreateQuery("2+2"), CancellationToken.None)
            .WaitAsync(TestTimeout);

        Assert.Empty(rows);
        Assert.Equal(0, evaluations);
    }

    [Theory]
    [InlineData("2024")]
    [InlineData("2+2=4")]
    [InlineData("1; 2")]
    [InlineData(@"C:\Users\me")]
    [InlineData("https://example.com")]
    [InlineData("sqrt(9")]
    [InlineData("2 +")]
    public async Task QueryAsync_GatingReject_ReturnsEmptyWithoutEvaluating(string search)
    {
        var plugin = await CreateFailedPluginAsync();
        plugin.IsAvailable = true;

        var evaluations = 0;
        plugin.EvaluationOverride = (_, _) =>
        {
            evaluations++;
            return OkNumber(1);
        };

        var rows = await plugin.QueryAsync(CreateQuery(search), CancellationToken.None)
            .WaitAsync(TestTimeout);

        Assert.Empty(rows);
        Assert.Equal(0, evaluations);
    }

    [Fact]
    public async Task QueryAsync_GatedExpression_ReturnsSingleResultRow()
    {
        var plugin = await CreateFailedPluginAsync();
        plugin.IsAvailable = true;
        string? evaluatedExpression = null;
        NumenEvalOptions? evaluatedOptions = null;
        plugin.EvaluationOverride = (expression, options) =>
        {
            evaluatedExpression = expression;
            evaluatedOptions = options;
            return OkNumber(4);
        };

        var rows = await plugin.QueryAsync(CreateQuery("  2+2  "), CancellationToken.None)
            .WaitAsync(TestTimeout);

        var row = Assert.Single(rows);
        Assert.Equal("4", row.Title);
        Assert.Equal("2+2", row.SubTitle);
        Assert.Equal("4", row.CopyText);
        Assert.Equal(ResultFactory.ResultScore, row.Score);
        Assert.Equal(ResultFactory.IconPath, row.IcoPath);
        Assert.Equal("2+2", evaluatedExpression);
        Assert.NotNull(evaluatedOptions);
        Assert.False(string.IsNullOrWhiteSpace(evaluatedOptions.Locale));
        Assert.Null(evaluatedOptions.Timezone);
    }

    [Fact]
    public async Task QueryAsync_SeparatorAndDateFormatSettings_ConfigureEngineOptions()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");

            var plugin = await CreateFailedPluginAsync();
            plugin.IsAvailable = true;
            plugin.Settings.DecimalSeparator = ",";
            plugin.Settings.GroupSeparator = ".";
            plugin.Settings.DateFormat = "iso";

            NumenEvalOptions? captured = null;
            plugin.EvaluationOverride = (_, options) =>
            {
                captured = options;
                return OkNumber(1);
            };

            await plugin.QueryAsync(CreateQuery("1+0"), CancellationToken.None).WaitAsync(TestTimeout);

            Assert.NotNull(captured);
            Assert.Equal("nl_FR", captured.Locale);
            Assert.Equal("fr_FR", captured.DateLocale);
            Assert.True(captured.NeutralDateFormat);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public async Task QueryAsync_CustomDateFormat_OverridesRowText()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

            var plugin = await CreateFailedPluginAsync();
            plugin.IsAvailable = true;
            plugin.Settings.DateFormat = "yyyy/MM/dd HH:mm";

            DateTimeOffset instant =
                DateTimeOffset.Parse("2026-01-18T06:30:00Z", CultureInfo.InvariantCulture);
            var part = new DateTimePart(
                instant.ToUnixTimeSeconds(),
                0,
                null,
                9 * 3600,
                null,
                null);

            plugin.EvaluationOverride = (_, _) => new NumenEvaluation
            {
                Status = NumenStatus.Ok,
                Kind = ValueKind.DateTime,
                DisplayText = "engine text",
                DateTime = part,
            };

            var rows = await plugin.QueryAsync(CreateQuery("now"), CancellationToken.None)
                .WaitAsync(TestTimeout);

            var row = Assert.Single(rows);
            Assert.Equal("2026/01/18 15:30", row.Title);
            Assert.Equal("2026/01/18 15:30", row.CopyText);

            var payload = Assert.IsType<ResultPayload>(row.ContextData);
            Assert.Equal("2026/01/18 15:30", payload.DisplayText);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public async Task QueryAsync_NonOkStatus_ReturnsEmpty()
    {
        var plugin = await CreateFailedPluginAsync();
        plugin.IsAvailable = true;
        plugin.EvaluationOverride = (_, _) => new NumenEvaluation
        {
            Status = NumenStatus.Expression,
            Error = "Unknown unit \"x\"",
            Kind = ValueKind.Number,
        };

        var rows = await plugin.QueryAsync(CreateQuery("2+2"), CancellationToken.None)
            .WaitAsync(TestTimeout);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task QueryAsync_SystemDateFormat_KeepsEngineText()
    {
        var plugin = await CreateFailedPluginAsync();
        plugin.IsAvailable = true;
        plugin.Settings.DateFormat = "";

        var part = new DateTimePart(1768717800, 0, null, 9 * 3600, null, null);
        plugin.EvaluationOverride = (_, _) => new NumenEvaluation
        {
            Status = NumenStatus.Ok,
            Kind = ValueKind.DateTime,
            DisplayText = "engine text",
            DateTime = part,
        };

        var rows = await plugin.QueryAsync(CreateQuery("now"), CancellationToken.None)
            .WaitAsync(TestTimeout);

        var row = Assert.Single(rows);
        Assert.Equal("engine text", row.Title);
        Assert.Equal("engine text", row.CopyText);
    }

    [Fact]
    public async Task QueryAsync_CancellationWhileGateHeld_ReturnsEmpty()
    {
        var plugin = await CreateFailedPluginAsync();
        plugin.IsAvailable = true;

        var evaluations = 0;
        plugin.EvaluationOverride = (_, _) =>
        {
            evaluations++;
            return OkNumber(4);
        };

        var gate = GetEvaluationGate(plugin);
        await gate.WaitAsync(TestTimeout);
        try
        {
            using var cancellation = new CancellationTokenSource();
            Task<List<Result>> query = plugin.QueryAsync(
                CreateQuery("2+2"),
                cancellation.Token);

            cancellation.Cancel();

            var rows = await query.WaitAsync(TestTimeout);

            Assert.Empty(rows);
            Assert.Equal(0, evaluations);
        }
        finally
        {
            gate.Release();
        }
    }

    [Fact]
    public async Task LoadContextMenus_NumberPayload_ProvidesFiveCopyRows()
    {
        var api = CreateApiWithIdentityTranslation();
        var plugin = await CreateFailedPluginAsync(api);

        var rows = plugin.LoadContextMenus(new Result
        {
            ContextData = new ResultPayload(
                Expression: "1 km to m",
                DisplayText: "1,000m",
                UnformattedNumber: "1000",
                UnitText: "m",
                Kind: ValueKind.Number),
        });

        Assert.Equal(5, rows.Count);
        var expectedTitles = new[]
        {
            "flowlauncher_plugin_numen_calculator_context_copy_answer",
            "flowlauncher_plugin_numen_calculator_context_copy_unformatted_answer",
            "flowlauncher_plugin_numen_calculator_context_copy_expression",
            "flowlauncher_plugin_numen_calculator_context_copy_expression_and_answer",
            "flowlauncher_plugin_numen_calculator_context_copy_unit",
        };
        var expectedSubtitles = new[] { "1,000m", "1000", "1 km to m", "1 km to m = 1,000m", "m" };
        for (var index = 0; index < rows.Count; index++)
        {
            Assert.Equal(expectedTitles[index], rows[index].Title);
            Assert.Equal(expectedSubtitles[index], rows[index].SubTitle);
        }

        Assert.All(rows, row => Assert.Equal(ResultFactory.IconPath, row.IcoPath));

        Assert.True(rows[0].Action!(new ActionContext()));
        api.Verify(
            a => a.CopyToClipboard("1,000m", It.IsAny<bool>(), It.IsAny<bool>()),
            Times.Once);
    }

    [Fact]
    public async Task LoadContextMenus_NonNumberPayload_HidesUnformattedAnswer()
    {
        var api = CreateApiWithIdentityTranslation();
        var plugin = await CreateFailedPluginAsync(api);

        var rows = plugin.LoadContextMenus(new Result
        {
            ContextData = new ResultPayload(
                Expression: "2 hr",
                DisplayText: "2 hours",
                UnformattedNumber: null,
                UnitText: "hours",
                Kind: ValueKind.Duration),
        });

        Assert.Equal(4, rows.Count);
        Assert.DoesNotContain(
            "flowlauncher_plugin_numen_calculator_context_copy_unformatted_answer",
            rows.Select(row => row.Title));
        Assert.Contains(
            "flowlauncher_plugin_numen_calculator_context_copy_unit",
            rows.Select(row => row.Title));
    }

    [Fact]
    public async Task LoadContextMenus_IgnoresResultsWithoutPayload()
    {
        var api = CreateApiWithIdentityTranslation();
        var plugin = await CreateFailedPluginAsync(api);

        var rows = plugin.LoadContextMenus(new Result { ContextData = null });

        Assert.Empty(rows);
    }

    [Fact]
    public async Task GetTranslatedPluginTitleAndDescription_UseFlowTranslations()
    {
        var api = CreateApi();
        var plugin = await CreateFailedPluginAsync(api);

        api.Setup(a => a.GetTranslation("flowlauncher_plugin_numen_calculator_plugin_name"))
            .Returns("Numen Calculator");
        api.Setup(a => a.GetTranslation("flowlauncher_plugin_numen_calculator_plugin_description"))
            .Returns("Calculator description");

        Assert.Equal("Numen Calculator", plugin.GetTranslatedPluginTitle());
        Assert.Equal("Calculator description", plugin.GetTranslatedPluginDescription());

        // InitAsync may already have translated the plugin name for a failure dialog.
        api.Verify(
            a => a.GetTranslation("flowlauncher_plugin_numen_calculator_plugin_name"),
            Times.AtLeastOnce);
        api.Verify(
            a => a.GetTranslation("flowlauncher_plugin_numen_calculator_plugin_description"),
            Times.Once);
    }

    private static NumenEvaluation OkNumber(double value) => new()
    {
        Status = NumenStatus.Ok,
        Kind = ValueKind.Number,
        DisplayText = value.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        NumberValue = value,
    };

    private static Mock<IPublicAPI> CreateApi() => new(MockBehavior.Loose);

    private static Query CreateQuery(string search)
    {
        var query = new Query();
        typeof(Query).GetProperty(nameof(Query.Search))!
            .SetValue(query, search);
        return query;
    }

    private static Mock<IPublicAPI> CreateApiWithIdentityTranslation()
    {
        var api = CreateApi();
        api.Setup(a => a.GetTranslation(It.IsAny<string>()))
            .Returns((string key) => key);
        return api;
    }

    private static Plugin CreateUninitializedPlugin() => new()
    {
        PluginDirectoryOverride = Path.Combine(
            Path.GetTempPath(),
            "numen-calculator-tests",
            Guid.NewGuid().ToString("N"),
            "plugin"),
    };

    private static PluginInitContext Context(Mock<IPublicAPI> api) =>
        new(new PluginMetadata(), api.Object);

    private static async Task<Plugin> CreateFailedPluginAsync(Mock<IPublicAPI>? api = null)
    {
        api ??= CreateApi();
        var plugin = CreateUninitializedPlugin();
        await plugin.InitAsync(Context(api)).WaitAsync(TestTimeout);
        Assert.False(plugin.IsAvailable);
        return plugin;
    }

    private static SemaphoreSlim GetEvaluationGate(Plugin plugin)
    {
        var field = typeof(Plugin).GetField(
            "_evaluationGate",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Plugin._evaluationGate was not found.");
        return Assert.IsType<SemaphoreSlim>(field.GetValue(plugin));
    }
}
