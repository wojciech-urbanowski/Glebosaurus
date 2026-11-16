using System.Text.Json.Nodes;
using Glebosaurus.Core;
using Glebosaurus.Core.Cloud;
using Glebosaurus.Core.Config;
using Glebosaurus.Core.Domain;
using Glebosaurus.Core.Mail;
using Glebosaurus.Tuya;
using Microsoft.Extensions.Logging.Abstractions;

namespace Glebosaurus.Cloud.Tests;

/// <summary>
/// Nadawca na prawdziwej chmurze (poczta udawana): blokada, hasło z Vault, alert z odczytów innego agenta, mail przez
/// kolejkę – raz, także po „restarcie” nadawcy – i raport na żądanie z wynikiem polecenia.
/// </summary>
public sealed class NotifierEndToEndTests(CloudDatabase db) : IDisposable
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public void Dispose() => http.Dispose();

    [Fact]
    public async Task AlertFromAnotherAgentsReadings_IsMailedOnce_AndReportOnRequestWorks()
    {
        db.SkipIfUnavailable();
        Assert.SkipWhen(db.Settings.PublishableKey is null, "Brak klucza publikowalnego w pliku próbnego projektu.");
        var ct = TestContext.Current.CancellationToken;
        var setup = new Scenario(db, DateTimeOffset.UtcNow);
        var sensorId = await setup.SensorAsync();
        await db.AdminAsync("select to_jsonb(null::text) from glebosaurus.admin_mail_secret('gmail-app-password', 'abcd efgh ijkl mnop')", null);
        await db.AdminAsync("select to_jsonb(null::text) from glebosaurus.admin_settings_update(@p)", null,
            ("p", new JsonObject { ["gmailAddress"] = "nadawca@gmail.com" }));
        // Blokada jest jedna na bazę, a testy z przesuniętym zegarem zostawiają ją „w przyszłości”.
        await db.AdminAsync("update glebosaurus.leases set holder_id = null, expires_at = null where resource = 'notifier' returning '{}'::text", null);
        var notifierApi = await EnrollAsync(setup, "pi", canSendMail: true, ct);
        var collectorApi = await EnrollAsync(setup, "android", canSendMail: false, ct);

        // Agent-zbieracz wysyła dwa suche odczyty (próg 25%, dwa z rzędu).
        var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await collectorApi.SyncAsync(new SyncRequest(new AgentStatus(AppInfo.Version, "test"))
        {
            Readings = [new(sensorId, now.AddMinutes(-20), 9, 200, 80, 2), new(sensorId, now.AddMinutes(-5), 8, 200, 80, 2)]
        }, ct);

        var clock = TimeProvider.System;
        var ledger = new LeaseLedger(clock);
        var lockPolicy = new NotifierLockPolicy(clock, ledger);
        ledger.Apply(ledger.Now(), [await notifierApi.ClaimAsync(CloudProtocol.NotifierResource, ct)]);
        Assert.True(lockPolicy.CanSendMail);

        var sensor = new SensorConfig { Id = sensorId, Name = "Czujnik testowy", Tuya = new TuyaCredentials { Uuid = "0123456789abcdef", DeviceId = "x", LocalKeyHex = "a1b2c3d4e5f6" } };
        var config = new CloudAgentConfig(new AgentConfig { Sensors = [sensor], Reports = new() { Times = [] } }, TimeZoneInfo.Utc, 1,
            new("pi", true, 1), new CloudMailSettings("nadawca@gmail.com", ["ktos@example.com"], [], []), []);
        var mail = new RecordingMail();
        var sync = CloudSyncState.NotStarted with { LastSuccessAt = DateTimeOffset.UtcNow };
        MailCredentials? received = null;
        NotifierEngine Engine() => new(() => config, notifierApi, lockPolicy, credentials => { received = credentials; return mail; }, null,
            () => sync, clock, NullLogger.Instance);

        await Engine().CycleAsync(ct);
        await Engine().CycleAsync(ct);                // „restart” nadawcy – nic drugi raz

        Assert.Equal("abcd efgh ijkl mnop", received?.AppPassword);
        var alertMail = Assert.Single(mail.Sent);
        Assert.Equal(["ktos@example.com"], alertMail.To);
        var outbox = await db.AdminAsync("""
            select jsonb_build_object('count', count(*), 'sent', bool_and(sent_at is not null), 'messageId', max(message_id))::text
              from glebosaurus.mail_outbox where key like @k
            """, null, ("k", $"alert|{sensorId}|NeedsWater|%"));
        Assert.Equal(1, outbox.GetProperty("count").GetInt32());
        Assert.True(outbox.GetProperty("sent").GetBoolean());
        Assert.Equal(alertMail.MessageId, outbox.GetProperty("messageId").GetString());
        var alert = Assert.Single(await collectorApi.AlertListAsync(ct), a => a.Sensor == sensorId);
        Assert.Equal(AlertKind.NeedsWater, alert.Kind);
        Assert.NotNull(alert.LastNotifiedAt);

        // Raport na żądanie (np. z panelu zbieracza) – chmura kieruje polecenie do obecnego nadawcy przy jego synchronizacji.
        var order = await collectorApi.CommandCreateAsync("report_now", null, ct);
        var delivered = await notifierApi.SyncAsync(new SyncRequest(new AgentStatus(AppInfo.Version, "test")) { Claims = [CloudProtocol.NotifierResource] }, ct);
        var engine = Engine();
        Assert.True(await engine.HandleCommandAsync(Assert.Single(delivered.Commands, c => c.Id == order.Id), ct));
        await engine.CycleAsync(ct);

        Assert.Equal(2, mail.Sent.Count);
        var done = await db.AdminAsync("select jsonb_build_object('ok', result->'ok')::text from glebosaurus.commands where id = @id",
            null, ("id", order.Id));
        Assert.True(done.GetProperty("ok").GetBoolean());

        await notifierApi.ReleaseAsync(CloudProtocol.NotifierResource, ledger.Epoch(CloudProtocol.NotifierResource)!.Value, ct);
    }

    private async Task<SupabaseCloudApi> EnrollAsync(Scenario setup, string prefix, bool canSendMail, CancellationToken ct)
    {
        var code = await setup.CodeAsync(Scenario.Unique(prefix), canSendMail, canSendMail ? 1 : null, DateTimeOffset.UtcNow);
        var enrolled = await SupabaseCloudApi.EnrollAsync(http, db.Settings.Url, db.Settings.PublishableKey!, code, ct);
        return new SupabaseCloudApi(http, db.Settings.Url, db.Settings.PublishableKey!, enrolled.Token);
    }

    private sealed class RecordingMail : IMailGateway
    {
        public List<MailMessage> Sent { get; } = [];

        public Task SendAsync(MailMessage message, CancellationToken cancellationToken)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }
}
