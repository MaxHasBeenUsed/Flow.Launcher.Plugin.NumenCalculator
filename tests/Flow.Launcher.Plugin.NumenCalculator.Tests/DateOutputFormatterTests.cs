using System.Globalization;
using Xunit;

namespace Flow.Launcher.Plugin.NumenCalculator.Tests;

public sealed class DateOutputFormatterTests
{
    private static readonly DateTimeOffset Instant =
        DateTimeOffset.Parse("2026-01-18T06:30:00Z", CultureInfo.InvariantCulture);

    private static DateTimePart TokyoPart(int nanos = 0) =>
        new(Instant.ToUnixTimeSeconds(), nanos, "Asia/Tokyo", 0, null, null);

    [Fact]
    public void Format_CustomPatternUsesResolvedTimezone()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

            Assert.Equal(
                "2026-01-18 15:30",
                DateOutputFormatter.Format(TokyoPart(), "yyyy-MM-dd HH:mm"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Format_PrintsPatternExactlyWithoutShorteningOrZoneSuffix()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

            string? formatted = DateOutputFormatter.Format(TokyoPart(), "yyyy-MM-dd");
            Assert.Equal("2026-01-18", formatted);

            Assert.Equal(
                "2026-01-18 15:30 +09:00",
                DateOutputFormatter.Format(TokyoPart(), "yyyy-MM-dd HH:mm zzz"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Format_IncludesSubsecondTicks()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

            Assert.Equal(
                "2026-01-18 15:30:00.5000000",
                DateOutputFormatter.Format(TokyoPart(500_000_000), "yyyy-MM-dd HH:mm:ss.fffffff"));
            Assert.Equal(
                "2026-01-18 15:30:00.123",
                DateOutputFormatter.Format(TokyoPart(123_456_789), "yyyy-MM-dd HH:mm:ss.fff"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Format_UnknownZoneWithOffsetUsesThatOffset()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

            var part = new DateTimePart(
                Instant.ToUnixTimeSeconds(),
                0,
                "Mars/Olympus",
                7200,
                null,
                null);

            Assert.Equal(
                "2026-01-18 08:30",
                DateOutputFormatter.Format(part, "yyyy-MM-dd HH:mm"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Format_UnknownZoneWithoutOffsetFallsBackToLocalZone()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

            var part = new DateTimePart(Instant.ToUnixTimeSeconds(), 0, "Mars/Olympus", 0, null, null);

            Assert.NotNull(DateOutputFormatter.Format(part, "yyyy-MM-dd HH:mm"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Format_NullPartOrBlankPatternReturnsNull()
    {
        Assert.Null(DateOutputFormatter.Format(null!, "yyyy-MM-dd"));
        Assert.Null(DateOutputFormatter.Format(TokyoPart(), ""));
        Assert.Null(DateOutputFormatter.Format(TokyoPart(), "   "));
    }

    [Theory]
    [InlineData("yyyy'")]
    [InlineData("yyyy-MM-dd HH:mm:ss.ffffffff")]
    public void Format_InvalidPatternReturnsNull(string pattern)
    {
        Assert.Null(DateOutputFormatter.Format(TokyoPart(), pattern));
    }

    [Fact]
    public void Format_OutOfRangeInstantReturnsNull()
    {
        var part = new DateTimePart(long.MaxValue, 0, "Asia/Tokyo", 0, null, null);

        Assert.Null(DateOutputFormatter.Format(part, "yyyy-MM-dd"));
    }

    [Fact]
    public void Format_OutOfRangeOffsetFallsBackWithoutThrowing()
    {
        var part = new DateTimePart(
            Instant.ToUnixTimeSeconds(),
            0,
            "Mars/Olympus",
            int.MaxValue,
            null,
            null);

        Assert.NotNull(DateOutputFormatter.Format(part, "yyyy-MM-dd HH:mm"));
    }
}
