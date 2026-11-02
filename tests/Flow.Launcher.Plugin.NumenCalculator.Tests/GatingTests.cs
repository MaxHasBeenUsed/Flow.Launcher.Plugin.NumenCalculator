using Xunit;

namespace Flow.Launcher.Plugin.NumenCalculator.Tests;

public class GatingTests
{
    // §8 decision table plus the guard boundaries. Rows whose "no row" verdict comes from Numen's
    // strict parse (not from a static guard) are accepted here and verified by the interop tests.
    [Theory]
    [InlineData("2+2", "2+2", "2+2")]
    [InlineData("1/2", "1/2", "1/2")]
    [InlineData("10:30", "10:30", "10:30")]
    [InlineData("1 km to m", "1 km to m", "1 km to m")]
    [InlineData("100 usd to eur", "100 usd to eur", "100 usd to eur")]
    [InlineData("1 usd", "1 usd", "1 usd")]
    [InlineData("5 pm", "5 pm", "5 pm")]
    [InlineData("min(1;4;8)", "min(1;4;8)", "min(1;4;8)")]
    [InlineData("twenty plus two", "twenty plus two", "twenty plus two")]
    [InlineData("hello world 2024", "hello world 2024", "hello world 2024")]
    [InlineData("tomorrow at 5pm", "tomorrow at 5pm", "tomorrow at 5pm")]
    [InlineData("100 years ago", "100 years ago", "100 years ago")]
    [InlineData("until dec 25", "until dec 25", "until dec 25")]
    [InlineData("what time is it", "what time is it", "time")]
    [InlineData("what's the time?", "what's the time?", "time")]
    [InlineData("today", "today", "today")]
    public void DecisionTable_Accepted(string search, string expectedQuery, string expectedExpression)
    {
        Assert.True(Gating.TryGetGatedQuery(search, out var query, out var expression));
        Assert.Equal(expectedQuery, query);
        Assert.Equal(expectedExpression, expression);
    }

    [Theory]
    [InlineData("2024")]
    [InlineData("-5")]
    [InlineData("2+2=4")]
    [InlineData("1; 2")]
    [InlineData("C:\\Users\\me")]
    [InlineData("https://example.com")]
    [InlineData("sqrt(9")]
    [InlineData("1)")]
    [InlineData("2 +")]
    public void DecisionTable_Rejected(string search)
    {
        Assert.False(Gating.TryGetGatedQuery(search, out var query, out var expression));
        Assert.Equal(string.Empty, query);
        Assert.Equal(string.Empty, expression);
    }

    [Fact]
    public void NullSearch_RejectedWithEmptyOutputs()
    {
        Assert.False(Gating.TryGetGatedQuery(null, out var query, out var expression));
        Assert.Equal(string.Empty, query);
        Assert.Equal(string.Empty, expression);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("5")]
    [InlineData("1+")]
    [InlineData("2+")]
    public void BelowMinimumLength_Rejected(string search)
    {
        Assert.False(Gating.TryGetGatedQuery(search, out _, out _));
    }

    [Theory]
    [InlineData("1+1")]
    [InlineData("5+5")]
    [InlineData("5<9")]
    public void AtMinimumLength_Accepted(string search)
    {
        Assert.True(Gating.TryGetGatedQuery(search, out var query, out var expression));
        Assert.Equal(search, query);
        Assert.Equal(search, expression);
    }

    [Theory]
    [InlineData("2+\n2")]
    [InlineData("1\r\n+2")]
    [InlineData("1+2\r3")]
    public void InternalLineBreak_Rejected(string search)
    {
        Assert.False(Gating.TryGetGatedQuery(search, out _, out _));
    }

    [Theory]
    [InlineData("1;2")]
    [InlineData("2+2;")]
    [InlineData("1; 2")]
    public void TopLevelSemicolon_Rejected(string search)
    {
        Assert.False(Gating.TryGetGatedQuery(search, out _, out _));
    }

