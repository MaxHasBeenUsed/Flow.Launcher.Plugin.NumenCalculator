using System.Globalization;
using Xunit;

namespace Flow.Launcher.Plugin.NumenCalculator.Tests;

public sealed class LocaleMapperTests
{
    [Theory]
    [InlineData("en-US", "en_US")]
    [InlineData("en-GB", "en_GB")]
    [InlineData("de-DE", "de_DE")]
    [InlineData("fr-FR", "fr_FR")]
    [InlineData("pt-BR", "pt_BR")]
    [InlineData("en-IN", "en_IN")]
    [InlineData("zh-Hans-CN", "zh_CN")]
    [InlineData("zh-Hant-TW", "zh_TW")]
    [InlineData("sr-Latn-RS", "sr_RS")]
    [InlineData("ja-JP", "ja_JP")]
    [InlineData("fr_FR.UTF-8@euro", "fr_FR")]
    public void ToNumenLocale_MapsKnownCultureNames(string cultureName, string expected)
    {
        Assert.Equal(expected, LocaleMapper.ToNumenLocale(NumberSeparatorStyle.System, cultureName));
    }

    [Theory]
    [InlineData("fr", "fr")]
    [InlineData("zh-Hans", "zh")]
    [InlineData("es-419", "es")]
    [InlineData("zh-Hans-419", "zh")]
    [InlineData("de-DE-1996", "de_DE")]
    [InlineData("en_US_POSIX.UTF-8", "en_US")]
    [InlineData("zh-Hans-CN-x-private", "zh_CN")]
    [InlineData("xx-YY", "xx_YY")]
    public void ToNumenLocale_HandlesNeutralAndOddNames(string cultureName, string expected)
    {
        Assert.Equal(expected, LocaleMapper.ToNumenLocale(NumberSeparatorStyle.System, cultureName));
    }

    [Theory]
    [InlineData("EN-us", "en_US")]
    [InlineData(" en-US ", "en_US")]
    [InlineData("Sr-latn-rs", "sr_RS")]
    public void ToNumenLocale_NormalizesCaseAndWhitespace(string cultureName, string expected)
    {
        Assert.Equal(expected, LocaleMapper.ToNumenLocale(NumberSeparatorStyle.System, cultureName));
    }

    [Theory]
    [InlineData("@euro")]
    [InlineData(".")]
    [InlineData("_")]
    [InlineData("---")]
    [InlineData("419")]
    public void ToNumenLocale_NeverReturnsEmpty(string cultureName)
    {
        string mapped = LocaleMapper.ToNumenLocale(NumberSeparatorStyle.System, cultureName);
        Assert.False(string.IsNullOrWhiteSpace(mapped));
        Assert.Equal("en_US", mapped);
    }

    [Theory]
    [InlineData(NumberSeparatorStyle.DotComma, "fr-FR", "ja_FR")]
    [InlineData(NumberSeparatorStyle.DotComma, "de-DE", "ja_DE")]
    [InlineData(NumberSeparatorStyle.DotComma, "en-US", "ja_US")]
    [InlineData(NumberSeparatorStyle.CommaDot, "en-US", "nl_US")]
    [InlineData(NumberSeparatorStyle.CommaDot, "fr-FR", "nl_FR")]
    [InlineData(NumberSeparatorStyle.CommaSpace, "zh-CN", "fr_CN")]
    [InlineData(NumberSeparatorStyle.CommaSpace, "fr-FR", "fr_FR")]
    public void ToNumenLocale_ComposesStyleLanguageWithSystemRegion(
        NumberSeparatorStyle style,
        string cultureName,
        string expected)
    {
        Assert.Equal(expected, LocaleMapper.ToNumenLocale(style, cultureName));
    }

    [Theory]
    [InlineData(NumberSeparatorStyle.DotComma, "zh-Hans-CN", "ja_CN")]
    [InlineData(NumberSeparatorStyle.CommaDot, "zh-Hans-CN", "nl_CN")]
    [InlineData(NumberSeparatorStyle.CommaSpace, "sr-Latn-RS", "fr_RS")]
    [InlineData(NumberSeparatorStyle.DotComma, "fr-fr", "ja_FR")]
    [InlineData(NumberSeparatorStyle.CommaDot, " en-US ", "nl_US")]
    public void ToNumenLocale_StyleCompositionDropsScriptAndKeepsRegion(
        NumberSeparatorStyle style,
        string cultureName,
        string expected)
    {
        Assert.Equal(expected, LocaleMapper.ToNumenLocale(style, cultureName));
    }

