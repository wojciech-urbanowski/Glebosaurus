using Glebosaurus.Core.Cloud;
using Glebosaurus.Core.Config;
using Microsoft.Extensions.Logging.Abstractions;

namespace Glebosaurus.Cloud.Tests;

/// <summary>Konfiguracja z prawdziwej chmury: <c>get_config</c> → AgentConfig, chroniona kopia na dysku i start z niej bez sieci.</summary>
public sealed class CloudConfigEndToEndTests(CloudDatabase db) : IDisposable
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"glebosaurus-e2e-config-{Guid.NewGuid():N}");

    public void Dispose()
    {
        http.Dispose();
        try { Directory.Delete(directory, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    [Fact]
    public async Task ConfigFromCloud_MapsSensors_AndAgentStartsFromProtectedCopyWithoutNetwork()
    {
        db.SkipIfUnavailable();
        Assert.SkipWhen(db.Settings.PublishableKey is null, "Brak klucza publikowalnego w pliku próbnego projektu.");
        var ct = TestContext.Current.CancellationToken;
        var setup = new Scenario(db, DateTimeOffset.UtcNow);
        var sensor = await setup.SensorAsync(options: new() { ["silentAfterHours"] = 9 });
        var withoutKeys = await setup.SensorAsync(withCredentials: false);
        var name = Scenario.Unique("cfg");
        var code = await setup.CodeAsync(name, true, 2, DateTimeOffset.UtcNow);
        var enrolled = await SupabaseCloudApi.EnrollAsync(http, db.Settings.Url, db.Settings.PublishableKey!, code, ct);
        var api = new FlakyApi(new SupabaseCloudApi(http, db.Settings.Url, db.Settings.PublishableKey!, enrolled.Token));
        var cache = new CloudConfigCache(Path.Combine(directory, "cloud-config.bin"), LocalProtectors.ForThisPlatform());
        var local = new AgentConfig { Database = "lokalna.db" };

        var fresh = await new CloudConfigSource(api, cache, local, TimeProvider.System, NullLogger.Instance).LoadAsync(ct);

        Assert.False(fresh.FromCache);
        Assert.Equal(name, fresh.Config.MonitorName);
        Assert.True(fresh.Agent.CanSendMail);
        Assert.NotNull(fresh.Mail);
        var mapped = Assert.Single(fresh.Config.Sensors, s => s.Id == sensor);
        Assert.Equal(9, mapped.SilentAfterHours);
        Assert.Equal("a1b2c3d4e5f6", mapped.Tuya.LocalKeyHex);
        Assert.DoesNotContain(fresh.Config.Sensors, s => s.Id == withoutKeys);
        Assert.Contains(fresh.Warnings, w => w.Contains(withoutKeys));
        Assert.Equal("lokalna.db", fresh.Config.Database);

        api.ConfigFailuresLeft = 1;
        var offline = await new CloudConfigSource(api, cache, local, TimeProvider.System, NullLogger.Instance).LoadAsync(ct);

        Assert.True(offline.FromCache);
        Assert.Equal(fresh.Version, offline.Version);
        Assert.Equal("a1b2c3d4e5f6", Assert.Single(offline.Config.Sensors, s => s.Id == sensor).Tuya.LocalKeyHex);
    }
}
