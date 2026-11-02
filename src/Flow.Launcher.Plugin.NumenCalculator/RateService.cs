using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Flow.Launcher.Plugin;

namespace Flow.Launcher.Plugin.NumenCalculator;

internal interface IRateHttpSource
{
    Task<string> GetRawJsonAsync(Uri endpoint, CancellationToken cancellationToken);
}

internal interface IRateCache
{
    bool TryLoad(out RateCacheEnvelope envelope);
    void Save(RateCacheEnvelope envelope);
}

internal interface IRateSink
{
    int SetRates(IReadOnlyList<RateEntry> rates);   // returns numen_status
}

internal interface IRateClock
{
    DateTimeOffset UtcNow { get; }
}

internal interface IRateTimer : IDisposable
{
    void Start(TimeSpan period, Func<CancellationToken, Task> onTick);
}

internal readonly record struct RateEntry(string Code, double UnitsPerUsd);

internal sealed record RateCacheEnvelope(int Schema, DateTimeOffset FetchedAtUtc, string Body)
{
    public const int CurrentSchema = 1;
}

internal sealed record RateSnapshot(
    IReadOnlyDictionary<string, double> UnitsPerUsd,
    DateTimeOffset FetchedAtUtc,
    string RawBody);

internal readonly record struct RateRefreshResult(bool Applied, bool Persisted, string? Error);

/// <summary>
/// Parses the Vicinae currencies payload into Numen's units-per-USD map. Pure and network-free.
/// </summary>
internal static class RateParser
{
    public static bool TryParse(
        string body,
        DateTimeOffset fetchedAtUtc,
        out RateSnapshot snapshot,
        out string? error)
    {
        snapshot = null!;
        error = null;

        if (string.IsNullOrWhiteSpace(body))
        {
            error = "The rates payload is empty.";
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException exception)
        {
            error = $"The rates payload is not valid JSON: {exception.Message}";
            return false;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "The rates payload root must be a JSON object.";
                return false;
            }

            if (!root.TryGetProperty("base", out JsonElement baseElement) ||
                baseElement.ValueKind != JsonValueKind.String ||
                !string.Equals(baseElement.GetString(), "USD", StringComparison.Ordinal))
            {
                error = "The rates payload base must be USD.";
                return false;
            }

            if (!root.TryGetProperty("fiat", out JsonElement fiatElement) ||
                fiatElement.ValueKind != JsonValueKind.Object ||
                !fiatElement.TryGetProperty("rates", out JsonElement fiatRates) ||
                fiatRates.ValueKind != JsonValueKind.Object)
            {
                error = "The rates payload is missing fiat.rates.";
                return false;
            }

            var rates = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (JsonProperty property in fiatRates.EnumerateObject())
            {
                if (!TryReadPositiveValue(property.Value, out double value))
                    continue;

                string code = NormalizeCode(property.Name);
                if (code.Length == 0)
                    continue;

                rates[code] = value;
            }

            if (root.TryGetProperty("crypto", out JsonElement cryptoElement) &&
                cryptoElement.ValueKind != JsonValueKind.Null)
            {
                if (cryptoElement.ValueKind != JsonValueKind.Object ||
                    !cryptoElement.TryGetProperty("prices", out JsonElement cryptoPrices) ||
                    cryptoPrices.ValueKind != JsonValueKind.Object)
                {
                    error = "The rates payload crypto section is malformed.";
                    return false;
                }

                foreach (JsonProperty property in cryptoPrices.EnumerateObject())
                {
                    if (!TryReadPositiveValue(property.Value, out double price))
                        continue;

                    double unitsPerUsd = 1.0 / price;
                    if (!double.IsFinite(unitsPerUsd) || unitsPerUsd <= 0)
                        continue;

                    string code = NormalizeCode(property.Name);
                    if (code.Length == 0)
                        continue;

                    // Crypto is applied after fiat so a ticker collision resolves to the crypto price.
                    rates[code] = unitsPerUsd;
                }
            }

            // The native map deliberately does not synthesize USD; the C# snapshot owns this invariant.
            rates["usd"] = 1.0;

            snapshot = new RateSnapshot(
                new ReadOnlyDictionary<string, double>(rates),
                fetchedAtUtc,
                body);
            return true;
        }
    }

    private static bool TryReadPositiveValue(JsonElement element, out double value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Number)
            return false;

        double parsed;
        try
        {
            if (!element.TryGetDouble(out parsed))
                return false;
        }
        catch (Exception)
        {
            // A number outside the double range is an invalid rate, not a payload failure.
            return false;
        }

        if (!double.IsFinite(parsed) || parsed <= 0)
            return false;

        value = parsed;
        return true;
    }

    private static string NormalizeCode(string code)
    {
        if (code.Length == 0)
            return string.Empty;

        return string.Create(code.Length, code, static (characters, source) =>
        {
            for (int index = 0; index < characters.Length; index++)
            {
                char value = source[index];
                characters[index] = value is >= 'A' and <= 'Z' ? (char)(value + ('a' - 'A')) : value;
            }
        });
    }
}

