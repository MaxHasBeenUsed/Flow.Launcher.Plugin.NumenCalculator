using System.Globalization;
using Flow.Launcher.Plugin;
using Moq;
using Xunit;

namespace Flow.Launcher.Plugin.NumenCalculator.Tests;

public sealed class ResultFactoryTests
{
    private readonly Mock<IPublicAPI> _api = new();

    private ResultFactory CreateFactory() => new(_api.Object);

    private static NumenEvaluation Evaluation(
        ValueKind kind = ValueKind.Number,
        string displayText = "4",
        double numberValue = 4,
        string? unitRaw = null,
        string? unitRender = null) => new()
        {
            Status = NumenStatus.Ok,
            Kind = kind,
            DisplayText = displayText,
            NumberValue = numberValue,
            UnitRaw = unitRaw,
            UnitRender = unitRender,
        };

    private static ResultPayload PayloadOf(Result result) =>
        Assert.IsType<ResultPayload>(result.ContextData);

    [Fact]
    public void Create_MapsTitleSubTitleCopyTextIconAndScore()
    {
        var result = CreateFactory().Create("2 + 2", Evaluation(displayText: "4"));

        Assert.Equal("4", result.Title);
        Assert.Equal("2 + 2", result.SubTitle);
        Assert.Equal("4", result.CopyText);
        Assert.Equal("Images\\calculator.png", result.IcoPath);
        Assert.Equal(300, result.Score);
    }

    [Fact]
    public void Create_TrimsUserQueryForSubTitleAndExpression()
    {
        var result = CreateFactory().Create("   2 + 2   ", Evaluation(displayText: "4"));

        Assert.Equal("2 + 2", result.SubTitle);
        Assert.Equal("2 + 2", PayloadOf(result).Expression);
    }

    [Fact]
    public void Create_PreservesPhraseQueryUnchangedBesidesTrimming()
    {
        var result = CreateFactory().Create("what time is it", Evaluation(
            kind: ValueKind.DateTime,
            displayText: "2026-01-18T00:00:00+00:00",
            numberValue: 0));

        Assert.Equal("what time is it", result.SubTitle);
        Assert.Equal("what time is it", PayloadOf(result).Expression);
    }

    [Fact]
    public void Create_LeavesOptionalResultFieldsUnset()
    {
        var result = CreateFactory().Create("2 + 2", Evaluation(displayText: "4"));

        Assert.Null(result.AutoCompleteText);
        Assert.Null(result.TitleToolTip);
        Assert.Null(result.SubTitleToolTip);
        Assert.Null(result.PreviewPanel);
        Assert.Null(result.ProgressBar);
    }

    [Fact]
    public void Create_NumberPayload_HasUnformattedNumberUnitAndKind()
    {
        var result = CreateFactory().Create("1 km to m", Evaluation(
            displayText: "1,000m",
            numberValue: 1000,
            unitRaw: "meter",
            unitRender: "m"));

        var payload = PayloadOf(result);

        Assert.Equal("1 km to m", payload.Expression);
        Assert.Equal("1,000m", payload.DisplayText);
        Assert.Equal("1000", payload.UnformattedNumber);
        Assert.Equal("m", payload.UnitText);
        Assert.Equal(ValueKind.Number, payload.Kind);
    }

    [Fact]
    public void Create_NumberPayload_UsesRoundTripFormatting()
    {
        var result = CreateFactory().Create("1/3", Evaluation(
            displayText: "0.3333333333333333",
            numberValue: 1.0 / 3.0));

        Assert.Equal((1.0 / 3.0).ToString("R", CultureInfo.InvariantCulture), PayloadOf(result).UnformattedNumber);
    }

