using System.Text;
using Glebosaurus.Cloud;
using Glebosaurus.Core.Cloud;
using Glebosaurus.Core.Config;
using Glebosaurus.Tuya;
using Microsoft.Extensions.Logging.Abstractions;

namespace Glebosaurus.Tests;

/// <summary>Konfiguracja z chmury: przełożenie na AgentConfig, chroniona kopia na dysku, start bez sieci i odświeżanie.</summary>
public sealed class CloudConfigTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private const string Key = "a1b2c3d4e5f6";

    private readonly string directory = Path.Combine(Path.GetTempPath(), $"glebosaurus-config-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(directory, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private static TuyaCredentials Tuya(string mac, string uuid = "07aaad78f40b0a74", string? productId = "gvygg3m8") =>
        new() { Mac = mac, Uuid = uuid, DeviceId = "1234567890123456789012", LocalKeyHex = Key, ProductId = productId };

    private static CloudSensor Sensor(string id, string mac, TuyaCredentials? tuya, bool enabled = true) =>
        new(id, "Czujnik " + id, mac, enabled, null, 30, 40, 2, 18, 6) { Tuya = tuya };

    private static CloudConfig Config(long version = 3, bool canSendMail = false, string timeZone = "Europe/Warsaw", params CloudSensor[] sensors) =>
        new(1, version, new AgentIdentity("bestia5", canSendMail, canSendMail ? 2 : null),
            new CloudSettings(timeZone, 20, 60, ["07:00", "19:00"], 3, 12, 7),
            new CloudMailSettings("nadawca@gmail.com", ["ktos@example.com"], [], ["admin@example.com"]),
            sensors.Length > 0 ? sensors : [Sensor("cisy", "DC:23:51:CC:03:C9", Tuya("DC:23:51:CC:03:C9"))]);

    private static AgentConfig Local() => new()
    {
        Database = "baza-lokalna.db", RetentionDays = 123, Web = new WebConfig { Port = 8471 }, ReadIntervalMinutes = 99, Bluetooth = false
    };

    // ---- Przełożenie --------------------------------------------------------------------------------------------------

    [Fact]
    public void Map_TakesEverythingFromCloud_ExceptDeviceSettings()
    {
        var mapped = CloudConfigMapper.Map(Config(), Local());

        var config = mapped.Config;
        Assert.Equal("bestia5", config.MonitorName);
        Assert.Equal(20, config.ReadIntervalMinutes);
        Assert.Equal(["07:00", "19:00"], config.Reports.Times);
        Assert.Equal(12, config.Alerts.ReminderHours);
        Assert.Equal("baza-lokalna.db", config.Database);
        Assert.Equal(123, config.RetentionDays);
        Assert.Equal(8471, config.Web!.Port);
        Assert.False(config.Bluetooth);
        var sensor = Assert.Single(config.Sensors);
        Assert.Equal(("cisy", 30, 40, Key), (sensor.Id, sensor.WateringBelowPercent, sensor.WateringClearAbovePercent, sensor.Tuya.LocalKeyHex));
        Assert.Equal(3, mapped.Version);
        Assert.False(mapped.FromCache);
        Assert.Empty(mapped.Warnings);
    }

    [Fact]
    public void Map_SkipsDisabledAndBrokenSensors_WithWarnings()
    {
        const string mac = "DC:23:51:CC:03:C9";
        var cloud = Config(sensors:
        [
            Sensor("dobry", mac, Tuya(mac)),
            Sensor("wylaczony", "DC:23:51:00:00:01", Tuya("DC:23:51:00:00:01", "aaaaaaaaaaaaaaaa"), enabled: false),
            Sensor("bez-kluczy", "DC:23:51:00:00:02", null),
            Sensor("zly-uuid", "DC:23:51:00:00:03", Tuya("DC:23:51:00:00:03", "za-krotki")),
            Sensor("inny-mac", "DC:23:51:00:00:04", Tuya("DC:23:51:00:00:99", "bbbbbbbbbbbbbbbb")),
            Sensor("nieznany-typ", "DC:23:51:00:00:05", Tuya("DC:23:51:00:00:05", "cccccccccccccccc", "xyz")),
            Sensor("ten-sam-uuid", "DC:23:51:00:00:06", Tuya("DC:23:51:00:00:06"))
        ]);

        var mapped = CloudConfigMapper.Map(cloud, Local());

        Assert.Equal(["dobry"], mapped.Config.Sensors.Select(s => s.Id));
        Assert.Equal(5, mapped.Warnings.Count);
        Assert.All(mapped.Warnings, w => Assert.DoesNotContain(Key, w));
    }

    /// <summary>Kody czujników do raportu liczone ze wszystkich czujników w chmurze – także wyłączonych i pominiętych – jak na stronie.</summary>
    [Fact]
    public void Map_SensorCodesCoverEverySensor_IncludingSkippedOnes()
    {
        var cloud = Config(sensors:
        [
            Sensor("dobry", "DC:23:51:CC:03:C9", Tuya("DC:23:51:CC:03:C9")),
            Sensor("wylaczony", "DC:23:51:00:00:01", Tuya("DC:23:51:00:00:01", "aaaaaaaaaaaaaaaa"), enabled: false),
            Sensor("bez-kluczy", "DC:23:51:00:00:02", null)
        ]);

        var mapped = CloudConfigMapper.Map(cloud, Local());

        Assert.Equal(["dobry"], mapped.Config.Sensors.Select(s => s.Id));
        Assert.Equal(SensorCodes.For(cloud.Sensors.Select(s => (s.Id, s.Name))).OrderBy(p => p.Key, StringComparer.Ordinal),
            mapped.SensorCodes.OrderBy(p => p.Key, StringComparer.Ordinal));
        Assert.Equal(3, mapped.SensorCodes.Count);
    }

    [Fact]
    public void Map_MailRecipientsOnlyForSenders()
    {
        Assert.Null(CloudConfigMapper.Map(Config(canSendMail: false), Local()).Mail);
        Assert.Equal(["ktos@example.com"], CloudConfigMapper.Map(Config(canSendMail: true), Local()).Mail!.AlertRecipients);
    }

    [Fact]
    public void Map_TimeZone_FallsBackToSystemWithWarning()
    {
        var warsaw = CloudConfigMapper.Map(Config(), Local());
        Assert.Equal(TimeSpan.FromHours(1), warsaw.TimeZone.GetUtcOffset(new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc)));

        var unknown = CloudConfigMapper.Map(Config(timeZone: "Mars/Olympus"), Local());
        Assert.Equal(TimeZoneInfo.Local, unknown.TimeZone);
        Assert.Contains(unknown.Warnings, w => w.Contains("Mars/Olympus"));
    }

    [Fact]
    public void Map_OtherProtocolVersion_IsRejected()
    {
        Assert.Throws<InvalidDataException>(() => CloudConfigMapper.Map(Config() with { Protocol = 2 }, Local()));
    }

    // ---- Kopia na dysku ---------------------------------------------------------------------------------------------

    /// <summary>Atrapa szyfrowania: odwraca bajty i dokleja znacznik – w pliku nie ma jawnego tekstu.</summary>
    private sealed class ReversingProtector : ILocalProtector
    {
        public string Description => "atrapa";
        public byte[] Protect(byte[] data) => [.. "P"u8, .. data.Reverse()];
        public byte[] Unprotect(byte[] data) => data.Length > 0 && data[0] == (byte)'P'
            ? [.. data.Skip(1).Reverse()] : throw new InvalidDataException("to nie moje");
    }

    private CloudConfigCache Cache(ILocalProtector? protector = null) =>
        new(Path.Combine(directory, "cloud-config.bin"), protector ?? new ReversingProtector());

    [Fact]
    public async Task Cache_RoundTrips_WithoutPlainSecrets()
    {
        var cache = Cache();
        await cache.SaveAsync(Config(), Fixtures.T0, Ct);

        Assert.DoesNotContain(Key, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(cache.Path, Ct)));
        var loaded = (await cache.LoadAsync(Ct))!;
        Assert.Equal(Fixtures.T0, loaded.SavedAt);
        Assert.Equal(Key, Assert.Single(loaded.Config.Sensors).Tuya!.LocalKeyHex);
        Assert.Equal(["07:00", "19:00"], loaded.Config.Settings.ReportTimes);
        Assert.DoesNotContain(Key, cache.ToString());
    }

    [Fact]
    public async Task Cache_MissingCorruptOrForeign()
    {
        Assert.Null(await Cache().LoadAsync(Ct));

        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Cache().Path, "cokolwiek", Ct);
        await Assert.ThrowsAsync<InvalidDataException>(() => Cache().LoadAsync(Ct));

        await Cache().SaveAsync(Config(), Fixtures.T0, Ct);
        await Assert.ThrowsAsync<InvalidDataException>(() => Cache(new OwnerOnlyFileProtector()).LoadAsync(Ct));
    }

    [Fact]
    public async Task Cache_OnUnix_FileIsOwnerOnly()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Prawa 600 dotyczą systemów uniksowych.");
        var cache = Cache(new OwnerOnlyFileProtector());
        await cache.SaveAsync(Config(), Fixtures.T0, Ct);

        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(cache.Path));
    }

    [Fact]
    public async Task Dpapi_OnWindows_ProtectsAndRoundTrips()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI jest tylko na Windows.");
        var protector = LocalProtectors.ForThisPlatform();
        Assert.IsType<DpapiProtector>(protector);
        var cache = Cache(protector);

        await cache.SaveAsync(Config(), Fixtures.T0, Ct);

        Assert.DoesNotContain("07aaad78f40b0a74", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(cache.Path, Ct)));
        Assert.Equal(Key, Assert.Single((await cache.LoadAsync(Ct))!.Config.Sensors).Tuya!.LocalKeyHex);
        Assert.Throws<InvalidDataException>(() => protector.Unprotect([1, 2, 3, 4]));
    }

    // ---- Źródło konfiguracji ----------------------------------------------------------------------------------------

    private (CloudConfigSource Source, FakeCloudApi Api, CloudConfigCache Cache) Source()
    {
        var clock = new ManualTimeProvider(Fixtures.T0);
        var api = new FakeCloudApi(clock) { Config = Config() };
        var cache = Cache();
        return (new CloudConfigSource(api, cache, Local(), clock, NullLogger.Instance), api, cache);
    }

    [Fact]
    public async Task Source_LoadsFromCloud_AndKeepsCopy()
    {
        var (source, _, cache) = Source();

        var config = await source.LoadAsync(Ct);

        Assert.False(config.FromCache);
        Assert.Same(config, source.Current);
        Assert.Equal(3, (await cache.LoadAsync(Ct))!.Config.ConfigVersion);
    }

    [Fact]
    public async Task Source_CloudDown_StartsFromCopy_ThenRefreshesWhenBack()
    {
        var (source, api, cache) = Source();
        await cache.SaveAsync(Config(version: 2), Fixtures.T0.AddDays(-1), Ct);
        api.ConfigFailures.Enqueue(new(CloudErrorKind.Unavailable, "brak sieci"));
        var changed = new List<long>();
        source.Changed += c => changed.Add(c.Version);

        var fallback = await source.LoadAsync(Ct);
        Assert.True(fallback.FromCache);
        Assert.Equal(2, fallback.Version);
        Assert.Equal(Fixtures.T0.AddDays(-1), fallback.CachedAt);

        // Pierwsza udana synchronizacja – nawet przy tej samej wersji agent bierze świeżą konfigurację zamiast kopii.
        api.Config = Config(version: 2);
        Assert.True(await source.RefreshAsync(2, Ct));
        Assert.False(source.Current!.FromCache);
        Assert.Equal([2L], changed);
    }

    [Fact]
    public async Task Source_NoCloudAndNoCopy_FailsClearly()
    {
        var (source, api, _) = Source();
        api.ConfigFailures.Enqueue(new(CloudErrorKind.Unavailable, "brak sieci"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => source.LoadAsync(Ct));
        Assert.Contains("pierwsze uruchomienie wymaga sieci", error.Message);
    }

    [Fact]
    public async Task Source_RevokedToken_IsNotHidden()
    {
        var (source, api, cache) = Source();
        await cache.SaveAsync(Config(), Fixtures.T0, Ct);
        api.ConfigFailures.Enqueue(new(CloudErrorKind.Unauthorized, "token unieważniony", "28000"));

        Assert.Equal(CloudErrorKind.Unauthorized, (await Assert.ThrowsAsync<CloudApiException>(() => source.LoadAsync(Ct))).Kind);
    }

    [Fact]
    public async Task Source_RefreshesOnlyOnNewerVersion_AndKeepsPreviousWhenNewIsBroken()
    {
        var (source, api, cache) = Source();
        await source.LoadAsync(Ct);

        Assert.False(await source.RefreshAsync(3, Ct));
        Assert.False(await source.RefreshAsync(null, Ct));
        Assert.Equal(1, api.ConfigCalls);

        api.Config = Config(version: 4) with { Protocol = 2 };
        Assert.False(await source.RefreshAsync(4, Ct));
        Assert.False(await source.RefreshAsync(4, Ct));   // tej wersji drugi raz nie pobiera
        Assert.Equal(2, api.ConfigCalls);
        Assert.Equal(3, source.Current!.Version);
        Assert.Equal(3, (await cache.LoadAsync(Ct))!.Config.ConfigVersion);

        api.Config = Config(version: 5);
        Assert.True(await source.RefreshAsync(5, Ct));
        Assert.Equal(5, source.Current!.Version);
        Assert.Equal(5, (await cache.LoadAsync(Ct))!.Config.ConfigVersion);
    }

    [Fact]
    public async Task Source_NetworkErrorDuringRefresh_TriesAgainLater()
    {
        var (source, api, _) = Source();
        await source.LoadAsync(Ct);
        api.Config = Config(version: 4);
        api.ConfigFailures.Enqueue(new(CloudErrorKind.Unavailable, "brak sieci"));

        Assert.False(await source.RefreshAsync(4, Ct));
        Assert.True(await source.RefreshAsync(4, Ct));
        Assert.Equal(4, source.Current!.Version);
    }

    [Fact]
    public async Task Source_RoleChangedInCloud_RefreshesWithoutNewVersion()
    {
        // Zmiana roli agenta w chmurze nie podnosi wersji konfiguracji – agent widzi ją w odpowiedzi agent_sync.
        var (source, api, cache) = Source();
        await source.LoadAsync(Ct);
        Assert.Null(source.Current!.Mail);

        Assert.False(await source.RefreshAsync(3, new AgentIdentity("bestia5", false, null), Ct));   // ta sama rola
        Assert.Equal(1, api.ConfigCalls);

        api.Config = Config(canSendMail: true);   // awans na nadawcę, wersja dalej 3
        Assert.True(await source.RefreshAsync(3, new AgentIdentity("bestia5", true, 2), Ct));
        Assert.True(source.Current!.Agent.CanSendMail);
        Assert.NotNull(source.Current.Mail);
        Assert.True((await cache.LoadAsync(Ct))!.Config.Agent.CanSendMail);
        Assert.False(await source.RefreshAsync(3, new AgentIdentity("bestia5", true, 2), Ct));   // drugi raz nie pobiera
        Assert.Equal(2, api.ConfigCalls);

        api.Config = Config(canSendMail: true) with { Agent = new AgentIdentity("bestia5", true, 1) };   // sam priorytet
        Assert.True(await source.RefreshAsync(3, new AgentIdentity("bestia5", true, 1), Ct));
        Assert.Equal(1, source.Current!.Agent.SenderPriority);

        api.Config = Config();   // z powrotem tylko zbieracz
        Assert.True(await source.RefreshAsync(3, new AgentIdentity("bestia5", false, null), Ct));
        Assert.False(source.Current!.Agent.CanSendMail);
        Assert.Null(source.Current.Mail);
        Assert.Equal(4, api.ConfigCalls);
    }
}