/// <summary>
/// Owns HTTP fetching, parsing, caching, refresh scheduling, and native rate publication.
/// </summary>
internal sealed class RateService : IDisposable
{
    private const string ClassName = nameof(RateService);
    private const int NumenOk = 0;
    internal const string DefaultEndpoint = "https://api.vicinae.com/v1/currencies";
    private static readonly Uri DefaultEndpointUri = new(DefaultEndpoint, UriKind.Absolute);
    private static readonly TimeSpan StartupRefreshAge = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan FutureTimestampTolerance = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RefreshPeriod = TimeSpan.FromHours(1);

    private readonly IRateHttpSource _http;
    private readonly IRateCache _cache;
    private readonly IRateSink _sink;
    private readonly IRateClock _clock;
    private readonly IRateTimer _timer;
    private readonly IPublicAPI _api;
    private readonly PluginSettings _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _flightSync = new();

    private Task<RateRefreshResult>? _inFlight;
    private RateSnapshot? _snapshot;
    private int _initialized;
    private int _disposed;

    public RateService(
        IRateHttpSource http,
        IRateCache cache,
        IRateSink sink,
        IRateClock clock,
        IRateTimer timer,
        IPublicAPI api,
        PluginSettings settings)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _timer = timer ?? throw new ArgumentNullException(nameof(timer));
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        if (cache is FileRateCache fileCache)
            fileCache.AttachLogger(api);
    }

    /// <summary>
    /// Loads the disk cache synchronously, optionally starts one background refresh, then starts
    /// the hourly timer. Never performs blocking network I/O.
    /// </summary>
    public void Initialize()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0)
            return;
        if (Volatile.Read(ref _disposed) != 0)
            return;

        LoadCache();

        RateSnapshot? current = Volatile.Read(ref _snapshot);
        if (_settings.RefreshRatesOnStartup && NeedsStartupRefresh(current))
            _ = RefreshInBackgroundAsync();

        try
        {
            _timer.Start(RefreshPeriod, token => RefreshAsync(token));
        }
        catch (Exception exception)
        {
            LogWarn($"Failed to start the hourly rate refresh timer: {exception.Message}");
        }
    }

    /// <summary>
    /// Fetches and publishes a fresh snapshot. Concurrent callers join the in-flight operation.
    /// Never throws; failures leave the current snapshot and cache untouched.
    /// </summary>
    public Task<RateRefreshResult> RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return Task.FromResult(new RateRefreshResult(false, false, "The rate service has been disposed."));

        lock (_flightSync)
        {
            if (_inFlight is not null)
            {
                if (!_inFlight.IsCompleted)
                    return _inFlight;

                _inFlight = null;
            }

            Task<RateRefreshResult> operation = RefreshCoreAsync(cancellationToken);
            _inFlight = operation;
            _ = operation.ContinueWith(
                static (completed, state) =>
                {
                    var service = (RateService)state!;
                    lock (service._flightSync)
                    {
                        if (ReferenceEquals(service._inFlight, completed))
                            service._inFlight = null;
                    }
                },
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return operation;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            _timer.Dispose();
        }
        catch (Exception exception)
        {
            LogWarn($"Failed to dispose the rate refresh timer: {exception.Message}");
        }

        if (_http is IDisposable disposableHttp)
        {
            try
            {
                disposableHttp.Dispose();
            }
            catch (Exception exception)
            {
                LogWarn($"Failed to dispose the rate HTTP source: {exception.Message}");
            }
        }

        _gate.Dispose();
    }

    private void LoadCache()
    {
        try
        {
            if (!_cache.TryLoad(out RateCacheEnvelope envelope))
            {
                LogDebug("No usable rate cache was found.");
                return;
            }

            if (envelope.Schema != RateCacheEnvelope.CurrentSchema)
            {
                LogWarn($"Ignoring rate cache with unsupported schema {envelope.Schema}.");
                return;
            }

            if (!RateParser.TryParse(envelope.Body, envelope.FetchedAtUtc, out RateSnapshot snapshot, out string? error))
            {
                LogWarn($"Ignoring unreadable rate cache: {error}");
                return;
            }

            // The cache is the current known snapshot even if native rejects it; the failure is
            // logged below and the hourly refresh can retry. Never discard a readable cache.
            Volatile.Write(ref _snapshot, snapshot);
            if (TryPublish(snapshot))
                LogDebug($"Loaded {snapshot.UnitsPerUsd.Count} rates from cache.");
        }
        catch (Exception exception)
        {
            LogWarn($"Failed to load the rate cache: {exception.Message}");
        }
    }

    private bool NeedsStartupRefresh(RateSnapshot? current)
    {
        if (current is null)
            return true;

        TimeSpan age = _clock.UtcNow - current.FetchedAtUtc;
        return age >= StartupRefreshAge || age < -FutureTimestampTolerance;
    }

    private async Task RefreshInBackgroundAsync()
    {
        try
        {
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogWarn($"Background rate refresh failed: {exception.Message}");
        }
    }

    private async Task<RateRefreshResult> RefreshCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await RefreshOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogDebug("Rate refresh was cancelled.");
            return new RateRefreshResult(false, false, "Rate refresh was cancelled.");
        }
        catch (ObjectDisposedException)
        {
            return new RateRefreshResult(false, false, "The rate service has been disposed.");
        }
        catch (Exception exception)
        {
            LogWarn($"Rate refresh failed: {exception.Message}");
            return new RateRefreshResult(false, false, exception.Message);
        }
    }

    private async Task<RateRefreshResult> RefreshOnceAsync(CancellationToken cancellationToken)
    {
        Uri endpoint = ResolveEndpoint();

        string body;
        try
        {
            body = await _http.GetRawJsonAsync(endpoint, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogDebug("Rate refresh was cancelled while fetching.");
            return new RateRefreshResult(false, false, "Rate refresh was cancelled.");
        }
        catch (Exception exception)
        {
            LogWarn($"Failed to fetch rates from {endpoint}: {exception.Message}");
            return new RateRefreshResult(false, false, exception.Message);
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            LogWarn($"The rate endpoint {endpoint} returned an empty body.");
            return new RateRefreshResult(false, false, "The rate endpoint returned an empty body.");
        }

        if (!RateParser.TryParse(body, _clock.UtcNow, out RateSnapshot snapshot, out string? error))
        {
            LogWarn($"Rejected the rates payload from {endpoint}: {error}");
            return new RateRefreshResult(false, false, error ?? "The rates payload was rejected.");
        }

        if (!TryPublish(snapshot))
            return new RateRefreshResult(false, false, "numen_set_rates rejected the rate snapshot.");

        try
        {
            _cache.Save(new RateCacheEnvelope(
                RateCacheEnvelope.CurrentSchema,
                snapshot.FetchedAtUtc,
                body));
        }
        catch (Exception exception)
        {
            LogWarn($"Failed to write the rate cache: {exception.Message}");
            return new RateRefreshResult(true, false, exception.Message);
        }

        return new RateRefreshResult(true, true, null);
    }

    private bool TryPublish(RateSnapshot snapshot)
    {
        IReadOnlyList<RateEntry> entries = snapshot.UnitsPerUsd
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => new RateEntry(pair.Key, pair.Value))
            .ToArray();

        int status;
        try
        {
            status = _sink.SetRates(entries);
        }
        catch (Exception exception)
        {
            LogWarn($"numen_set_rates threw: {exception.Message}");
            return false;
        }

        if (status != NumenOk)
        {
            LogWarn($"numen_set_rates rejected the rate snapshot with status {status}.");
            return false;
        }

        Volatile.Write(ref _snapshot, snapshot);
        return true;
    }

    private Uri ResolveEndpoint()
    {
        string? configured = _settings.RateEndpoint;
        if (!string.IsNullOrWhiteSpace(configured) &&
            Uri.TryCreate(configured.Trim(), UriKind.Absolute, out Uri? endpoint) &&
            (endpoint.Scheme == Uri.UriSchemeHttp || endpoint.Scheme == Uri.UriSchemeHttps))
        {
            return endpoint;
        }

        return DefaultEndpointUri;
    }

    private void LogDebug(string message) => _api.LogDebug(ClassName, message);

    private void LogWarn(string message) => _api.LogWarn(ClassName, message);
}

