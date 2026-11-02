using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Flow.Launcher.Plugin.NumenCalculator;

internal static class NativeMethods
{
    internal const string LibraryName = "numen_c";
    private const string LibraryFileName = LibraryName + ".dll";

    private static readonly object Sync = new();
    private static bool _resolverInstalled;
    private static IntPtr _handle;

    static NativeMethods()
    {
        EnsureStructSize<NativeEvalOptions>(72);
        EnsureStructSize<NativeResult>(248);
        EnsureStructSize<NativeConversion>(40);
        EnsureStructSize<NativeRate>(16);
    }

    internal static void Initialize(string pluginDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDirectory);

        string libraryPath = Path.GetFullPath(Path.Combine(pluginDirectory, LibraryFileName));

        if (!File.Exists(libraryPath))
        {
            throw new FileNotFoundException(
                $"The native library '{LibraryFileName}' was not found at '{libraryPath}'. " +
                "Build the native library with build.ps1 and copy numen_c.dll next to the plugin assembly.",
                libraryPath);
        }

        lock (Sync)
        {
            if (!_resolverInstalled)
            {
                NativeLibrary.SetDllImportResolver(typeof(NativeMethods).Assembly, Resolve);
                _resolverInstalled = true;
            }

            if (_handle != IntPtr.Zero)
            {
                return;
            }

            try
            {
                _handle = NativeLibrary.Load(libraryPath);
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    $"Failed to load the native library '{LibraryFileName}' from '{libraryPath}'. " +
                    "Ensure it is an x64 build produced by build.ps1.",
                    ex);
            }
        }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!IsNumenLibrary(libraryName))
        {
            return IntPtr.Zero;
        }

        IntPtr handle;
        lock (Sync)
        {
            handle = _handle;
        }

        if (handle == IntPtr.Zero)
        {
            throw new DllNotFoundException(
                $"The native library '{LibraryFileName}' is not loaded. Call " +
                $"{nameof(NativeMethods)}.{nameof(Initialize)}(pluginDirectory) before using numen_c.");
        }

        return handle;
    }

    private static bool IsNumenLibrary(string libraryName)
    {
        return libraryName.Equals(LibraryName, StringComparison.OrdinalIgnoreCase)
            || libraryName.Equals(LibraryFileName, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureStructSize<T>(int expectedSize)
        where T : unmanaged
    {
        int actualSize = Marshal.SizeOf<T>();
        if (actualSize != expectedSize)
        {
            throw new InvalidOperationException(
                $"Managed layout for native struct {typeof(T).Name} is {actualSize} bytes, " +
                $"but the numen_c ABI requires {expectedSize} bytes.");
        }
    }

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint numen_abi_version();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern IntPtr numen_engine_version();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern IntPtr numen_create();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void numen_destroy(IntPtr instance);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern NumenStatus numen_set_rates(IntPtr instance, [In] NativeRate[]? rates, nuint count);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern NumenStatus numen_evaluate(
        IntPtr instance,
        IntPtr expression,
        nuint expressionLength,
        in NativeEvalOptions options,
        ref NativeResult result);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void numen_result_free(ref NativeResult result);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void numen_eval_options_init(ref NativeEvalOptions options);

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRate
    {
        internal IntPtr Code;
        internal double UnitsPerUsd;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeEvalOptions
    {
        internal uint StructSize;
        internal IntPtr Locale;
        internal int Strict;
        internal int ImplicitCurrency;
        internal int ImplicitUnit;
        internal int ImplicitTimezone;
        internal long NowUnixSeconds;
        internal IntPtr Timezone;
        internal int RelativeDates;
        internal int WithTimezone;
        internal int DateFormatNeutral;
        internal IntPtr DateLocale;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeConversion
    {
        internal int Kind;
        internal int Implicit;
        internal int HasFrom;
        internal IntPtr FromText;
        internal IntPtr ToText;
        internal int FromOffsetSeconds;
        internal int ToOffsetSeconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeResult
    {
        internal uint StructSize;
        internal NumenStatus Status;
        internal ValueKind Kind;
        internal IntPtr Error;
        internal IntPtr DisplayText;
        internal double NumberValue;
        internal IntPtr NumberText;
        internal int NumberFormat;
        internal int IsPercentage;
        internal int HasUnit;
        internal IntPtr UnitRaw;
        internal IntPtr UnitRender;
        internal IntPtr UnitName;
        internal int UnitDecimals;
        internal int UnitSymbolPrefix;
        internal long UnixSeconds;
        internal int Nanos;
        internal int OffsetSeconds;
        internal IntPtr TimezoneName;
        internal IntPtr Rfc3339;
        internal IntPtr TimezoneString;
        internal long Years;
        internal long Months;
        internal long Seconds;
        internal long SubsecondNanos;
        internal uint PresentMask;
        internal double TotalSeconds;
        internal int BooleanValue;
        internal IntPtr TextValue;
        internal int HasConversion;
        internal NativeConversion Conversion;
    }
}
