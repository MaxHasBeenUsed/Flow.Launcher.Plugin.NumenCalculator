using System.IO;
using System.Runtime.InteropServices;
using Flow.Launcher.Plugin.NumenCalculator;
using Xunit;

namespace Flow.Launcher.Plugin.NumenCalculator.Tests;

public sealed class InteropTests
{
    private static readonly string PluginDirectory = AppContext.BaseDirectory;

    [Fact]
    public void NativeStructLayoutsMatchAbi()
    {
        Assert.Equal(72, Marshal.SizeOf<NativeMethods.NativeEvalOptions>());
        Assert.Equal(248, Marshal.SizeOf<NativeMethods.NativeResult>());
        Assert.Equal(40, Marshal.SizeOf<NativeMethods.NativeConversion>());
        Assert.Equal(16, Marshal.SizeOf<NativeMethods.NativeRate>());
    }

    [Fact]
    public void NativeAbiVersionIsOne()
    {
        NumenInterop.Initialize(PluginDirectory);

        Assert.Equal(1u, NativeMethods.numen_abi_version());
    }

    [Fact]
    public void NativeEngineVersionIsReported()
    {
        using NumenInterop interop = CreateInterop();

        Assert.Equal("0.8.0", interop.EngineVersion);
    }

    [Fact]
    public void EvaluatesArithmeticAsNumber()
    {
        using NumenInterop interop = CreateInterop();

        NumenEvaluation result = interop.Evaluate("1+1", new NumenEvalOptions());

        Assert.Equal(NumenStatus.Ok, result.Status);
        Assert.Null(result.Error);
        Assert.Equal(ValueKind.Number, result.Kind);
        Assert.Equal("2", result.DisplayText);
        Assert.Equal(2d, result.NumberValue);
        Assert.Null(result.DateTime);
        Assert.Null(result.Duration);
        Assert.Null(result.Conversion);
    }

    [Theory]
    [InlineData("ja_DE", "1,234.5")]
    [InlineData("nl_US", "1.234,5")]
    [InlineData("fr_CN", "1 234,5")]
    public void ComposedLocaleKeepsPunctuationWhileRegionChanges(string locale, string expected)
    {
        using NumenInterop interop = CreateInterop();

        NumenEvaluation result = interop.Evaluate(
            "1234.5",
            new NumenEvalOptions { Locale = locale });

        Assert.Equal(NumenStatus.Ok, result.Status);
        Assert.Null(result.Error);
        Assert.Equal(expected, result.DisplayText);
    }

    [Fact]
    public void ConvertsKilometresToMetres()
    {
        using NumenInterop interop = CreateInterop();

        NumenEvaluation result = interop.Evaluate(
            "1 km to m",
            new NumenEvalOptions { Locale = "en_US" });

        Assert.Equal(NumenStatus.Ok, result.Status);
        Assert.Null(result.Error);
        Assert.Equal(ValueKind.Number, result.Kind);
        Assert.Equal("1,000m", result.DisplayText);
        Assert.Equal(1000d, result.NumberValue);
        Assert.Equal("m", result.UnitRaw);
        Assert.Equal("m", result.UnitRender);
        Assert.Equal("m", result.UnitText);
        ConversionPart conversion = Assert.IsType<ConversionPart>(result.Conversion);
        Assert.Equal(1, conversion.Kind);
        Assert.False(conversion.Implicit);
    }

    [Fact]
    public void RoundTripsUtf8Strings()
    {
        using NumenInterop interop = CreateInterop();

        NumenEvaluation result = interop.Evaluate("\"caf\u00E9\"", new NumenEvalOptions());

        Assert.Equal(NumenStatus.Ok, result.Status);
        Assert.Null(result.Error);
        Assert.Equal(ValueKind.Text, result.Kind);
        Assert.Equal("caf\u00E9", result.TextValue);
        Assert.Equal("caf\u00E9", result.DisplayText);
    }

    [Fact]
    public void RejectsUnknownTimezone()
    {
        using NumenInterop interop = CreateInterop();

        NumenEvaluation result = interop.Evaluate(
            "1+1",
            new NumenEvalOptions { Timezone = "Not/A_Real_Zone" });

        Assert.Equal(NumenStatus.InvalidArgument, result.Status);
        string error = Assert.IsType<string>(result.Error);
        Assert.Contains("Unknown timezone", error);
    }

    [Fact]
    public void SetRatesEnablesCurrencyConversionAndEmptyRatesDisableIt()
    {
        using NumenInterop interop = CreateInterop();

        NumenStatus setStatus = interop.SetRates(
            new Dictionary<string, double>
            {
                ["usd"] = 1d,
                ["eur"] = 0.9d,
            });

        Assert.Equal(NumenStatus.Ok, setStatus);

        NumenEvaluation converted = interop.Evaluate("100 usd to eur", new NumenEvalOptions());

        Assert.Equal(NumenStatus.Ok, converted.Status);
        Assert.Null(converted.Error);
        Assert.Equal(ValueKind.Number, converted.Kind);
        Assert.Equal(90d, converted.NumberValue, 6);

        NumenStatus clearStatus = interop.SetRates(new Dictionary<string, double>());

        Assert.Equal(NumenStatus.Ok, clearStatus);

        NumenEvaluation withoutRates = interop.Evaluate("100 usd to eur", new NumenEvalOptions());

        Assert.NotEqual(NumenStatus.Ok, withoutRates.Status);
        Assert.False(string.IsNullOrWhiteSpace(withoutRates.Error));
    }

    [Fact]
    public void MissingNativeLibraryThrowsInformativeException()
    {
        string missingDirectory = Path.Combine(
            Path.GetTempPath(),
            "numen-missing-" + Guid.NewGuid().ToString("N"));

        FileNotFoundException error = Assert.Throws<FileNotFoundException>(
            () => NativeMethods.Initialize(missingDirectory));

        Assert.Contains("numen_c.dll", error.Message);
        Assert.Contains(Path.GetFullPath(missingDirectory), error.Message);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(missingDirectory, "numen_c.dll")),
            error.FileName);
    }

    [Fact]
    public void DisposeIsIdempotent()
    {
        NumenInterop interop = CreateInterop();

        interop.Dispose();
        interop.Dispose();

        Assert.Throws<ObjectDisposedException>(
            () =>
            {
                interop.Evaluate("1+1", new NumenEvalOptions());
            });
    }

    private static NumenInterop CreateInterop()
    {
        NumenInterop.Initialize(PluginDirectory);
        return new NumenInterop();
    }
}