/// <summary>
/// Production HTTP source. Owns one <see cref="HttpClient"/> for the plugin lifetime.
/// </summary>
internal sealed class HttpRateSource : IRateHttpSource, IDisposable
{
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    internal const string UserAgentValue = "FlowLauncher-NumenCalculator/0.1.0";

    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public HttpRateSource()
    {
        _client = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        })
        {
            Timeout = RequestTimeout,
        };
        _ownsClient = true;
    }

    internal HttpRateSource(HttpClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ownsClient = false;
    }

    public async Task<string> GetRawJsonAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd(UserAgentValue);

        using HttpResponseMessage response = await _client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_ownsClient)
            _client.Dispose();
    }
}

/// <summary>
/// JSON envelope cache at %LOCALAPPDATA%\FlowLauncher\Cache\Plugins\....\rates-v1.json.
/// </summary>
internal sealed class FileRateCache : IRateCache
{
    internal const string FileName = "rates-v1.json";

    private readonly string _filePath;
    private Action<string>? _warn;

    public FileRateCache()
        : this(GetDefaultPath())
    {
    }

    internal FileRateCache(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("A cache file path is required.", nameof(filePath));

        _filePath = filePath;
    }

    internal static string GetDefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FlowLauncher",
        "Cache",
        "Plugins",
        "Flow.Launcher.Plugin.NumenCalculator",
        FileName);

    internal string FilePath => _filePath;

    internal void AttachLogger(IPublicAPI api)
    {
        ArgumentNullException.ThrowIfNull(api);
        _warn = message => api.LogWarn(nameof(FileRateCache), message);
    }

    public bool TryLoad(out RateCacheEnvelope envelope)
    {
        envelope = null!;

        try
        {
            if (!File.Exists(_filePath))
                return false;

            string json = File.ReadAllText(_filePath, Encoding.UTF8);
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                Warn($"Ignoring rate cache at {_filePath}: the root is not a JSON object.");
                return false;
            }

            if (!root.TryGetProperty("schema", out JsonElement schemaElement) ||
                schemaElement.ValueKind != JsonValueKind.Number ||
                !schemaElement.TryGetInt32(out int schema) ||
                schema != RateCacheEnvelope.CurrentSchema)
            {
                Warn($"Ignoring rate cache at {_filePath}: unsupported schema.");
                return false;
            }

            if (!root.TryGetProperty("fetchedAtUtc", out JsonElement fetchedElement) ||
                fetchedElement.ValueKind != JsonValueKind.String ||
                !DateTimeOffset.TryParse(
                    fetchedElement.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out DateTimeOffset fetchedAtUtc))
            {
                Warn($"Ignoring rate cache at {_filePath}: invalid fetchedAtUtc.");
                return false;
            }

            if (!root.TryGetProperty("body", out JsonElement bodyElement) ||
                bodyElement.ValueKind != JsonValueKind.String ||
                bodyElement.GetString() is not { } body)
            {
                Warn($"Ignoring rate cache at {_filePath}: invalid body.");
                return false;
            }

            envelope = new RateCacheEnvelope(schema, fetchedAtUtc.ToUniversalTime(), body);
            return true;
        }
        catch (Exception exception)
        {
            Warn($"Ignoring unreadable rate cache at {_filePath}: {exception.Message}");
            return false;
        }
    }

    public void Save(RateCacheEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        string? directory = Path.GetDirectoryName(_filePath);
        string tempPath = _filePath + ".tmp";

        try
        {
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            string json = JsonSerializer.Serialize(new
            {
                schema = envelope.Schema,
                fetchedAtUtc = envelope.FetchedAtUtc.ToUniversalTime().UtcDateTime
                    .ToString("O", CultureInfo.InvariantCulture),
                body = envelope.Body,
            });

            byte[] bytes = Encoding.UTF8.GetBytes(json);
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, _filePath, overwrite: true);
        }
        catch (Exception)
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch (Exception cleanupException)
            {
                Warn($"Failed to delete the rate cache temp file at {tempPath}: {cleanupException.Message}");
            }

            throw;
        }
    }

    private void Warn(string message) => _warn?.Invoke(message);
}

