using System.Runtime.InteropServices;
using System.Text;

namespace Flow.Launcher.Plugin.NumenCalculator;

internal enum NumenStatus
{
    Ok = 0,
    InvalidArgument = 1,
    AbiMismatch = 2,
    Expression = 3,
    Internal = 4,
}

internal enum ValueKind
{
    Number = 0,
    DateTime = 1,
    Duration = 2,
    Boolean = 3,
    Text = 4,
}

internal sealed class NumenEvalOptions
{
    public string? Locale { get; init; }
    public bool Strict { get; init; } = true;
    public bool ImplicitCurrency { get; init; } = true;
    public bool ImplicitUnit { get; init; } = true;
    public bool ImplicitTimezone { get; init; } = true;
    public long NowUnixSeconds { get; init; }
    public string? Timezone { get; init; }
    public bool RelativeDates { get; init; } = true;
    public bool WithTimezone { get; init; } = true;
    public bool NeutralDateFormat { get; init; }
    public string? DateLocale { get; init; }
}

internal sealed class NumenEvaluation
{
    public required NumenStatus Status { get; init; }
    public string? Error { get; init; }
    public string? DisplayText { get; init; }
    public required ValueKind Kind { get; init; }
    public double NumberValue { get; init; }
    public string? NumberText { get; init; }
    public bool IsPercentage { get; init; }
    public string? UnitRaw { get; init; }
    public string? UnitRender { get; init; }
    public string? UnitText => UnitRender ?? UnitRaw;
    public string? UnitName { get; init; }
    public int UnitDecimals { get; init; }
    public bool UnitSymbolPrefix { get; init; }
    public DateTimePart? DateTime { get; init; }
    public DurationPart? Duration { get; init; }
    public bool BooleanValue { get; init; }
    public string? TextValue { get; init; }
    public ConversionPart? Conversion { get; init; }
}

internal sealed record DateTimePart(
    long UnixSeconds,
    int Nanos,
    string? TimezoneName,
    int OffsetSeconds,
    string? Rfc3339,
    string? TimezoneString);

internal sealed record DurationPart(
    long Years,
    long Months,
    long Seconds,
    long SubsecondNanos,
    uint PresentMask,
    double TotalSeconds);

internal sealed record ConversionPart(
    int Kind,
    bool Implicit,
    string? FromText,
    string? ToText,
    int FromOffsetSeconds,
    int ToOffsetSeconds);

internal sealed class NumenInterop : IDisposable
{
    private const uint ExpectedAbiVersion = 1;

    private static readonly object InitializeSync = new();
    private static bool _initialized;

    private IntPtr _instance;
    private bool _disposed;

    public static void Initialize(string pluginDirectory)
    {
        lock (InitializeSync)
        {
            if (_initialized)
            {
                return;
            }

            NativeMethods.Initialize(pluginDirectory);

            uint abiVersion = NativeMethods.numen_abi_version();
            if (abiVersion != ExpectedAbiVersion)
            {
                throw new InvalidOperationException(
                    $"numen_c.dll reports ABI version {abiVersion}, but this plugin requires ABI version " +
                    $"{ExpectedAbiVersion}. Deploy the numen_c.dll built for this plugin version.");
            }

            _initialized = true;
        }
    }

    public NumenInterop()
    {
        _instance = NativeMethods.numen_create();
        if (_instance == IntPtr.Zero)
        {
            throw new InvalidOperationException("numen_c.dll did not create an evaluation instance.");
        }

        EngineVersion = Marshal.PtrToStringUTF8(NativeMethods.numen_engine_version()) ?? string.Empty;
    }

    public string EngineVersion { get; }

