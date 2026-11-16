using System.Text;
using Glebosaurus.Cloud;
using Glebosaurus.Core.Cloud;
using Glebosaurus.Core.Config;
using Glebosaurus.Tuya;
using Microsoft.Extensions.Logging.Abstractions;

namespace Glebosaurus.Tests;

/// <summary>Agent w trybie chmurowym: sekcja „cloud”, chroniony token i spięcie części w <see cref="CloudRuntime"/>.</summary>
public sealed class CloudAgentTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private const string Cloud = """ "cloud": { "url": "https://abcdefghijklmnopqrst.supabase.co", "publishableKey": "sb_publishable_przyklad" } """;

    private readonly string directory = Path.Combine(Path.GetTempPath(), $"glebosaurus-agent-{Guid.NewGuid():N}");

    public CloudAgentTests() => Directory.CreateDirectory(directory);

    public void Dispose()
    {
        try { Directory.Delete(directory, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private string Write(string json)
    {
        var path = Path.Combine(directory, "glebosaurus.config.json");
        File.WriteAllText(path, json);
        return path;
    }

    // ---- Sekcja „cloud” ------------------------------------------------------------------------------------------------

    [Fact]
    public void CloudConfig_NeedsNoSensors_AndResolvesFilesNextToConfig()
    {
        var path = Write($$"""{ {{Cloud}}, "database": "bufor.db" }""");

        var config = AgentConfigLoader.Load(path);

        Assert.Empty(config.Sensors);
        Assert.Equal("abcdefghijklmnopqrst.supabase.co", config.Cloud!.ProjectUrl.Host);
        Assert.Equal(Path.Combine(directory, "glebosaurus-cloud.token"), AgentConfigLoader.ResolveRelative(path, config.Cloud.TokenFile));
    }

    [Theory]
    [InlineData(""" "mail": { "gmailAddress": "a@gmail.com", "appPassword": "x", "alertRecipients": ["b@example.com"] } """, "„mail”")]
    [InlineData(""" "sensors": [ { "id": "s1", "tuya": { "uuid": "07aaad78f40b0a74", "deviceId": "x", "localKeyHex": "a1b2c3d4e5f6", "productId": "gvygg3m8" } } ] """, "„sensors”")]
    public void CloudConfig_WithLocalMailOrSensors_IsRejected(string extra, string named)
    {
        var path = Write($$"""{ {{Cloud}}, {{extra}} }""");

        Assert.Contains(named, Assert.Throws<InvalidDataException>(() => AgentConfigLoader.Load(path)).Message);
    }

    [Theory]
    [InlineData("http://abcdefghijklmnopqrst.supabase.co", "sb_publishable_x")]
    [InlineData("https://example.com", "sb_publishable_x")]
    [InlineData("https://abcdefghijklmnopqrst.supabase.co", "sb_secret_x")]
    public void CloudConfig_WithWrongAddressOrSecretKey_IsRejected(string url, string key)
    {
        var path = Write($$"""{ "cloud": { "url": "{{url}}", "publishableKey": "{{key}}" } }""");

        Assert.Throws<InvalidDataException>(() => AgentConfigLoader.Load(path));
    }

    [Fact]
    public void StandaloneConfig_StillNeedsSensors()
    {
        Assert.Throws<InvalidDataException>(() => AgentConfigLoader.Load(Write("{ }")));
    }

    // ---- Token -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Token_IsStoredProtected_AndReadBack()
    {
        var store = new AgentTokenStore(Path.Combine(directory, "glebosaurus-cloud.token"), LocalProtectors.ForThisPlatform());

        Assert.Null(await store.LoadAsync(Ct));
        await store.SaveAsync("glb_tajny-token-agenta", Ct);

        Assert.Equal("glb_tajny-token-agenta", await store.LoadAsync(Ct));
        Assert.DoesNotContain("glb_tajny-token-agenta", store.ToString());
        if (OperatingSystem.IsWindows())
            Assert.DoesNotContain("tajny-token", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(store.Path, Ct)));
        else
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.Path));
    }

    [Fact]
    public async Task Token_FromOtherFileKind_IsRejected()
    {
        var cache = new CloudConfigCache(Path.Combine(directory, "plik.bin"), new OwnerOnlyFileProtector());
        await cache.SaveAsync(Config(), Fixtures.T0, Ct);

        await Assert.ThrowsAsync<InvalidDataException>(() => new AgentTokenStore(cache.Path, new OwnerOnlyFileProtector()).LoadAsync(Ct));
    }

    // ---- Spięcie części ------------------------------------------------------------------------------------------------

    private static CloudConfig Config(bool canSendMail = false) =>
        new(1, 7, new AgentIdentity("bestia5", canSendMail, canSendMail ? 2 : null),
            new CloudSettings("UTC", 15, 60, [], 3, 24, 7), null,
            [new("s1", "Monstera", "DC:23:51:CC:03:C9", true, null, 25, 35, 2, 18, 6)
            {
                Tuya = new TuyaCredentials { Mac = "DC:23:51:CC:03:C9", Uuid = "07aaad78f40b0a74", DeviceId = "1234567890123456789012", LocalKeyHex = "a1b2c3d4e5f6", ProductId = "gvygg3m8" }
            }]);

    private sealed record Parts(CloudRuntime Runtime, FakeCloudApi Api, CloudSync Sync, FakeSensorReader Reader, FakeHearing Hearing, TempStore Store);

    private Parts Build(TimeProvider? time = null, TimeSpan? tick = null)
    {
        var clock = time ?? new ManualTimeProvider(Fixtures.T0);
        var manual = clock as ManualTimeProvider ?? new ManualTimeProvider(clock.GetUtcNow());
        var api = new FakeCloudApi(manual) { Config = Config() };
        var store = new TempStore();
        var ledger = new LeaseLedger(clock);
        var sensors = new SensorLeasePolicy(clock, ledger);
        var notifierLock = new NotifierLockPolicy(clock, ledger);
        var hearing = new FakeHearing();
        var source = new CloudConfigSource(api, new CloudConfigCache(Path.Combine(directory, "kopia.bin"), new OwnerOnlyFileProtector()),
            new AgentConfig(), clock, NullLogger.Instance);
        var every = tick ?? TimeSpan.FromSeconds(30);
        var sync = new CloudSync(api, store.Store, hearing, ledger, sensors, notifierLock,
            () => source.Current?.Config.Sensors.Select(s => s.Id).ToList() ?? [], () => new("test", "test"), clock, NullLogger.Instance,
            new CloudSyncOptions { Interval = every, RetryDelays = [every] });
        var reader = new FakeSensorReader();
        var collector = new CollectorEngine(() => source.Current, store.Store, reader, new FakeListener(), sensors, new SensorHearingTracker(clock),
            api, null, clock, NullLogger.Instance, new CollectorOptions { TickInterval = every });
        var notifier = new NotifierEngine(() => source.Current, api, notifierLock, _ => new FakeMail(), null, () => sync.State, clock,
            NullLogger.Instance, new NotifierOptions { TickInterval = every });
        return new(new CloudRuntime(source, sync, collector, notifier, NullLogger.Instance), api, sync, reader, hearing, store);
    }

    [Fact]
    public async Task AfterSync_RefreshesConfig_AndDispatchesCommands()
    {
        var parts = Build();
        using var store = parts.Store;
        parts.Api.Commands = [new(1, "release", "s1", null, Fixtures.T0, 600), new(2, "report_now", null, null, Fixtures.T0, 600),
                              new(3, "pair", "s1", null, Fixtures.T0, 600)];

        // Bez wczytanej konfiguracji: hak po synchronizacji sam ją pobiera (wersja 7), potem rozdziela polecenia.
        await parts.Sync.SyncOnceAsync(Ct);

        Assert.Equal(1, parts.Api.ConfigCalls);
        Assert.Equal(1, Assert.Single(parts.Api.Done).Id);   // release obsłużył zbieracz; report_now czeka u nadawcy; pair – nikt
    }

    [Fact]
    public async Task Run_ClaimsHeardSensor_ReadsUploads_AndShutdownHandsItBack()
    {
        var parts = Build(TimeProvider.System, TimeSpan.FromMilliseconds(50));
        using var store = parts.Store;
        parts.Hearing.Current["s1"] = new(DateTimeOffset.UtcNow, -60, null, null, 0);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await parts.Runtime.RunAsync(stop.Token);

        Assert.Contains(parts.Api.Requests, r => r.Claims.Contains("sensor:s1"));
        Assert.NotEmpty(parts.Reader.Calls);
        Assert.NotEmpty(parts.Api.Stored);
        var last = parts.Api.Requests[^1];
        Assert.Empty(last.Claims);
        Assert.Contains(last.Releases, r => r.Resource == "sensor:s1");
    }

    [Fact]
    public async Task Run_WithoutCloudAndWithoutCopy_FailsWithClearMessage()
    {
        var parts = Build();
        using var store = parts.Store;
        parts.Api.ConfigFailures.Enqueue(new(CloudErrorKind.Unavailable, "brak sieci"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => parts.Runtime.RunAsync(Ct));
        Assert.Contains("pierwsze uruchomienie wymaga sieci", error.Message);
    }
}