    [Theory]
    [InlineData(NumberSeparatorStyle.DotComma, "fr", "ja_US")]
    [InlineData(NumberSeparatorStyle.CommaDot, "fr", "nl_DE")]
    [InlineData(NumberSeparatorStyle.CommaSpace, "fr", "fr_FR")]
    [InlineData(NumberSeparatorStyle.DotComma, "zh-Hans", "ja_US")]
    [InlineData(NumberSeparatorStyle.CommaDot, "es-419", "nl_DE")]
    [InlineData(NumberSeparatorStyle.CommaSpace, "zh-Hans-419", "fr_FR")]
    public void ToNumenLocale_StyleWithoutRegionUsesLanguageDefaults(
        NumberSeparatorStyle style,
        string cultureName,
        string expected)
    {
        Assert.Equal(expected, LocaleMapper.ToNumenLocale(style, cultureName));
    }

    [Theory]
    [InlineData(NumberSeparatorStyle.System, "en_US")]
    [InlineData(NumberSeparatorStyle.DotComma, "ja_US")]
    [InlineData(NumberSeparatorStyle.CommaDot, "nl_DE")]
    [InlineData(NumberSeparatorStyle.CommaSpace, "fr_FR")]
    public void ToNumenLocale_StyleWithUnusableNameFallsBackToDefaults(
        NumberSeparatorStyle style,
        string expected)
    {
        foreach (string cultureName in new[] { "@euro", ".", "_", "---", "419", "!!" })
            Assert.Equal(expected, LocaleMapper.ToNumenLocale(style, cultureName));
    }

    [Fact]
    public void ToNumenLocale_EmptyCultureNameUsesCurrentCulture()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");

            Assert.Equal("fr_FR", LocaleMapper.ToNumenLocale(NumberSeparatorStyle.System));
            Assert.Equal("fr_FR", LocaleMapper.ToNumenLocale(NumberSeparatorStyle.System, ""));
            Assert.Equal("fr_FR", LocaleMapper.ToNumenLocale(NumberSeparatorStyle.System, null));
            Assert.Equal("fr_FR", LocaleMapper.ToNumenLocale(NumberSeparatorStyle.System, "   "));
            Assert.Equal("ja_FR", LocaleMapper.ToNumenLocale(NumberSeparatorStyle.DotComma, ""));
            Assert.Equal("nl_FR", LocaleMapper.ToNumenLocale(NumberSeparatorStyle.CommaDot, null));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void ToNumenLocale_InvariantCurrentCultureFallsBackToDefaults()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

            Assert.Equal("en_US", LocaleMapper.ToNumenLocale(NumberSeparatorStyle.System, ""));
            Assert.Equal("en_US", LocaleMapper.ToNumenLocale(NumberSeparatorStyle.System, null));
            Assert.Equal("ja_US", LocaleMapper.ToNumenLocale(NumberSeparatorStyle.DotComma, ""));
            Assert.Equal("nl_DE", LocaleMapper.ToNumenLocale(NumberSeparatorStyle.CommaDot, ""));
            Assert.Equal("fr_FR", LocaleMapper.ToNumenLocale(NumberSeparatorStyle.CommaSpace, ""));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void ToNumenLocale_HandEditedPosixNameKeepsRegionOnly()
    {
        Assert.Equal(
            "fr_FR",
            LocaleMapper.ToNumenLocale(NumberSeparatorStyle.System, "fr_FR.UTF-8@euro"));
        Assert.Equal(
            "de_DE",
            LocaleMapper.ToNumenLocale(NumberSeparatorStyle.System, "de_DE.ISO-8859-1"));
        Assert.Equal(
            "ja_FR",
            LocaleMapper.ToNumenLocale(NumberSeparatorStyle.DotComma, "fr_FR.UTF-8@euro"));
    }
}