    [Fact]
    public void Create_DateTimePayload_HasNoUnformattedNumber()
    {
        var result = CreateFactory().Create("today", Evaluation(
            kind: ValueKind.DateTime,
            displayText: "2026-01-18",
            numberValue: 1768694400,
            unitRaw: null,
            unitRender: null));

        var payload = PayloadOf(result);

        Assert.Equal("today", payload.Expression);
        Assert.Equal("2026-01-18", payload.DisplayText);
        Assert.Null(payload.UnformattedNumber);
        Assert.Null(payload.UnitText);
        Assert.Equal(ValueKind.DateTime, payload.Kind);
    }

    [Fact]
    public void Create_TextPayload_HasNoUnformattedNumberAndTextKind()
    {
        var result = CreateFactory().Create("\"abc\"", Evaluation(
            kind: ValueKind.Text,
            displayText: "abc",
            numberValue: 0));

        var payload = PayloadOf(result);

        Assert.Null(payload.UnformattedNumber);
        Assert.Equal(ValueKind.Text, payload.Kind);
        Assert.Equal("abc", payload.DisplayText);
    }

    [Fact]
    public void Create_NonNumberKinds_HaveNoUnformattedNumber()
    {
        ValueKind[] nonNumberKinds = [ValueKind.DateTime, ValueKind.Duration, ValueKind.Boolean, ValueKind.Text];

        foreach (var kind in nonNumberKinds)
        {
            var result = CreateFactory().Create("2 hr", Evaluation(
                kind: kind,
                displayText: "2h",
                numberValue: 7200));

            Assert.Null(PayloadOf(result).UnformattedNumber);
        }
    }

    [Fact]
    public void Create_UnformattedNumberIsCultureInvariantUnderCommaDecimalCulture()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal(",", CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator);

            var result = CreateFactory().Create("1 km to m", Evaluation(
                displayText: "1.234,5m",
                numberValue: 1234.5));

            Assert.Equal("1234.5", PayloadOf(result).UnformattedNumber);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void Create_UnitTextPrefersRenderOverRaw()
    {
        var result = CreateFactory().Create("1 km to m", Evaluation(
            displayText: "1,000m",
            numberValue: 1000,
            unitRaw: "kilometer",
            unitRender: "km"));

        Assert.Equal("km", PayloadOf(result).UnitText);
    }

    [Fact]
    public void Create_UnitTextFallsBackToRawWhenRenderMissing()
    {
        var result = CreateFactory().Create("1 usd", Evaluation(
            displayText: "$1.00",
            numberValue: 1,
            unitRaw: "USD",
            unitRender: null));

        Assert.Equal("USD", PayloadOf(result).UnitText);
    }

    [Fact]
    public void Create_UnitTextIsNullWhenNoUnit()
    {
        var result = CreateFactory().Create("2 + 2", Evaluation(displayText: "4"));

        Assert.Null(PayloadOf(result).UnitText);
    }

    [Fact]
    public void Create_ActionCopiesDisplayTextOnceAndReturnsTrue()
    {
        var result = CreateFactory().Create("2 + 2", Evaluation(displayText: "4"));

        var action = result.Action;
        Assert.NotNull(action);
        Assert.True(action!(new ActionContext()));

        _api.Verify(a => a.CopyToClipboard("4", false, true), Times.Once);
    }

    [Fact]
    public void Create_ActionReturnsFalseAndLogsWhenCopyThrows()
    {
        _api.Setup(a => a.CopyToClipboard(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .Throws(new InvalidOperationException("clipboard unavailable"));

        var result = CreateFactory().Create("2 + 2", Evaluation(displayText: "4"));

        var action = result.Action;
        Assert.NotNull(action);
        Assert.False(action!(new ActionContext()));

        _api.Verify(a => a.LogDebug("ResultFactory", It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public void Create_ActionNeverThrowsWhenCopyThrows()
    {
        _api.Setup(a => a.CopyToClipboard(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .Throws(new ObjectDisposedException("clipboard"));

        var result = CreateFactory().Create("2 + 2", Evaluation(displayText: "4"));
        var action = result.Action;

        var exception = Record.Exception(() => Assert.False(action!(new ActionContext())));

        Assert.Null(exception);
    }
}
