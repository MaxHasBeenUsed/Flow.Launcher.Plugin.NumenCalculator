using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Flow.Launcher.Plugin;
using Moq;
using Xunit;

namespace Flow.Launcher.Plugin.NumenCalculator.Tests;

public sealed class RateServiceTests
{
    private const string DefaultBody = """
        {"base":"USD","fiat":{"rates":{"USD":1,"EUR":0.895588}},"crypto":{"prices":{"BTC":77452}}}
        """;

    private const string CachedBody = """
        {"base":"USD","fiat":{"rates":{"USD":1,"GBP":0.747088}}}
        """;

    [Fact]
    public async Task RefreshAsync_AppliesFiatCryptoAndUsdInvariant()
    {
        using var context = new TestContext();

        RateRefreshResult result = await context.Service.RefreshAsync();

        Assert.True(result.Applied);
        Assert.True(result.Persisted);
        Assert.Null(result.Error);

        IReadOnlyList<RateEntry> entries = Assert.Single(context.Sink.Calls);
        Assert.Equal(new[] { "btc", "eur", "usd" }, entries.Select(entry => entry.Code).ToArray());

        Dictionary<string, double> rates = ToRates(entries);
        Assert.Equal(1.0, rates["usd"]);
        Assert.Equal(0.895588, rates["eur"]);
        Assert.Equal(1.0 / 77452.0, rates["btc"]);
    }

    [Fact]
    public async Task RefreshAsync_InvertsCryptoPricesToUnitsPerUsd()
    {
        using var context = new TestContext();
        context.Http.Handler = (_, _) => Task.FromResult(
            """{"base":"USD","fiat":{"rates":{"USD":1}},"crypto":{"prices":{"BTC":77452,"ETH":2250.49}}}""");

        await context.Service.RefreshAsync();

        Dictionary<string, double> rates = ToRates(Assert.Single(context.Sink.Calls));
        Assert.Equal(1.0 / 77452.0, rates["btc"]);
        Assert.Equal(1.0 / 2250.49, rates["eth"]);
    }

    [Fact]
    public async Task RefreshAsync_CryptoWinsFiatCollision()
    {
        using var context = new TestContext();
        context.Http.Handler = (_, _) => Task.FromResult(
            """{"base":"USD","fiat":{"rates":{"USD":1,"MNT":3610.804102}},"crypto":{"prices":{"MNT":0.476536}}}""");

        await context.Service.RefreshAsync();

        Dictionary<string, double> rates = ToRates(Assert.Single(context.Sink.Calls));
        Assert.Equal(1.0 / 0.476536, rates["mnt"]);
    }

    [Fact]
    public async Task RefreshAsync_LowercasesKeysAndForcesUsdToOne()
    {
        using var context = new TestContext();
        context.Http.Handler = (_, _) => Task.FromResult(
            """{"base":"USD","fiat":{"rates":{"USD":2.0,"EUR":1.5,"Foo":2.0}},"crypto":{"prices":{"BTC":1}}}""");

        await context.Service.RefreshAsync();

        IReadOnlyList<RateEntry> entries = Assert.Single(context.Sink.Calls);
        Assert.Equal(new[] { "btc", "eur", "foo", "usd" }, entries.Select(entry => entry.Code).ToArray());

        Dictionary<string, double> rates = ToRates(entries);
        Assert.Equal(1.0, rates["usd"]);
        Assert.Equal(1.5, rates["eur"]);
        Assert.Equal(2.0, rates["foo"]);
    }

