using Glebosaurus.Cloud.Admin;

namespace Glebosaurus.Cloud.Tests;

/// <summary>
/// <c>cloud settings --read-interval</c> na próbnym projekcie: zmienia sam odstęp odczytów (reszta ustawień zostaje),
/// podnosi wersję konfiguracji, a agent dostaje nowy odstęp z <c>get_config</c>.
/// </summary>
public sealed class CloudSettingsEndToEndTests(CloudDatabase db) : IDisposable
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public void Dispose() => http.Dispose();

    [Fact]
    public async Task SetReadInterval_ChangesOnlyInterval_AndAgentsSeeIt()
    {
        db.SkipIfUnavailable();
        Assert.SkipWhen(db.Settings.PublishableKey is null, "Brak klucza publikowalnego w pliku próbnego projektu.");
        var ct = TestContext.Current.CancellationToken;
        Assert.True((await MigrationRunner.StatusAsync(db.DataSource, Migration.Embedded(), ct)).UpToDate);

        var before = await CloudSettingsAdmin.GetAsync(db.DataSource, ct);
        var minutes = before.ReadIntervalMinutes == 125 ? 47.5 : 125;
        try
        {
            await CloudSettingsAdmin.SetReadIntervalAsync(db.DataSource, minutes, ct);

            var after = await CloudSettingsAdmin.GetAsync(db.DataSource, ct);
            Assert.Equal(minutes, after.ReadIntervalMinutes);
            Assert.True(after.ConfigVersion > before.ConfigVersion);
            Assert.Equal((before.TimeZone, before.ScanWindowSeconds, before.AlertReminderHours, before.LogRetentionDays),
                (after.TimeZone, after.ScanWindowSeconds, after.AlertReminderHours, after.LogRetentionDays));
            Assert.Equal(before.ReportTimes, after.ReportTimes);
            Assert.Equal(before.Sensors, after.Sensors);

            var code = await CloudAgents.CreateWithCodeAsync(db.DataSource, Scenario.Unique("ust"), senderPriority: null, ct);
            var enrolled = await SupabaseCloudApi.EnrollAsync(http, db.Settings.Url, db.Settings.PublishableKey!, code, ct);
            var config = await new SupabaseCloudApi(http, db.Settings.Url, db.Settings.PublishableKey!, enrolled.Token).GetConfigAsync(ct);
            Assert.Equal(minutes, config.Settings.ReadIntervalMinutes);
        }
        finally
        {
            await CloudSettingsAdmin.SetReadIntervalAsync(db.DataSource, before.ReadIntervalMinutes, CancellationToken.None);
        }
    }
}
