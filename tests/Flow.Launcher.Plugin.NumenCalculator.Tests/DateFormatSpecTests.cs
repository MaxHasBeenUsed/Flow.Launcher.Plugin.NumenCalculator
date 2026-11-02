using Xunit;

namespace Flow.Launcher.Plugin.NumenCalculator.Tests;

public sealed class DateFormatSpecTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("local")]
    [InlineData("Local")]
    [InlineData(" LOCAL ")]
    public void TryNormalize_EmptyOrLocalMeansSystemCulture(string? input)
    {
        bool ok = DateFormatSpec.TryNormalize(input, out string normalized, out DateOutputMode mode);

        Assert.True(ok);
        Assert.Equal("", normalized);
        Assert.Equal(DateOutputMode.Local, mode);
    }

    [Theory]
    [InlineData("iso")]
    [InlineData("ISO")]
    [InlineData(" Iso ")]
    public void TryNormalize_IsoSelectsNeutralIsoDates(string input)
    {
        bool ok = DateFormatSpec.TryNormalize(input, out string normalized, out DateOutputMode mode);

        Assert.True(ok);
        Assert.Equal("iso", normalized);
        Assert.Equal(DateOutputMode.Iso, mode);
    }

    [Theory]
    [InlineData(" yyyy-MM-dd ", "yyyy-MM-dd")]
    [InlineData("dd/MM/yyyy HH:mm", "dd/MM/yyyy HH:mm")]
    [InlineData("yyyy-MM-ddTHH:mm:sszzz", "yyyy-MM-ddTHH:mm:sszzz")]
    public void TryNormalize_CustomPatternIsTrimmedAndAccepted(string input, string expected)
    {
        bool ok = DateFormatSpec.TryNormalize(input, out string normalized, out DateOutputMode mode);

        Assert.True(ok);
        Assert.Equal(expected, normalized);
        Assert.Equal(DateOutputMode.Custom, mode);
    }

    [Fact]
    public void TryNormalize_PatternLongerThanLimitIsRejected()
    {
        string pattern = new('d', 65);

        bool ok = DateFormatSpec.TryNormalize(pattern, out string normalized, out DateOutputMode mode);

        Assert.False(ok);
        Assert.Equal("", normalized);
        Assert.Equal(DateOutputMode.Local, mode);
    }

    [Theory]
    [InlineData("yyyy-MM\ndd")]
    [InlineData("dd\rMM")]
    [InlineData("dd\r\nMM")]
    public void TryNormalize_LineBreaksAreRejected(string input)
    {
        bool ok = DateFormatSpec.TryNormalize(input, out string normalized, out DateOutputMode mode);

        Assert.False(ok);
        Assert.Equal("", normalized);
        Assert.Equal(DateOutputMode.Local, mode);
    }

    [Theory]
    [InlineData("yyyy'")]
    [InlineData("yyyy-MM-dd HH:mm:ss.ffffffff")]
    public void TryNormalize_UnformattablePatternIsRejected(string input)
    {
        bool ok = DateFormatSpec.TryNormalize(input, out string normalized, out DateOutputMode mode);

        Assert.False(ok);
        Assert.Equal("", normalized);
        Assert.Equal(DateOutputMode.Local, mode);
    }
}