/// <summary>
/// Hourly background timer. Failures are owned by the callback; a tick never escapes as an
/// unhandled exception on a thread-pool thread.
/// </summary>
internal sealed class PeriodicRateTimer : IRateTimer
{
    private readonly object _sync = new();
    private Timer? _timer;
    private CancellationTokenSource? _cancellation;
    private bool _disposed;

    public void Start(TimeSpan period, Func<CancellationToken, Task> onTick)
    {
        ArgumentNullException.ThrowIfNull(onTick);
        if (period <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(period), "The timer period must be positive.");

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_timer is not null)
                throw new InvalidOperationException("The rate refresh timer has already been started.");

            _cancellation = new CancellationTokenSource();
            CancellationToken token = _cancellation.Token;
            _timer = new Timer(
                static state =>
                {
                    var (_, callback, cancellationToken) =
                        ((PeriodicRateTimer, Func<CancellationToken, Task>, CancellationToken))state!;
                    _ = PeriodicRateTimer.TickAsync(callback, cancellationToken);
                },
                (this, onTick, token),
                period,
                period);
        }
    }

    private static async Task TickAsync(Func<CancellationToken, Task> onTick, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return;

        try
        {
            await onTick(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown in progress.
        }
        catch (Exception)
        {
            // The callback owns failure logging; the timer must stay alive.
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            _timer?.Dispose();
            _timer = null;
            _cancellation?.Cancel();
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }
}

internal sealed class SystemRateClock : IRateClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