    [Fact]
    public async Task RefreshAsync_SkipsInvalidRateValues()
    {
        using var context = new TestContext();
        context.Http.Handler = (_, _) => Task.FromResult(
            """
            {"base":"USD","fiat":{"rates":{"USD":1,"ZERO":0,"NEGATIVE":-2,"STRING":"3","NULL":null,"BIG":1e400,"OK":2.5}},"crypto":{"prices":{"BAD":0,"GOOD":2}}}
            """);

        await context.Service.RefreshAsync();

        IReadOnlyList<RateEntry> entries = Assert.Single(context.Sink.Calls);
        Assert.Equal(new[] { "good", "ok", "usd" }, entries.Select(entry => entry.Code).ToArray());

        Dictionary<string, double> rates = ToRates(entries);
        Assert.Equal(0.5, rates["good"]);
        Assert.Equal(2.5, rates["ok"]);
        Assert.Equal(1.0, rates["usd"]);
    }

    [Fact]
    public async Task RefreshAsync_RejectsNonUsdBaseAndKeepsPriorSnapshot()
    {
        using var context = new TestContext();
        context.Settings.RefreshRatesOnStartup = false;
        context.Cache.Set(new RateCacheEnvelope(
            RateCacheEnvelope.CurrentSchema,
            context.Clock.UtcNow,
            CachedBody));
        context.Service.Initialize();
        Assert.Single(context.Sink.Calls);

        context.Http.Handler = (_, _) => Task.FromResult(
            """{"base":"EUR","fiat":{"rates":{"USD":1,"EUR":0.9}}}""");

        RateRefreshResult result = await context.Service.RefreshAsync();

        Assert.False(result.Applied);
        Assert.False(result.Persisted);
        Assert.NotNull(result.Error);
        Assert.Contains("base", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Single(context.Sink.Calls);
        Assert.Empty(context.Cache.Saved);
    }

    [Fact]
    public async Task RefreshAsync_HttpFailureKeepsPriorSnapshot()
    {
        using var context = new TestContext();
        context.Settings.RefreshRatesOnStartup = false;
        context.Cache.Set(new RateCacheEnvelope(
            RateCacheEnvelope.CurrentSchema,
            context.Clock.UtcNow,
            CachedBody));
        context.Service.Initialize();

        context.Http.Handler = (_, _) => throw new HttpRequestException("connection refused");

        RateRefreshResult result = await context.Service.RefreshAsync();

        Assert.False(result.Applied);
        Assert.False(result.Persisted);
        Assert.Equal("connection refused", result.Error);
        Assert.Single(context.Sink.Calls);
        Assert.Empty(context.Cache.Saved);
        context.Api.Verify(api => api.ShowMsgError(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task RefreshAsync_RejectsLowercaseBase()
    {
        using var context = new TestContext();
        context.Settings.RefreshRatesOnStartup = false;
        context.Http.Handler = (_, _) => Task.FromResult(
            """{"base":"usd","fiat":{"rates":{"USD":1,"EUR":0.9}}}""");

        RateRefreshResult result = await context.Service.RefreshAsync();

        Assert.False(result.Applied);
        Assert.NotNull(result.Error);
        Assert.Contains("base", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(context.Sink.Calls);
    }

    [Fact]
    public async Task RefreshAsync_AcceptsPayloadWithoutCrypto()
    {
        using var context = new TestContext();
        context.Http.Handler = (_, _) => Task.FromResult(
            """{"base":"USD","fiat":{"rates":{"USD":1,"EUR":0.895588}}}""");

        RateRefreshResult result = await context.Service.RefreshAsync();

        Assert.True(result.Applied);
        Dictionary<string, double> rates = ToRates(Assert.Single(context.Sink.Calls));
        Assert.Equal(new[] { "eur", "usd" }, rates.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray());
        Assert.Equal(0.895588, rates["eur"]);
    }

    [Fact]
    public async Task RefreshAsync_CacheWriteFailureKeepsAppliedSnapshot()
    {
        using var context = new TestContext();
        context.Cache.ThrowOnSave = true;

        RateRefreshResult result = await context.Service.RefreshAsync();

        Assert.True(result.Applied);
        Assert.False(result.Persisted);
        Assert.NotNull(result.Error);
        Assert.Single(context.Sink.Calls);
        Assert.Empty(context.Cache.Saved);
    }

    [Fact]
    public async Task RefreshAsync_SinkFailureDoesNotWriteCache()
    {
        using var context = new TestContext(sinkStatus: 1);

        RateRefreshResult result = await context.Service.RefreshAsync();

        Assert.False(result.Applied);
        Assert.False(result.Persisted);
        Assert.NotNull(result.Error);
        Assert.Contains("numen_set_rates", result.Error!, StringComparison.Ordinal);
        Assert.Single(context.Sink.Calls);
        Assert.Empty(context.Cache.Saved);
    }

    [Fact]
    public async Task RefreshAsync_SuccessWritesCacheWithRawBody()
    {
        using var context = new TestContext();

        RateRefreshResult result = await context.Service.RefreshAsync();

        Assert.True(result.Applied);
        Assert.True(result.Persisted);
        RateCacheEnvelope envelope = Assert.Single(context.Cache.Saved);
        Assert.Equal(RateCacheEnvelope.CurrentSchema, envelope.Schema);
        Assert.Equal(context.Clock.UtcNow, envelope.FetchedAtUtc);
        Assert.Equal(DefaultBody, envelope.Body);
    }

    [Fact]
    public async Task RefreshAsync_AfterFailureAllowsAnotherAttempt()
    {
        using var context = new TestContext(sinkStatus: 1);

        RateRefreshResult failed = await context.Service.RefreshAsync();
        Assert.False(failed.Applied);

        context.Sink.Status = 0;
        RateRefreshResult succeeded = await context.Service.RefreshAsync();

        Assert.True(succeeded.Applied);
        Assert.Equal(2, context.Http.CallCount);
        Assert.Equal(2, context.Sink.Calls.Count);
    }

    [Fact]
    public void Initialize_WithNoCacheAndStartupRefreshEnabledFetchesInBackground()
    {
        using var context = new TestContext();

        context.Service.Initialize();

        Assert.Equal(1, context.Http.CallCount);
        IReadOnlyList<RateEntry> entries = Assert.Single(context.Sink.Calls);
        Assert.Equal(0.895588, ToRates(entries)["eur"]);
        Assert.Single(context.Cache.Saved);
    }

    [Fact]
    public void Initialize_DoesNotAwaitStartupRefresh()
    {
        using var context = new TestContext();
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Http.Handler = (_, _) => release.Task;

        context.Service.Initialize();

        Assert.Equal(1, context.Http.CallCount);
        Assert.Empty(context.Sink.Calls);

        release.SetResult(DefaultBody);
    }

    [Fact]
    public void Initialize_WithStaleCachePushesCacheThenRefreshes()
    {
        using var context = new TestContext();
        context.Cache.Set(new RateCacheEnvelope(
            RateCacheEnvelope.CurrentSchema,
            context.Clock.UtcNow - TimeSpan.FromMinutes(31),
            CachedBody));

        context.Service.Initialize();

        Assert.Equal(2, context.Sink.Calls.Count);
        Assert.Equal(new[] { "gbp", "usd" }, context.Sink.Calls[0].Select(entry => entry.Code).ToArray());
        Assert.Equal(new[] { "btc", "eur", "usd" }, context.Sink.Calls[1].Select(entry => entry.Code).ToArray());
        RateCacheEnvelope saved = Assert.Single(context.Cache.Saved);
        Assert.Equal(DefaultBody, saved.Body);
    }

    [Fact]
    public void Initialize_WithFreshCacheDoesNotFetch()
    {
        using var context = new TestContext();
        context.Cache.Set(new RateCacheEnvelope(
            RateCacheEnvelope.CurrentSchema,
            context.Clock.UtcNow - TimeSpan.FromMinutes(5),
            CachedBody));

        context.Service.Initialize();

        Assert.Equal(0, context.Http.CallCount);
        IReadOnlyList<RateEntry> entries = Assert.Single(context.Sink.Calls);
        Assert.Equal(0.747088, ToRates(entries)["gbp"]);
        Assert.Empty(context.Cache.Saved);
    }

    [Fact]
    public void Initialize_WithStartupRefreshDisabledDoesNotFetchButStartsTimer()
    {
        using var context = new TestContext(refreshOnStartup: false);

        context.Service.Initialize();

        Assert.Equal(0, context.Http.CallCount);
        Assert.Empty(context.Sink.Calls);
        Assert.True(context.Timer.Started);
        Assert.Equal(TimeSpan.FromHours(1), context.Timer.Period);
        Assert.Empty(context.Cache.Saved);
    }

    [Fact]
    public async Task ManualRefresh_BypassesAgeCheckAndStartupToggle()
    {
        using var context = new TestContext(refreshOnStartup: false);
        context.Cache.Set(new RateCacheEnvelope(
            RateCacheEnvelope.CurrentSchema,
            context.Clock.UtcNow - TimeSpan.FromMinutes(1),
            CachedBody));
        context.Service.Initialize();
        Assert.Equal(0, context.Http.CallCount);
        Assert.Single(context.Sink.Calls);

        RateRefreshResult result = await context.Service.RefreshAsync();

        Assert.True(result.Applied);
        Assert.Equal(1, context.Http.CallCount);
        Assert.Equal(2, context.Sink.Calls.Count);
    }

    [Fact]
    public async Task TimerTick_RefreshesEvenWhenStartupRefreshIsDisabled()
    {
        using var context = new TestContext(refreshOnStartup: false);
        context.Cache.Set(new RateCacheEnvelope(
            RateCacheEnvelope.CurrentSchema,
            context.Clock.UtcNow,
            CachedBody));
        context.Service.Initialize();
        Assert.Equal(0, context.Http.CallCount);

        await context.Timer.RaiseTickAsync();

        Assert.Equal(1, context.Http.CallCount);
        Assert.Equal(2, context.Sink.Calls.Count);
        Assert.Equal(DefaultBody, Assert.Single(context.Cache.Saved).Body);
    }

    [Fact]
    public void Initialize_IgnoresCacheWithUnparseableBodyAndRefreshes()
    {
        using var context = new TestContext();
        context.Cache.Set(new RateCacheEnvelope(
            RateCacheEnvelope.CurrentSchema,
            context.Clock.UtcNow,
            "not-json"));

        context.Service.Initialize();

        Assert.Equal(1, context.Http.CallCount);
        IReadOnlyList<RateEntry> entries = Assert.Single(context.Sink.Calls);
        Assert.Equal(0.895588, ToRates(entries)["eur"]);
        context.Api.Verify(
            api => api.LogWarn(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public void Initialize_IgnoresSchemaMismatchAndRefreshes()
    {
        using var context = new TestContext();
        context.Cache.Set(new RateCacheEnvelope(RateCacheEnvelope.CurrentSchema + 1, context.Clock.UtcNow, CachedBody));

        context.Service.Initialize();

        Assert.Equal(1, context.Http.CallCount);
        IReadOnlyList<RateEntry> entries = Assert.Single(context.Sink.Calls);
        Assert.Equal(0.895588, ToRates(entries)["eur"]);
    }

    [Fact]
    public async Task RefreshAsync_UsesConfiguredEndpoint()
    {
        using var context = new TestContext();
        context.Settings.RateEndpoint = "https://rates.example.test/v1/currencies";

        await context.Service.RefreshAsync();

        Uri endpoint = Assert.Single(context.Http.Endpoints);
        Assert.Equal("https://rates.example.test/v1/currencies", endpoint.AbsoluteUri);
    }

    [Fact]
    public async Task RefreshAsync_FallsBackToDefaultEndpointWhenSettingIsInvalid()
    {
        using var context = new TestContext();
        context.Settings.RateEndpoint = "not a uri";

        await context.Service.RefreshAsync();

        Uri endpoint = Assert.Single(context.Http.Endpoints);
        Assert.Equal("https://api.vicinae.com/v1/currencies", endpoint.AbsoluteUri);
    }

    [Fact]
    public async Task RefreshAsync_ConcurrentCallersShareOneRequest()
    {
        using var context = new TestContext();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        context.Http.Handler = async (_, cancellationToken) =>
        {
            entered.TrySetResult(true);
            await release.Task.WaitAsync(cancellationToken);
            return DefaultBody;
        };

        Task<RateRefreshResult> first = context.Service.RefreshAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task<RateRefreshResult> second = context.Service.RefreshAsync();
        Assert.Same(first, second);
        Assert.False(second.IsCompleted);

        release.SetResult(true);
        RateRefreshResult[] results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, context.Http.CallCount);
        Assert.Equal(1, context.Http.MaxConcurrency);
        Assert.All(results, result => Assert.True(result.Applied));
    }

    private static Dictionary<string, double> ToRates(IEnumerable<RateEntry> entries) =>
        entries.ToDictionary(entry => entry.Code, entry => entry.UnitsPerUsd, StringComparer.Ordinal);

    private sealed class TestContext : IDisposable
    {
        public TestContext(int sinkStatus = 0, bool refreshOnStartup = true)
        {
            Http.Handler = (_, _) => Task.FromResult(DefaultBody);
            Sink.Status = sinkStatus;
            Settings.RefreshRatesOnStartup = refreshOnStartup;
            Service = new RateService(Http, Cache, Sink, Clock, Timer, Api.Object, Settings);
        }

        public StubHttpSource Http { get; } = new();

        public InMemoryRateCache Cache { get; } = new();

        public RecordingSink Sink { get; } = new();

        public ManualClock Clock { get; } = new();

        public ManualTimer Timer { get; } = new();

        public Mock<IPublicAPI> Api { get; } = new();

        public PluginSettings Settings { get; } = new();

        public RateService Service { get; }

        public void Dispose() => Service.Dispose();
    }

    private sealed class StubHttpSource : IRateHttpSource
    {
        private int _active;
        private int _callCount;
        private int _maxConcurrency;

        public Func<Uri, CancellationToken, Task<string>> Handler { get; set; } =
            (_, _) => Task.FromResult(string.Empty);

        public ConcurrentQueue<Uri> Endpoints { get; } = new();

        public int CallCount => Volatile.Read(ref _callCount);

        public int MaxConcurrency => Volatile.Read(ref _maxConcurrency);

        public async Task<string> GetRawJsonAsync(Uri endpoint, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            Endpoints.Enqueue(endpoint);

            int active = Interlocked.Increment(ref _active);
            int observed;
            while (active > (observed = Volatile.Read(ref _maxConcurrency)) &&
                   Interlocked.CompareExchange(ref _maxConcurrency, active, observed) != observed)
            {
            }

            try
            {
                return await Handler(endpoint, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private sealed class InMemoryRateCache : IRateCache
    {
        public RateCacheEnvelope? Envelope { get; set; }

        public bool ThrowOnSave { get; set; }

        public List<RateCacheEnvelope> Saved { get; } = [];

        public void Set(RateCacheEnvelope envelope) => Envelope = envelope;

        public bool TryLoad(out RateCacheEnvelope envelope)
        {
            envelope = Envelope!;
            return Envelope is not null;
        }

        public void Save(RateCacheEnvelope envelope)
        {
            if (ThrowOnSave)
                throw new IOException("cache disk full");

            Saved.Add(envelope);
            Envelope = envelope;
        }
    }

    private sealed class RecordingSink : IRateSink
    {
        public int Status { get; set; }

        public List<IReadOnlyList<RateEntry>> Calls { get; } = [];

        public int SetRates(IReadOnlyList<RateEntry> rates)
        {
            Calls.Add(rates);
            return Status;
        }
    }

    private sealed class ManualClock : IRateClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class ManualTimer : IRateTimer
    {
        public bool Started { get; private set; }

        public TimeSpan Period { get; private set; }

        public bool Disposed { get; private set; }

        public Func<CancellationToken, Task>? OnTick { get; private set; }

        public void Start(TimeSpan period, Func<CancellationToken, Task> onTick)
        {
            Started = true;
            Period = period;
            OnTick = onTick;
        }

        public Task RaiseTickAsync() => OnTick!(CancellationToken.None);

        public void Dispose() => Disposed = true;
    }
}

public sealed class FileRateCacheTests
{
    [Fact]
    public void DefaultPath_IsUnderLocalApplicationData()
    {
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FlowLauncher",
            "Cache",
            "Plugins",
            "Flow.Launcher.Plugin.NumenCalculator",
            "rates-v1.json");

        Assert.Equal(expected, FileRateCache.GetDefaultPath());
    }

    [Fact]
    public void SaveAndLoad_RoundTripsEnvelope()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = Path.Combine(directory, "rates-v1.json");
            var cache = new FileRateCache(path);
            var envelope = new RateCacheEnvelope(
                RateCacheEnvelope.CurrentSchema,
                new DateTimeOffset(2026, 10, 31, 15, 16, 49, TimeSpan.Zero).AddTicks(1234567),
                """{"base":"USD","fiat":{"rates":{"USD":1}}}""");

            cache.Save(envelope);

            Assert.True(cache.TryLoad(out RateCacheEnvelope loaded));
            Assert.Equal(envelope.Schema, loaded.Schema);
            Assert.Equal(envelope.Body, loaded.Body);
            Assert.Equal(envelope.FetchedAtUtc, loaded.FetchedAtUtc);
            Assert.Equal(TimeSpan.Zero, loaded.FetchedAtUtc.Offset);
            Assert.False(File.Exists(path + ".tmp"));

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(1, document.RootElement.GetProperty("schema").GetInt32());
            Assert.Equal(envelope.Body, document.RootElement.GetProperty("body").GetString());
            Assert.EndsWith("Z", document.RootElement.GetProperty("fetchedAtUtc").GetString()!);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void Save_OverwritesExistingEnvelope()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = Path.Combine(directory, "rates-v1.json");
            var cache = new FileRateCache(path);
            cache.Save(new RateCacheEnvelope(1, DateTimeOffset.UnixEpoch, """{"first":true}"""));
            cache.Save(new RateCacheEnvelope(1, DateTimeOffset.UnixEpoch.AddHours(1), """{"second":true}"""));

            Assert.True(cache.TryLoad(out RateCacheEnvelope loaded));
            Assert.Equal("""{"second":true}""", loaded.Body);
            Assert.Single(Directory.GetFiles(directory));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void TryLoad_ReturnsFalseForMissingFile()
    {
        string directory = CreateTempDirectory();
        try
        {
            var cache = new FileRateCache(Path.Combine(directory, "rates-v1.json"));
            Assert.False(cache.TryLoad(out _));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void TryLoad_IgnoresCorruptJson()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = Path.Combine(directory, "rates-v1.json");
            File.WriteAllText(path, "{ this is not json");
            var cache = new FileRateCache(path);

            Assert.False(cache.TryLoad(out _));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void TryLoad_IgnoresSchemaMismatch()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = Path.Combine(directory, "rates-v1.json");
            File.WriteAllText(
                path,
                """{"schema":2,"fetchedAtUtc":"2026-10-31T15:16:49.0000000Z","body":"{}"}""");
            var cache = new FileRateCache(path);

            Assert.False(cache.TryLoad(out _));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void TryLoad_IgnoresInvalidFetchedAt()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = Path.Combine(directory, "rates-v1.json");
            File.WriteAllText(
                path,
                """{"schema":1,"fetchedAtUtc":"not-a-timestamp","body":"{}"}""");
            var cache = new FileRateCache(path);

            Assert.False(cache.TryLoad(out _));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "numen-rate-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }
}
