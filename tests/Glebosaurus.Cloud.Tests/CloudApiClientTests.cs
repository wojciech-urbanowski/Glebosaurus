using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Glebosaurus.Cloud;
using Glebosaurus.Core.Cloud;
using Glebosaurus.Core.Domain;

namespace Glebosaurus.Cloud.Tests;

/// <summary>
/// Klient C# (<see cref="SupabaseCloudApi"/>) przez prawdziwe Data API próbnego projektu – czy typy protokołu w Core zgadzają
/// się z JSON-em funkcji SQL. Czas prawdziwy (bez sterowania zegarem).
/// </summary>
public sealed class CloudApiClientTests(CloudDatabase db) : IDisposable
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public void Dispose() => http.Dispose();

    private async Task<SupabaseCloudApi> AgentAsync(string prefix, bool canSendMail = false, int? priority = null)
    {
        var scenario = new Scenario(db, DateTimeOffset.UtcNow);
        var name = Scenario.Unique(prefix);
        var code = await scenario.CodeAsync(name, canSendMail, priority, DateTimeOffset.UtcNow);
        var enrolled = await WithSchemaRetry(() => SupabaseCloudApi.EnrollAsync(http, db.Settings.Url, db.Settings.PublishableKey!, code,
            TestContext.Current.CancellationToken));
        Assert.Equal(name, enrolled.Agent);
        Assert.DoesNotContain(enrolled.Token, enrolled.ToString());
        return new SupabaseCloudApi(http, db.Settings.Url, db.Settings.PublishableKey!, enrolled.Token);
    }

    /// <summary>Po migracji Data API odświeża opis schematu z opóźnieniem (brak funkcji = błąd protokołu przez chwilę).</summary>
    private static async Task<T> WithSchemaRetry<T>(Func<Task<T>> call)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await call(); }
            catch (CloudApiException e) when (e.Kind == CloudErrorKind.Protocol && e.SqlState == "PGRST202" && attempt < 10)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            }
        }
    }

    private void SkipUnlessHttp()
    {
        db.SkipIfUnavailable();
        Assert.SkipWhen(db.Settings.PublishableKey is null, "Brak klucza publikowalnego w pliku próbnego projektu.");
    }

    [Fact]
    public async Task CollectorPath_ConfigSyncLeasesReadingsCommands()
    {
        SkipUnlessHttp();
        var ct = TestContext.Current.CancellationToken;
        var setup = new Scenario(db, DateTimeOffset.UtcNow);
        var sensor = await setup.SensorAsync(options: new() { ["readIntervalMinutes"] = 20 });
        var api = await AgentAsync("android");

        var config = await api.GetConfigAsync(ct);
        var cloudSensor = Assert.Single(config.Sensors, s => s.Id == sensor);
        Assert.Equal(20, cloudSensor.ReadIntervalMinutes);
        Assert.Equal("a1b2c3d4e5f6", cloudSensor.Tuya?.LocalKeyHex);
        Assert.DoesNotContain("a1b2c3d4e5f6", cloudSensor.ToString());
        Assert.Null(config.Mail);
        Assert.Equal("Europe/Warsaw", config.Settings.TimeZone);

        var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());   // jak w SQLite
        var reading = new StoredReading(sensor, now.AddMinutes(-1), 41, 21.5m, 80, 2);
        var heartbeat = new AgentStatus("0.2.0-test", "android", "ok")
        {
            BatteryPercent = 80, BatteryTemperatureCelsius = 31.5, Charging = true, Network = "wifi", WifiDbm = -58, ThermalStatus = "none"
        };
        var sync = await api.SyncAsync(new SyncRequest(heartbeat)
        {
            Sightings = [new(sensor, -77, 12.5, 60, 60, 0)],
            Readings = [ReadingReport.From(reading), new("brak-takiego", now, 1, null, null, null)],
            Claims = [CloudProtocol.SensorResource(sensor)]
        }, ct);

        Assert.Equal(CloudProtocol.Version, sync.Protocol);
        Assert.Equal(1, sync.Readings.Inserted);
        Assert.Equal("unknown_sensor", Assert.Single(sync.Readings.Rejected).Reason);
        var lease = Assert.Single(sync.Leases);
        Assert.True(lease.Granted);
        Assert.Equal(1800, lease.ValidSeconds);
        Assert.Equal(config.Agent.Name, Assert.Single(sync.Sensors, s => s.Sensor == sensor).Holder);
        Assert.Equal(heartbeat, Assert.Single(sync.Agents, a => a.Name == config.Agent.Name).Status);   // sygnał życia dla innych agentów
        Assert.InRange((sync.ServerTime - now).Duration(), TimeSpan.Zero, TimeSpan.FromMinutes(2));

        var stored = Assert.Single(await api.ReadingsRangeAsync([sensor], now.AddHours(-1), now.AddHours(1), ct));
        Assert.Equal(reading, stored);

        // Kopia odczytów: odczyt jest świeży (nieustalony), więc od najnowszego numeru nic nie przychodzi; starsze paczki
        // (z innych testów) muszą dać się odczytać.
        var lastSeq = (await db.AdminAsync("select to_jsonb(coalesce(max(seq), 0))::text from glebosaurus.readings", null)).GetInt64();
        var empty = await api.ReadingsFeedAsync(lastSeq, 100, ct);
        Assert.Empty(empty.Readings);
        Assert.Equal(lastSeq, empty.Last);
        Assert.False(empty.More);
        var oldest = await api.ReadingsFeedAsync(0, 2, ct);
        Assert.InRange(oldest.Readings.Count, 0, 2);
        Assert.All(oldest.Readings, r => Assert.False(string.IsNullOrEmpty(r.Sensor)));

        var created = await api.CommandCreateAsync("read_now", sensor, ct);
        Assert.True(created.Created);
        var next = await api.SyncAsync(new SyncRequest(new AgentStatus("0.2.0-test", "test")) { Claims = [CloudProtocol.SensorResource(sensor)] }, ct);
        var command = Assert.Single(next.Commands, c => c.Id == created.Id);
        Assert.Equal("read_now", command.Kind);
        Assert.True(await api.CommandDoneAsync(command.Id, new JsonObject { ["ok"] = true }, ct));

        Assert.True(await api.ReleaseAsync(CloudProtocol.SensorResource(sensor), lease.Epoch!.Value, ct));
    }

    [Fact]
    public async Task NotifierPath_LockMailAlertsLogs()
    {
        SkipUnlessHttp();
        var ct = TestContext.Current.CancellationToken;
        var setup = new Scenario(db, DateTimeOffset.UtcNow);
        var sensor = await setup.SensorAsync();
        // Blokada nadawcy jest jedna na bazę, a testy z przesuniętym zegarem zostawiają ją „w przyszłości” – tu czas jest prawdziwy.
        await db.AdminAsync("update glebosaurus.leases set holder_id = null, expires_at = null where resource = 'notifier' returning '{}'::text", null);
        var pi = await AgentAsync("pi", canSendMail: true, priority: 1);
        var android = await AgentAsync("android");

        var config = await pi.GetConfigAsync(ct);
        Assert.NotNull(config.Mail);
        var lockResult = await pi.ClaimAsync(CloudProtocol.NotifierResource, ct);
        Assert.True(lockResult.Granted);
        var epoch = lockResult.Epoch!.Value;
        Assert.Equal("not_sender", (await android.ClaimAsync(CloudProtocol.NotifierResource, ct)).Error);

        var key = "report|" + Scenario.Unique("http");
        Assert.True((await pi.MailClaimAsync(key, epoch, ct)).Claimed);
        Assert.True((await pi.MailClaimAsync(key, epoch, ct)).AlreadyClaimed);
        Assert.True(await pi.MailSentAsync(key, "<test@glebosaurus>", ct));
        var credentials = await pi.MailCredentialsAsync(epoch, ct);
        Assert.DoesNotContain(credentials.AppPassword ?? "∅∅∅", credentials.ToString());
        var denied = await Assert.ThrowsAsync<CloudApiException>(() => android.MailCredentialsAsync(epoch, ct));
        Assert.Equal(CloudErrorKind.NotNotifier, denied.Kind);

        var opened = await pi.AlertOpenAsync(epoch, sensor, AlertKind.LowBattery, "bateria czujnika 15% (próg 18% lub mniej)", ct);
        Assert.True(opened.Created);
        Assert.Equal(AlertKind.LowBattery, opened.Alert.Kind);
        Assert.Contains(await android.AlertListAsync(ct), a => a.Id == opened.Alert.Id);
        Assert.Equal(1, await pi.AlertNotifiedAsync(epoch, [opened.Alert.Id], ct));
        Assert.True(await pi.AlertResolveAsync(epoch, opened.Alert.Id, ct));

        var day = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
        var text = "07:00:01 info: Monitor startuje – zażółć gęślą jaźń\n";
        await android.LogPutAsync(day, Gzip(text), ct);
        var log = Assert.Single(await pi.LogGetAsync(epoch, day, ct), l => l.Day == day && l.SizeBytes > 0
            && Gunzip(l.ContentGzipBase64) == text);
        Assert.NotEqual(pi.ToString(), log.Agent);

        Assert.True(await pi.ReleaseAsync(CloudProtocol.NotifierResource, epoch, ct));
    }

    [Fact]
    public async Task Errors_AreMappedToKinds()
    {
        SkipUnlessHttp();
        var ct = TestContext.Current.CancellationToken;
        var unknown = new SupabaseCloudApi(http, db.Settings.Url, db.Settings.PublishableKey!, "glb_nieznany");
        Assert.Equal(CloudErrorKind.Unauthorized, (await Assert.ThrowsAsync<CloudApiException>(() => unknown.GetConfigAsync(ct))).Kind);

        var badKey = new SupabaseCloudApi(http, db.Settings.Url, "sb_publishable_zmyslony0000000000000000", "glb_nieznany");
        Assert.Equal(CloudErrorKind.Unauthorized, (await Assert.ThrowsAsync<CloudApiException>(() => badKey.GetConfigAsync(ct))).Kind);

        var api = await AgentAsync("android");
        var invalid = await Assert.ThrowsAsync<CloudApiException>(() => api.CommandCreateAsync("pair", null, ct));
        Assert.Equal(CloudErrorKind.Invalid, invalid.Kind);
        Assert.Equal("22023", invalid.SqlState);
        Assert.DoesNotContain("glb_", invalid.Message);

        var offline = new SupabaseCloudApi(http, new Uri("https://localhost:1"), "x", "glb_x");
        Assert.Equal(CloudErrorKind.Unavailable, (await Assert.ThrowsAsync<CloudApiException>(() => offline.GetConfigAsync(ct))).Kind);
    }

    private static byte[] Gzip(string text)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.SmallestSize)) gzip.Write(Encoding.UTF8.GetBytes(text));
        return buffer.ToArray();
    }

    private static string Gunzip(string base64)
    {
        using var gzip = new GZipStream(new MemoryStream(Convert.FromBase64String(base64)), CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
