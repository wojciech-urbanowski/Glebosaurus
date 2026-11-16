using Glebosaurus.Cloud.Admin;
using Glebosaurus.Core.Cloud;
using Glebosaurus.Core.Config;
using Glebosaurus.Tuya;

namespace Glebosaurus.Cloud.Tests;

/// <summary>
/// <c>cloud import</c> i <c>cloud agent</c> na próbnym projekcie: zmyślona konfiguracja trybu samodzielnego trafia do chmury,
/// a agent zarejestrowany kodem z <see cref="CloudAgents"/> widzi czujniki z kluczami, ustawienia, odbiorców i (z blokadą)
/// hasło Gmaila i adres strażnika. Import powtórzony – bez dubli.
/// </summary>
public sealed class CloudImportEndToEndTests(CloudDatabase db) : IDisposable
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public void Dispose() => http.Dispose();

    private static SensorConfig Sensor(string name)
    {
        var mac = Scenario.RandomMac();
        return new SensorConfig
        {
            Id = Scenario.Unique("imp"), Name = name, WateringBelowPercent = 28, WateringClearAbovePercent = 38, SilentAfterHours = 8,
            Tuya = new TuyaCredentials
            {
                Mac = mac.ToLowerInvariant(), Uuid = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)),
                DeviceId = "1234567890123456789012", LocalKeyHex = "0a1b2c3d4e5f", ProductId = "gvygg3m8"
            }
        };
    }

    [Fact]
    public async Task ImportedConfig_ReachesAgents_AndReimportDoesNotDuplicate()
    {
        db.SkipIfUnavailable();
        Assert.SkipWhen(db.Settings.PublishableKey is null, "Brak klucza publikowalnego w pliku próbnego projektu.");
        var ct = TestContext.Current.CancellationToken;
        Assert.True((await MigrationRunner.StatusAsync(db.DataSource, Migration.Embedded(), ct)).UpToDate);

        var standalone = new AgentConfig
        {
            Sensors = [Sensor("Paproć w kuchni"), Sensor("Zielistka")],
            Mail = new MailConfig
            {
                GmailAddress = "glebosaurus-test@gmail.com", AppPassword = "zmyslone haslo aplikacji",
                AlertRecipients = ["alerty@example.com"], ReportRecipients = ["raporty@example.com"], AdminRecipients = ["admin@example.com"]
            },
            Reports = new() { Times = ["06:30", "20:00"], CatchUpHours = 2 },
            Alerts = new() { ReminderHours = 12 }
        };
        var plan = CloudImporter.Plan(standalone, "https://hc-ping.com/zmyslony-adres-testu", "Europe/Warsaw");
        Assert.Empty(plan.Problems);
        await CloudImporter.ApplyAsync(db.DataSource, plan, ct);

        var name = Scenario.Unique("imp");
        var code = await CloudAgents.CreateWithCodeAsync(db.DataSource, name, senderPriority: 2, ct);
        var enrolled = await SupabaseCloudApi.EnrollAsync(http, db.Settings.Url, db.Settings.PublishableKey!, code, ct);
        var api = new SupabaseCloudApi(http, db.Settings.Url, db.Settings.PublishableKey!, enrolled.Token);
        Assert.Contains(await CloudAgents.ListAsync(db.DataSource, ct), a => a.Name == name && a.Enrolled && a.SenderPriority == 2);

        var config = await api.GetConfigAsync(ct);
        foreach (var sensor in standalone.Sensors)
        {
            var cloud = Assert.Single(config.Sensors, s => s.Id == sensor.Id);
            Assert.Equal(sensor.Tuya.Mac.ToUpperInvariant(), cloud.Mac);
            Assert.Equal((28, 38, 8.0), (cloud.WateringBelowPercent, cloud.WateringClearAbovePercent, cloud.SilentAfterHours));
            Assert.Equal("0a1b2c3d4e5f", cloud.Tuya?.LocalKeyHex);
            Assert.Equal(sensor.Tuya.Uuid, cloud.Tuya?.Uuid);
        }
        Assert.Equal(["06:30", "20:00"], config.Settings.ReportTimes);
        Assert.Equal(12, config.Settings.AlertReminderHours);
        Assert.Equal(["raporty@example.com"], config.Mail!.ReportRecipients);
        Assert.Equal("glebosaurus-test@gmail.com", config.Mail.GmailAddress);

        await db.AdminAsync("update glebosaurus.leases set holder_id = null, expires_at = null where resource = 'notifier' returning '{}'::text", null);
        var lockResult = await api.ClaimAsync(CloudProtocol.NotifierResource, ct);
        var credentials = await api.MailCredentialsAsync(lockResult.Epoch!.Value, ct);
        Assert.Equal("zmyslone haslo aplikacji", credentials.AppPassword);
        Assert.Equal("https://hc-ping.com/zmyslony-adres-testu", credentials.HealthchecksPingUrl);
        await api.ReleaseAsync(CloudProtocol.NotifierResource, lockResult.Epoch.Value, ct);

        var versionBefore = config.ConfigVersion;
        await CloudImporter.ApplyAsync(db.DataSource, plan, ct);
        var again = await api.GetConfigAsync(ct);
        Assert.True(again.ConfigVersion > versionBefore);
        Assert.Equal(2, again.Sensors.Count(s => standalone.Sensors.Any(o => o.Id == s.Id)));
    }
}