    [Theory]
    [InlineData("sqrt(9;16)")]
    [InlineData("min(1;2)")]
    [InlineData("max(min(1;2);3)")]
    public void NestedSemicolon_Accepted(string search)
    {
        Assert.True(Gating.TryGetGatedQuery(search, out var query, out var expression));
        Assert.Equal(search, query);
        Assert.Equal(search, expression);
    }

    [Theory]
    [InlineData("C:\\Users\\me")]
    [InlineData("D:\\temp 1")]
    [InlineData("z:/data 5")]
    [InlineData("\\\\server\\share 1")]
    [InlineData("https://example.com")]
    [InlineData("ftp://host/1")]
    public void PathOrUrl_Rejected(string search)
    {
        Assert.False(Gating.TryGetGatedQuery(search, out _, out _));
    }

    [Theory]
    [InlineData("2+2?3")]
    [InlineData("2+2 / 3")]
    public void QuestionMarkInsideExpression_Accepted(string search)
    {
        Assert.True(Gating.TryGetGatedQuery(search, out var query, out var expression));
        Assert.Equal(search, query);
        Assert.Equal(search, expression);
    }

    [Theory]
    [InlineData("(1+2)*3")]
    [InlineData("[1+2]")]
    [InlineData("{1+2}")]
    [InlineData("(1)")]
    [InlineData("2*(3+(4*5))")]
    public void BalancedBrackets_Accepted(string search)
    {
        Assert.True(Gating.TryGetGatedQuery(search, out var query, out var expression));
        Assert.Equal(search, query);
        Assert.Equal(search, expression);
    }

    [Theory]
    [InlineData("(1]")]
    [InlineData("[1+2)")]
    [InlineData("(1+(2)")]
    [InlineData("(1+2))")]
    [InlineData("{1+2]")]
    public void UnbalancedBrackets_Rejected(string search)
    {
        Assert.False(Gating.TryGetGatedQuery(search, out _, out _));
    }

    [Theory]
    [InlineData("2+\"1\"")]
    [InlineData("3'+4'")]
    [InlineData("\"1\"+\"2\"")]
    public void EvenQuotes_Accepted(string search)
    {
        Assert.True(Gating.TryGetGatedQuery(search, out var query, out var expression));
        Assert.Equal(search, query);
        Assert.Equal(search, expression);
    }

    [Theory]
    [InlineData("2+3'")]
    [InlineData("2+\"1")]
    [InlineData("5\"+2")]
    [InlineData("7'*3")]
    public void OddQuotes_Rejected(string search)
    {
        Assert.False(Gating.TryGetGatedQuery(search, out _, out _));
    }

    [Theory]
    [InlineData("what's the time")]
    [InlineData("what's the time?")]
    public void Contraction_DoesNotTripQuoteGuard(string search)
    {
        Assert.True(Gating.TryGetGatedQuery(search, out var query, out var expression));
        Assert.Equal(search, query);
        Assert.Equal("time", expression);
    }

    [Theory]
    [InlineData("2==2")]
    [InlineData("2!=3")]
    [InlineData("2<=3")]
    [InlineData("2>=3")]
    [InlineData("2<3")]
    [InlineData("2>3")]
    [InlineData("2<>3")]
    [InlineData("1<2")]
    public void CompleteEqualityOperators_Accepted(string search)
    {
        Assert.True(Gating.TryGetGatedQuery(search, out var query, out var expression));
        Assert.Equal(search, query);
        Assert.Equal(search, expression);
    }

    [Theory]
    [InlineData("2=2")]
    [InlineData("2+2=4")]
    [InlineData("=2+2")]
    [InlineData("1+1 = 2")]
    public void LoneEquals_Rejected(string search)
    {
        Assert.False(Gating.TryGetGatedQuery(search, out _, out _));
    }

    [Theory]
    [InlineData("10+")]
    [InlineData("10-")]
    [InlineData("10*")]
    [InlineData("10/")]
    [InlineData("10^")]
    [InlineData("10&")]
    [InlineData("10|")]
    [InlineData("10<")]
    [InlineData("10>")]
    [InlineData("10,")]
    [InlineData("10 + ")]
    public void TrailingBinaryOperator_Rejected(string search)
    {
        Assert.False(Gating.TryGetGatedQuery(search, out _, out _));
    }

