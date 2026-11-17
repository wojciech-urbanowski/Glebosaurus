using System.IO.Compression;
using System.Text;
using Glebosaurus.Core.Cloud;
using Glebosaurus.Core.Config;
using Glebosaurus.Core.Domain;
using Glebosaurus.Core.Mail;
using Microsoft.Extensions.Logging;

namespace Glebosaurus.Tests;

/// <summary>Nadawca w trybie chmurowym: maile tylko z blokadą i najwyżej raz (kolejka w chmurze), alerty, raporty, logi, strażnik.</summary>
public sealed class NotifierTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);
    private static readonly DateTimeOffset Morning = new(2026, 11, 17, 6, 0, 0, TimeSpan.Zero);

    private readonly ManualTimeProvider clock = new(Morning);
    private readonly FakeCloudApi api;
    private readonly LeaseLedger ledger;
    private readonly NotifierLockPolicy lockPolicy;
    private readonly FakeMail mail = new();
    private readonly FakeHeartbeat heartbeat = new();
    private readonly CapturingLogger logger = new();
    private CloudSyncState sync;
    private CloudAgentConfig config;
    private int gatewaysCreated;

    public NotifierTests()
    {
        api = new(clock);
        ledger = new(clock);
        lockPolicy = new(clock, ledger);
        sync = CloudSyncState.NotStarted with { LastSuccessAt = Morning };
        config = Config(canSendMail: true);
    }

    private static CloudAgentConfig Config(bool canSendMail, double silentAfterHours = 1000)
    {
        SensorConfig Sensor(string id, string uuid)
        {
            var sensor = Fixtures.Sensor(id, "Roślina " + id, uuid);
            sensor.SilentAfterHours = silentAfterHours;
            return sensor;
        }
        var agent = new AgentConfig
        {
            Sensors = [Sensor("s1", "07aaad78f40b0a74"), Sensor("s2", "07aaad78f40b0a75")],
            Reports = new() { Times = ["07:00"], CatchUpHours = 3 },
            Alerts = new() { ReminderHours = 24 }
        };
        return new(agent, TimeZoneInfo.Utc, 1, new("pi", canSendMail, canSendMail ? 1 : null),
            new CloudMailSettings("nadawca@gmail.com", ["ktos@example.com"], [], ["admin@example.com"]), []);
    }

    private NotifierEngine Engine(FakeMail? gateway = null) =>
        new(() => config, api, lockPolicy, _ => { gatewaysCreated++; return gateway ?? mail; }, heartbeat, () => sync, clock, logger);

    /// <summary>Blokada nadawcy z epoką bazy (atrapy) – po przesunięciu zegara trzeba ją „odnowić”, jak robi synchronizacja.</summary>
    private void Lock() =>
        ledger.Apply(ledger.Now(), [new LeaseResult(CloudProtocol.NotifierResource, true) { Epoch = api.NotifierEpoch, ValidSeconds = 300 }]);

    private void Advance(TimeSpan span)
    {
        clock.Advance(span);
        sync = sync with { LastSuccessAt = clock.GetUtcNow() };
        Lock();
    }

    private void Dry(string sensor, int moisture = 10)
    {
        api.CloudReadings.Add(Fixtures.Reading(sensor, clock.GetUtcNow().AddMinutes(-16), moisture));
        api.CloudReadings.Add(Fixtures.Reading(sensor, clock.GetUtcNow().AddMinutes(-1), moisture));
    }

    [Fact]
    public async Task WithoutLock_NoPasswordAndNoMail()
    {
        Dry("s1");
        await Engine().CycleAsync(Ct);

        Assert.Equal(0, api.CredentialCalls);
        Assert.Empty(mail.Sent);
    }

    [Fact]
    public async Task Collector_NeverSends()
    {
        config = Config(canSendMail: false);
        Lock();
        Dry("s1");
        await Engine().CycleAsync(Ct);

        Assert.Equal(0, api.CredentialCalls);
        Assert.Empty(mail.Sent);
    }

    [Fact]
    public async Task Alert_IsOpenedAndMailedOnce_ThenRemindedAfterADay()
    {
        Lock();
        Dry("s1");
        var engine = Engine();

        await engine.CycleAsync(Ct);
        Advance(Minute);
        await engine.CycleAsync(Ct);

        var alert = Assert.Single(api.Alerts);
        Assert.Equal((AlertKind.NeedsWater, Morning), (alert.Kind, alert.LastNotifiedAt!.Value));
        var first = Assert.Single(mail.Sent);
        var key = Assert.Single(api.Outbox.Keys);
        Assert.StartsWith("alert|s1|NeedsWater|2026-11-17T06:00:00.000000Z|0", key);
        Assert.True(api.Outbox[key].Sent);
        Assert.Equal(NotifierEngine.MessageId(key), first.MessageId);

        Advance(TimeSpan.FromHours(24));
        Dry("s1");
        await engine.CycleAsync(Ct);

        Assert.Equal(2, mail.Sent.Count);
        Assert.Contains(api.Outbox.Keys, k => k.EndsWith("|1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScheduledReport_GoesOutOnce_EvenWithASecondNotifier()
    {
        clock.Advance(TimeSpan.FromMinutes(60.5));   // 07:00:30
        Lock();
        var other = new FakeMail();

        await Engine().CycleAsync(Ct);
        await Engine(other).CycleAsync(Ct);          // np. nowy proces po restarcie albo przejęciu blokady

        Assert.Single(mail.Sent);
        Assert.Empty(other.Sent);
        Assert.True(api.Outbox["report|07:00|2026-11-17"].Sent);
    }

    [Fact]
    public async Task SureSmtpFailure_IsRetriedAfterDelay()
    {
        clock.Advance(TimeSpan.FromMinutes(60.5));
        Lock();
        mail.Failure = new MailTransportException("brak połączenia", new IOException("x"), maybeDelivered: false);
        var engine = Engine();

        await engine.CycleAsync(Ct);
        Assert.Empty(api.Outbox);                    // rezerwacja oddana

        mail.Failure = null;
        Advance(Minute);
        await engine.CycleAsync(Ct);
        Assert.Empty(mail.Sent);                     // przerwa po porażce
        Advance(5 * Minute);
        await engine.CycleAsync(Ct);

        Assert.Single(mail.Sent);
        Assert.Equal(2, mail.Attempts);
    }

    [Fact]
    public async Task UncertainSmtpFailure_IsNeverRetried()
    {
        clock.Advance(TimeSpan.FromMinutes(60.5));
        Lock();
        mail.Failure = new MailTransportException("przerwa w trakcie DATA", new IOException("x"), maybeDelivered: true);

        await Engine().CycleAsync(Ct);
        mail.Failure = null;
        Advance(10 * Minute);
        await Engine().CycleAsync(Ct);               // także po restarcie

        Assert.Equal(1, mail.Attempts);
        Assert.False(api.Outbox["report|07:00|2026-11-17"].Sent);
    }

    [Fact]
    public async Task LockLostOnTheServer_MeansNoMail()
    {
        Lock();
        var engine = Engine();
        await engine.CycleAsync(Ct);                 // 06:00 – hasło pobrane, nic do wysłania
        api.NotifierEpoch = 6;                        // blokadę przejął ktoś inny, a lokalna ważność jeszcze trwa
        clock.Advance(TimeSpan.FromMinutes(60.5));
        ledger.Apply(ledger.Now(), [new LeaseResult(CloudProtocol.NotifierResource, true) { Epoch = 5, ValidSeconds = 300 }]);

        await engine.CycleAsync(Ct);

        Assert.Empty(mail.Sent);
        Assert.Empty(api.Outbox);
    }

    [Fact]
    public async Task ReportOnRequest_GoesOutAndReportsDone()
    {
        Lock();
        var engine = Engine();

        Assert.True(await engine.HandleCommandAsync(new(9, "report_now", null, null, Morning, 600), Ct));
        Assert.False(await engine.HandleCommandAsync(new(10, "read_now", "s1", null, Morning, 600), Ct));
        await engine.CycleAsync(Ct);

        Assert.Single(mail.Sent);
        Assert.True(api.Outbox["report-request|9"].Sent);
        var (id, result) = Assert.Single(api.Done);
        Assert.Equal(9, id);
        Assert.True((bool)result!["ok"]!);
    }

    [Fact]
    public async Task AdminLogs_OneMailPerAgent_AfterOneOClock()
    {
        clock.Advance(TimeSpan.FromHours(-5.5));      // 00:30
        Lock();
        var yesterday = new DateOnly(2026, 11, 16);
        api.AgentLogs.Add(new("pi", yesterday, 10, clock.GetUtcNow(), Gzip("00:01:00 info: pi startuje\n")));
        api.AgentLogs.Add(new("android", yesterday, 10, clock.GetUtcNow(), Gzip("00:02:00 warn: słaby sygnał\n")));
        var engine = Engine();

        await engine.CycleAsync(Ct);
        Assert.Empty(mail.Sent);
        Advance(30 * Minute);                         // 01:00
        await engine.CycleAsync(Ct);
        Advance(TimeSpan.FromHours(2));
        await engine.CycleAsync(Ct);

        Assert.Equal(2, mail.Sent.Count);
        Assert.All(mail.Sent, m => Assert.Equal(["admin@example.com"], m.To));
        Assert.Contains(mail.Sent, m => m.Subject.StartsWith("android:", StringComparison.Ordinal) && m.Attachments!.Count == 1);
        Assert.Contains("log|2026-11-16|pi", api.Outbox.Keys);
    }

    [Fact]
    public async Task SilentSensor_WaitsForTheGraceAfterNotifierStart()
    {
        config = Config(canSendMail: true, silentAfterHours: 6);
        Lock();
        api.CloudReadings.Add(Fixtures.Reading("s1", Morning.AddHours(-8))); // nadawca (i zbieracze) wstają po nocy
        var engine = Engine();

        await engine.CycleAsync(Ct);
        for (var i = 0; i < 5; i++)
        {
            Advance(TimeSpan.FromMinutes(5));
            await engine.CycleAsync(Ct);
        }
        Assert.Empty(api.Alerts);                       // 25 min – zbieracze mają czas na odczyty
        Assert.Empty(mail.Sent);

        Advance(TimeSpan.FromMinutes(6));
        await engine.CycleAsync(Ct);

        var alert = Assert.Single(api.Alerts);          // s2 bez odczytów liczy się od startu nadawcy – jeszcze nie
        Assert.Equal((AlertKind.SensorSilent, "s1"), (alert.Kind, alert.Sensor));
        Assert.Single(mail.Sent);
    }

    [Fact]
    public async Task SilentAgent_IsReportedOncePerEpisode()
    {
        Lock();
        lockPolicy.Observe(new SyncResponse(1, Morning, 1, new("pi", true, 1))
        {
            Agents = [new("pi", true, 1, 0), new("android", false, null, 2000), new("bestia5", true, 2, 30)]
        });
        var engine = Engine();

        await engine.CycleAsync(Ct);
        Advance(Minute);
        await engine.CycleAsync(Ct);

        var silent = Assert.Single(mail.Sent);
        Assert.Equal("Agent android milczy", silent.Subject);
        Assert.Contains(api.Outbox.Keys, k => k == "agent-silent|android|2026-11-17T05:26Z");
    }

    [Fact]
    public async Task Heartbeat_OnlyWhenEverythingWorks_AndUrlNeverLogged()
    {
        api.Credentials = api.Credentials with { HealthchecksPingUrl = "https://hc-ping.com/sekretny-adres" };
        Lock();
        var engine = Engine();

        await engine.CycleAsync(Ct);
        Advance(Minute);
        await engine.CycleAsync(Ct);
        Assert.Single(heartbeat.Pings);

        Advance(5 * Minute);
        sync = sync with { ConsecutiveFailures = 2 };  // chmura nie odpowiada – strażnik ma zamilknąć
        await engine.CycleAsync(Ct);
        Assert.Single(heartbeat.Pings);

        sync = sync with { ConsecutiveFailures = 0 };
        await engine.CycleAsync(Ct);
        Assert.Equal(2, heartbeat.Pings.Count);
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("sekretny-adres"));
    }

    [Fact]
    public async Task MissingGmailPassword_NoMailAndOneError()
    {
        api.Credentials = new("nadawca@gmail.com", null, null);
        Lock();
        Dry("s1");
        var engine = Engine();

        await engine.CycleAsync(Ct);
        await engine.CycleAsync(Ct);

        Assert.Empty(mail.Sent);
        Assert.Equal(0, gatewaysCreated);
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public void AlertKeys_AreStable_AndCountReminderPeriods()
    {
        var opened = new DateTimeOffset(2026, 11, 17, 6, 0, 0, 123, TimeSpan.Zero);
        var alert = new CloudAlert(1, "cisy", AlertKind.LowBattery, "bateria 15%", opened, null, null);
        var day = TimeSpan.FromHours(24);

        Assert.Equal("alert|cisy|LowBattery|2026-11-17T06:00:00.123000Z|0", NotifierEngine.AlertKey(alert, opened.AddHours(30), day));
        var notified = alert with { LastNotifiedAt = opened };
        Assert.EndsWith("|1", NotifierEngine.AlertKey(notified, opened.AddHours(25), day));
        Assert.EndsWith("|2", NotifierEngine.AlertKey(notified, opened.AddHours(49), day));
        Assert.Equal(NotifierEngine.MessageId("report|07:00|2026-11-17"), NotifierEngine.MessageId("report|07:00|2026-11-17"));
        Assert.EndsWith("@glebosaurus", NotifierEngine.MessageId("x"));
    }

    private static string Gzip(string text)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.SmallestSize)) gzip.Write(Encoding.UTF8.GetBytes(text));
        return Convert.ToBase64String(buffer.ToArray());
    }

    private sealed class FakeHeartbeat : IHeartbeat
    {
        public List<Uri> Pings { get; } = [];

        public Task PingAsync(Uri url, CancellationToken cancellationToken)
        {
            Pings.Add(url);
            return Task.CompletedTask;
        }
    }
}