    public NumenEvaluation Evaluate(string expression, NumenEvalOptions options)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);

        IntPtr expressionPointer = IntPtr.Zero;
        IntPtr localePointer = IntPtr.Zero;
        IntPtr timezonePointer = IntPtr.Zero;
        IntPtr dateLocalePointer = IntPtr.Zero;

        var nativeResult = new NativeMethods.NativeResult
        {
            StructSize = (uint)Marshal.SizeOf<NativeMethods.NativeResult>(),
        };

        try
        {
            expressionPointer = Marshal.StringToCoTaskMemUTF8(expression);
            localePointer = AllocateUtf8(options.Locale);
            timezonePointer = AllocateUtf8(options.Timezone);
            dateLocalePointer = AllocateUtf8(options.DateLocale);

            var nativeOptions = new NativeMethods.NativeEvalOptions
            {
                StructSize = (uint)Marshal.SizeOf<NativeMethods.NativeEvalOptions>(),
                Locale = localePointer,
                Strict = BoolToInt(options.Strict),
                ImplicitCurrency = BoolToInt(options.ImplicitCurrency),
                ImplicitUnit = BoolToInt(options.ImplicitUnit),
                ImplicitTimezone = BoolToInt(options.ImplicitTimezone),
                NowUnixSeconds = options.NowUnixSeconds,
                Timezone = timezonePointer,
                RelativeDates = BoolToInt(options.RelativeDates),
                WithTimezone = BoolToInt(options.WithTimezone),
                DateFormatNeutral = BoolToInt(options.NeutralDateFormat),
                DateLocale = dateLocalePointer,
            };

            NativeMethods.numen_evaluate(
                _instance,
                expressionPointer,
                (nuint)Encoding.UTF8.GetByteCount(expression),
                in nativeOptions,
                ref nativeResult);

            return CopyResult(nativeResult);
        }
        finally
        {
            try
            {
                NativeMethods.numen_result_free(ref nativeResult);
            }
            finally
            {
                FreeUtf8(expressionPointer);
                FreeUtf8(localePointer);
                FreeUtf8(timezonePointer);
                FreeUtf8(dateLocalePointer);
            }
        }
    }

    public NumenStatus SetRates(IReadOnlyDictionary<string, double> unitsPerUsd)
    {
        ArgumentNullException.ThrowIfNull(unitsPerUsd);
        ObjectDisposedException.ThrowIf(_disposed, this);

        int count = unitsPerUsd.Count;
        if (count == 0)
        {
            return NativeMethods.numen_set_rates(_instance, null, 0);
        }

        var nativeRates = new NativeMethods.NativeRate[count];
        var codePointers = new IntPtr[count];

        try
        {
            int index = 0;
            foreach (KeyValuePair<string, double> rate in unitsPerUsd)
            {
                IntPtr codePointer = Marshal.StringToCoTaskMemUTF8(rate.Key);
                codePointers[index] = codePointer;
                nativeRates[index] = new NativeMethods.NativeRate
                {
                    Code = codePointer,
                    UnitsPerUsd = rate.Value,
                };
                index++;
            }

            return NativeMethods.numen_set_rates(_instance, nativeRates, (nuint)count);
        }
        finally
        {
            foreach (IntPtr codePointer in codePointers)
            {
                FreeUtf8(codePointer);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        IntPtr instance = _instance;
        _instance = IntPtr.Zero;

        if (instance != IntPtr.Zero)
        {
            NativeMethods.numen_destroy(instance);
        }
    }

    private static NumenEvaluation CopyResult(NativeMethods.NativeResult result)
    {
        return new NumenEvaluation
        {
            Status = result.Status,
            Error = ReadUtf8(result.Error),
            DisplayText = ReadUtf8(result.DisplayText),
            Kind = result.Kind,
            NumberValue = result.NumberValue,
            NumberText = ReadUtf8(result.NumberText),
            IsPercentage = result.IsPercentage != 0,
            UnitRaw = ReadUtf8(result.UnitRaw),
            UnitRender = ReadUtf8(result.UnitRender),
            UnitName = ReadUtf8(result.UnitName),
            UnitDecimals = result.UnitDecimals,
            UnitSymbolPrefix = result.UnitSymbolPrefix != 0,
            DateTime = result.Kind == ValueKind.DateTime
                ? new DateTimePart(
                    result.UnixSeconds,
                    result.Nanos,
                    ReadUtf8(result.TimezoneName),
                    result.OffsetSeconds,
                    ReadUtf8(result.Rfc3339),
                    ReadUtf8(result.TimezoneString))
                : null,
            Duration = result.Kind == ValueKind.Duration
                ? new DurationPart(
                    result.Years,
                    result.Months,
                    result.Seconds,
                    result.SubsecondNanos,
                    result.PresentMask,
                    result.TotalSeconds)
                : null,
            BooleanValue = result.BooleanValue != 0,
            TextValue = ReadUtf8(result.TextValue),
            Conversion = result.HasConversion != 0
                ? new ConversionPart(
                    result.Conversion.Kind,
                    result.Conversion.Implicit != 0,
                    ReadUtf8(result.Conversion.FromText),
                    ReadUtf8(result.Conversion.ToText),
                    result.Conversion.FromOffsetSeconds,
                    result.Conversion.ToOffsetSeconds)
                : null,
        };
    }

    private static int BoolToInt(bool value) => value ? 1 : 0;

    private static IntPtr AllocateUtf8(string? value) =>
        value is null ? IntPtr.Zero : Marshal.StringToCoTaskMemUTF8(value);

    private static string? ReadUtf8(IntPtr value) =>
        value == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(value);

    private static void FreeUtf8(IntPtr value)
    {
        if (value != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(value);
        }
    }
}