    [Theory]
    [InlineData("50%")]
    [InlineData("50 %")]
    [InlineData("1%+2")]
    public void TrailingPercent_Accepted(string search)
    {
        Assert.True(Gating.TryGetGatedQuery(search, out var query, out var expression));
        Assert.Equal(search, query);
        Assert.Equal(search, expression);
    }

    [Theory]
    [InlineData("WHAT TIME IS IT", "time")]
    [InlineData("what is the time", "time")]
    [InlineData("what time is it now", "time")]
    [InlineData("current time", "time")]
    [InlineData("time is it", "time")]
    [InlineData("what is the date", "today")]
    [InlineData("what's the date", "today")]
    [InlineData("what is today's date", "today")]
    [InlineData("what's today's date", "today")]
    [InlineData("what day is it", "today")]
    [InlineData("what day is today", "today")]
    [InlineData("what day is today?", "today")]
    [InlineData("todays date", "today")]
    [InlineData("Today's Date", "today")]
    [InlineData("what's the time ?", "time")]
    public void PhraseNormalization_Accepted(string search, string expectedExpression)
    {
        Assert.True(Gating.TryGetGatedQuery(search, out var query, out var expression));
        Assert.Equal(search.Trim(), query);
        Assert.Equal(expectedExpression, expression);
    }

    [Theory]
    [InlineData("  what   time   is   it  ", "what   time   is   it", "time")]
    [InlineData("  What   Is   The   Date  ", "What   Is   The   Date", "today")]
    public void PhraseNormalization_CollapsesWhitespace(string search, string expectedQuery, string expectedExpression)
    {
        Assert.True(Gating.TryGetGatedQuery(search, out var query, out var expression));
        Assert.Equal(expectedQuery, query);
        Assert.Equal(expectedExpression, expression);
    }

    [Theory]
    [InlineData("  2 + 2  ", "2 + 2", "2 + 2")]
    [InlineData("  2   +   2  ", "2   +   2", "2   +   2")]
    [InlineData("1+1\r\n", "1+1", "1+1")]
    public void TrimmingOnly_OutsidePhrases(string search, string expectedQuery, string expectedExpression)
    {
        Assert.True(Gating.TryGetGatedQuery(search, out var query, out var expression));
        Assert.Equal(expectedQuery, query);
        Assert.Equal(expectedExpression, expression);
    }

    [Theory]
    [InlineData("today")]
    [InlineData("now")]
    [InlineData("tomorrow")]
    [InlineData("yesterday")]
    [InlineData("date")]
    [InlineData("ago")]
    [InlineData("until")]
    [InlineData("monday")]
    [InlineData("friday")]
    [InlineData("december")]
    [InlineData("thirty")]
    [InlineData("trillion")]
    public void TemporalOrNumberWordOnly_Accepted(string search)
    {
        Assert.True(Gating.TryGetGatedQuery(search, out var query, out var expression));
        Assert.Equal(search, query);
        Assert.Equal(search, expression);
    }

    [Theory]
    [InlineData("héllo+2")]
    [InlineData("2+é")]
    [InlineData("2+節")]
    public void UnicodeWithAsciiSignals_Accepted(string search)
    {
        Assert.True(Gating.TryGetGatedQuery(search, out var query, out var expression));
        Assert.Equal(search, query);
        Assert.Equal(search, expression);
    }

    [Theory]
    [InlineData("２＋２")]
    [InlineData("Ⅻ+Ⅹ")]
    public void NonAsciiOnlyInput_Rejected(string search)
    {
        Assert.False(Gating.TryGetGatedQuery(search, out var query, out var expression));
        Assert.Equal(string.Empty, query);
        Assert.Equal(string.Empty, expression);
    }

    [Theory]
    [InlineData("2024")]
    [InlineData("123456")]
    public void BareNumber_Rejected(string search)
    {
        Assert.False(Gating.TryGetGatedQuery(search, out _, out _));
    }
}
