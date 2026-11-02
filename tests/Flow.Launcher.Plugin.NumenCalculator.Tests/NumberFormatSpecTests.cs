using Xunit;

namespace Flow.Launcher.Plugin.NumenCalculator.Tests;

public sealed class NumberFormatSpecTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData(" ", "  ")]
    [InlineData(null, "")]
    public void TryNormalize_EmptyPairMeansSystem(string? decimalSeparator, string? groupSeparator)
    {
        bool ok = NumberFormatSpec.TryNormalize(
            decimalSeparator,
            groupSeparator,
            out string normalizedDecimal,
            out string normalizedGroup,
            out NumberSeparatorStyle style);

        Assert.True(ok);
        Assert.Equal("", normalizedDecimal);
        Assert.Equal("", normalizedGroup);
        Assert.Equal(NumberSeparatorStyle.System, style);
    }

    [Theory]
    [InlineData(".", ",", NumberSeparatorStyle.DotComma)]
    [InlineData(",", ".", NumberSeparatorStyle.CommaDot)]
    [InlineData(" . ", " , ", NumberSeparatorStyle.DotComma)]
    [InlineData(" , ", " . ", NumberSeparatorStyle.CommaDot)]
    [InlineData(" ,", ". ", NumberSeparatorStyle.CommaDot)]
    public void TryNormalize_AcceptUnambiguousPairs(
        string decimalSeparator,
        string groupSeparator,
        NumberSeparatorStyle expectedStyle)
    {
        bool ok = NumberFormatSpec.TryNormalize(
            decimalSeparator,
            groupSeparator,
            out string normalizedDecimal,
            out string normalizedGroup,
            out NumberSeparatorStyle style);

        Assert.True(ok);
        Assert.Equal(expectedStyle, style);
        Assert.Equal(decimalSeparator.Trim(), normalizedDecimal);
        Assert.Equal(groupSeparator.Trim(), normalizedGroup);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\u00A0")]
    public void TryNormalize_CommaWithWhitespaceGroupIsCommaSpace(string groupSeparator)
    {
        bool ok = NumberFormatSpec.TryNormalize(
            ",",
            groupSeparator,
            out string normalizedDecimal,
            out string normalizedGroup,
            out NumberSeparatorStyle style);

        Assert.True(ok);
        Assert.Equal(",", normalizedDecimal);
        Assert.Equal(" ", normalizedGroup);
        Assert.Equal(NumberSeparatorStyle.CommaSpace, style);
    }

    [Theory]
    [InlineData(".", "")]
    [InlineData(",", "")]
    [InlineData("", ",")]
    [InlineData("", ".")]
    [InlineData(" ", ",")]
    [InlineData(".", " ")]
    [InlineData(".", ".")]
    [InlineData(",", ",")]
    [InlineData("'", ",")]
    [InlineData(",", "'")]
    [InlineData("..", ",")]
    [InlineData(",", " ,")]
    [InlineData("x", ",")]
    public void TryNormalize_RejectsOneSidedIdenticalOrUnsupportedPairs(
        string decimalSeparator,
        string groupSeparator)
    {
        bool ok = NumberFormatSpec.TryNormalize(
            decimalSeparator,
            groupSeparator,
            out string normalizedDecimal,
            out string normalizedGroup,
            out NumberSeparatorStyle style);

        Assert.False(ok);
        Assert.Equal("", normalizedDecimal);
        Assert.Equal("", normalizedGroup);
        Assert.Equal(NumberSeparatorStyle.System, style);
    }
}
