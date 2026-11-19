using System.Text.Json;
using System.Text.Json.Serialization;
using Glebosaurus.Core.Cloud;
using Glebosaurus.Core.Domain;
using Glebosaurus.Core.Monitoring;
using Microsoft.Extensions.Logging.Abstractions;

namespace Glebosaurus.Tests;

/// <summary>
/// Lokalny panel agenta: kopia odczytów wszystkich agentów w SQLite (bez ponownego wysyłania), pętla kopii,
/// status (kto czyta który czujnik, nadawca, agenci, alerty z chmury) i akcje przez polecenia w chmurze.
/// </summary>
public sealed class CloudPanelTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);
    private const string Source = "abcdefghijklmnopqrst.supabase.co";

    private readonly TempStore db = new();
    private readonly ManualTimeProvider clock = new(Fixtures.T0);
    private readonly FakeCloudApi api;
    private readonly CloudAgentConfig config;
    private CloudSyncState sync = CloudSyncState.NotStarted;
    private MonitorRuntimeState collector = MonitorRuntimeState.NotStarted;
    private int wakes;

    public CloudPanelTests()
    {
        api = new(clock);
        var agentConfig = Fixtures.Config(Fixtures.Sensor("s1", "Cisy"), Fixtures.Sensor("s2", "Zioła", uuid: "07aaad78f40b0a75"));
        agentConfig.MonitorName = "bestia5";
        config = new(agentConfig, TimeZoneInfo.Utc, 7, new("bestia5", true, 2),
            new("nadawca@gmail.com", ["alerty@example.com"], [], ["admin@example.com"]), ["Czujnik „Stary” pominięty: brak danych Tuya."]);
        db.Store.InitializeAsync(Ct).GetAwaiter().GetResult();
    }

    public void Dispose() => db.Dispose();

    private CloudPanel Panel(ReadingMirrorState? mirror = null) => new(Source, () => config, db.Store, api, () => collector, () => sync,
        () => MailRuntimeState.Unused, mirror is null ? null : () => mirror, () => wakes++, clock, NullLogger.Instance);

    private static readonly MonitorHostInfo Host = new("test", "glebosaurus.config.json", "glebosaurus.db", "logs");

    private void SyncedView(string? notifier = "pi")
    {
        sync = CloudSyncState.NotStarted with { LastSuccessAt = clock.GetUtcNow(), LastAttemptAt = clock.GetUtcNow(), UnsentReadings = 3, ConfigVersion = 7 };
        sync = sync with
        {
            View = new(clock.GetUtcNow(), new("bestia5", true, 2),
                [new("s1", "bestia5", 1500, new OtherHearing("telefon", -71, 40)), new("s2", "telefon", 600, null)],
                notifier is null ? null : new(notifier, 3, 240),
                [new("bestia5", true, 2, 0) { Status = new("0.2.3", "windows", "Shiny (WinRT)") }, new("pi", true, 1, 30),
                 new("telefon", false, null, 3600)
                 {
                     Status = new("0.2.3", "android", "ok")
                     {
                         BatteryPercent = 25, Charging = false, BatteryTemperatureCelsius = 33.5, ThermalStatus = "none", Network = "wifi", WifiDbm = -58
                     }
                 }])
        };
    }

    // ---- kopia odczytów w SQLite ----------------------------------------------------------------------------------

    [Fact]
    public async Task ReadingsFromCloud_AreNotUploadedAgain_AndOwnUnsentStayUnsent()
    {
        var own = Fixtures.Reading("s1", Fixtures.T0 - Minute);
        await db.Store.AddReadingAsync(own, Ct);
        var other = Fixtures.Reading("s2", Fixtures.T0 - 2 * Minute, moisture: 33);

        var added = await db.Store.AddFromCloudAsync(Source, [own with { MoisturePercent = 99 }, other], 42, Ct);

        Assert.Equal(1, added);
        Assert.Equal([own], await db.Store.GetUnsentAsync(10, Ct));
        Assert.Equal(50, (await db.Store.GetLatestAsync("s1", Ct))!.MoisturePercent);   // własny odczyt bez zmian
        Assert.Equal(other, await db.Store.GetLatestAsync("s2", Ct));
        Assert.Equal(42, await db.Store.GetFeedCursorAsync(Source, Ct));
        Assert.Null(await db.Store.GetFeedCursorAsync("inny-projekt.supabase.co", Ct));
    }

    // ---- pętla kopii ------------------------------------------------------------------------------------------------

    private CloudReadingMirror Mirror(int pageSize = 3) =>
        new(api, db.Store, Source, TimeSpan.FromDays(30), clock, NullLogger.Instance, new() { PageSize = pageSize });

    [Fact]
    public async Task Mirror_PullsAllPages_ThenContinuesFromCursor()
    {
        api.AddToFeed([.. Enumerable.Range(1, 7).Select(i => Fixtures.Reading("s2", Fixtures.T0 - i * Minute))]);
        var mirror = Mirror();

        Assert.True(await mirror.PullAsync(Ct));
        Assert.Equal([(0L, 3), (3L, 3), (6L, 3)], api.FeedCalls);
        Assert.Equal((7L, 7), (mirror.State.Cursor!.Value, mirror.State.Added));
        Assert.Equal(0, await db.Store.CountUnsentAsync(Ct));

        api.AddToFeed(Fixtures.Reading("s1", Fixtures.T0));
        Assert.True(await Mirror().PullAsync(Ct));   // także po restarcie – numer jest w bazie
        Assert.Equal((7L, 3), api.FeedCalls[^1]);
        Assert.Equal(Fixtures.T0, (await db.Store.GetLatestAsync("s1", Ct))!.At);
    }

    [Fact]
    public async Task Mirror_SkipsReadingsOlderThanRetention_ButMovesOn()
    {
        api.AddToFeed(Fixtures.Reading("s1", Fixtures.T0 - TimeSpan.FromDays(40)), Fixtures.Reading("s1", Fixtures.T0 - TimeSpan.FromDays(1)));
        var mirror = Mirror();

        await mirror.PullAsync(Ct);

        Assert.Equal(1, mirror.State.Added);
        Assert.Equal(2, await db.Store.GetFeedCursorAsync(Source, Ct));
        Assert.Equal(Fixtures.T0 - TimeSpan.FromDays(1), (await db.Store.GetReadingBoundsAsync("s1", Ct))!.Value.First);
    }

    [Fact]
    public async Task Mirror_WithoutCloud_KeepsCursorAndReportsError_RevokedTokenStopsIt()
    {
        api.AddToFeed(Fixtures.Reading("s1", Fixtures.T0));
        var mirror = Mirror();
        api.FeedFailures.Enqueue(new(CloudErrorKind.Unavailable, "readings_feed: brak sieci"));

        Assert.False(await mirror.PullAsync(Ct));
        Assert.Equal("readings_feed: brak sieci", mirror.State.LastError);
        Assert.Null(await db.Store.GetFeedCursorAsync(Source, Ct));

        Assert.True(await mirror.PullAsync(Ct));
        Assert.Null(mirror.State.LastError);

        api.FeedFailures.Enqueue(new(CloudErrorKind.Unauthorized, "readings_feed: 403"));
        Assert.False(await mirror.PullAsync(Ct));
        Assert.True(mirror.State.Stopped);
        Assert.False(await mirror.PullAsync(Ct));
        Assert.Equal(3, api.FeedCalls.Count);
        await mirror.RunAsync(Ct);   // zatrzymana kopia od razu kończy pętlę
    }

    // ---- status ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Status_ShowsWhoReadsWhat_TheNotifier_Agents_AndAlertsFromCloud()
    {
        SyncedView();
        api.Alerts.Add(new(5, "s2", AlertKind.NeedsWater, "wilgotność 12%", Fixtures.T0 - Minute, null, null));
        await db.Store.AddFromCloudAsync(Source, [Fixtures.Reading("s2", Fixtures.T0 - Minute, moisture: 12)], 1, Ct);
        clock.Advance(TimeSpan.FromSeconds(20));

        var status = await Panel(new ReadingMirrorState(Fixtures.T0, Fixtures.T0, null, 1, 1, false)).StatusAsync(Host, Ct);

        var s1 = status.Sensors.Single(s => s.Id == "s1");
        Assert.Equal(("bestia5", true, Fixtures.T0.AddSeconds(1500)), (s1.Holder, s1.HolderIsMe, s1.HolderUntil));
        Assert.Equal(("telefon", -71), (s1.BestOtherAgent, s1.BestOtherRssi));
        var s2 = status.Sensors.Single(s => s.Id == "s2");
        Assert.Equal(("telefon", false), (s2.Holder, s2.HolderIsMe));
        Assert.Equal(12, s2.Latest!.MoisturePercent);   // odczyt innego agenta z kopii
        Assert.Equal(["czas podlać"], s2.OpenAlerts);
        Assert.Equal("s2", Assert.Single(status.Alerts).SensorId);

        var cloud = status.Cloud!;
        Assert.Equal((Source, "bestia5", "pi", false), (cloud.Project, cloud.Agent, cloud.Notifier, cloud.NotifierIsMe));
        Assert.Equal(Fixtures.T0.AddSeconds(240), cloud.NotifierUntil);
        Assert.Equal((3, 7L), (cloud.UnsentReadings, cloud.ConfigVersion!.Value));
        Assert.Equal(["Czujnik „Stary” pominięty: brak danych Tuya."], cloud.ConfigWarnings);
        Assert.Equal([("bestia5", true, 1), ("pi", false, 0), ("telefon", false, 1)],
            cloud.Agents.Select(a => (a.Name, a.IsMe, a.HeldSensors)).ToList());
        var phone = cloud.Agents.Single(a => a.Name == "telefon");
        Assert.Equal(Fixtures.T0.AddSeconds(-3600), phone.LastSeenAt);
        Assert.Equal("Glebosaurus 0.2.3 · Android", phone.Device!.Program);
        Assert.Equal([new("Bateria", "25 % · bez ładowarki"), new("Temperatura", "33,5 °C (akumulator) · system: w normie"),
            new("Sieć", "Wi-Fi -58 dBm (dobry)"), new DeviceDetail("Bluetooth", "ok")], phone.Device.Details);
        Assert.Equal(["Słaba bateria (25 %) – podłącz telefon do ładowarki."], phone.Device.Warnings);
        Assert.Equal("Glebosaurus 0.2.3 · Windows", cloud.Agents.Single(a => a.IsMe).Device!.Program);
        Assert.Null(cloud.Agents.Single(a => a.Name == "pi").Device);   // bez sygnału życia (albo baza sprzed migracji)
        Assert.True(cloud.MirrorEnabled);
        Assert.Equal(Fixtures.T0, cloud.MirrorAt);

        Assert.True(status.Mail.Configured);
        Assert.Equal(["alerty@example.com"], status.Mail.ReportRecipients);   // pusta lista raportów = odbiorcy alertów
        Assert.True(status.Reports.OnRequest.Available);
    }

    [Fact]
    public async Task Status_HasNoSecrets()
    {
        SyncedView();
        var json = JsonSerializer.Serialize(await Panel().StatusAsync(Host, Ct),
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } });

        Assert.Contains("\"holder\":\"bestia5\"", json);
        Assert.DoesNotContain("a1b2c3d4e5f6", json);              // klucz lokalny czujnika
        Assert.DoesNotContain("1234567890123456789012", json);    // deviceId
    }

    [Fact]
    public async Task Status_WithoutCloudYet_StillShowsSensorsFromConfig()
    {
        api.Alerts.Add(new(5, "s2", AlertKind.NeedsWater, "x", Fixtures.T0, null, null));
        var status = await Panel().StatusAsync(Host, Ct);

        Assert.Equal(["s1", "s2"], status.Sensors.Select(s => s.Id).ToList());
        Assert.All(status.Sensors, s => Assert.Null(s.Holder));
        Assert.Null(status.Cloud!.Notifier);
        Assert.False(status.Reports.OnRequest.Available);
        Assert.Single(status.Alerts);
    }

    [Fact]
    public async Task Status_KeepsLastAlerts_WhenCloudIsDown()
    {
        SyncedView();
        api.Alerts.Add(new(5, "s2", AlertKind.NeedsWater, "x", Fixtures.T0, null, null));
        var panel = Panel();
        Assert.Single((await panel.StatusAsync(Host, Ct)).Alerts);

        api.Alerts.Clear();
        clock.Advance(Minute);
        Assert.Empty((await panel.StatusAsync(Host, Ct)).Alerts);   // po 30 s pyta chmurę od nowa
    }

    // ---- akcje ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ReadOnRequest_GoesToCloud_AndWaitsUntilANewReadingArrives()
    {
        SyncedView();
        var panel = Panel();

        var result = await panel.RequestReadAsync(null, Ct);

        Assert.Equal([("s1", ReadRequestStatus.Queued), ("s2", ReadRequestStatus.Queued)], result.Items.Select(i => (i.SensorId, i.Status)).ToList());
        Assert.Equal([("read_now", "s1"), ("read_now", "s2")], api.CreatedCommands);
        Assert.Equal(1, wakes);
        var status = await panel.StatusAsync(Host, Ct);
        Assert.All(status.Sensors, s => Assert.True(s.ReadRequested));
        Assert.All(status.Sensors, s => Assert.Equal(Fixtures.T0 + Minute, s.ReadOnRequestFrom));

        Assert.Equal(ReadRequestStatus.AlreadyQueued, Assert.Single((await panel.RequestReadAsync(["s1"], Ct)).Items).Status);

        clock.Advance(Minute);
        await db.Store.AddFromCloudAsync(Source, [Fixtures.Reading("s1", clock.GetUtcNow())], 1, Ct);
        status = await panel.StatusAsync(Host, Ct);
        Assert.False(status.Sensors.Single(s => s.Id == "s1").ReadRequested);
        Assert.True(status.Sensors.Single(s => s.Id == "s2").ReadRequested);

        clock.Advance(CloudPanel.ReadPending);   // wynik nie przyszedł – po 3 min przycisk wraca
        Assert.All((await panel.StatusAsync(Host, Ct)).Sensors, s => Assert.False(s.ReadRequested));
    }

    [Fact]
    public async Task ReadOnRequest_UnknownSensor_AndCloudDown()
    {
        api.CommandFailures.Enqueue(new(CloudErrorKind.Unavailable, "command_create: brak sieci"));

        var result = await Panel().RequestReadAsync(["s1", "brak"], Ct);

        Assert.Equal([ReadRequestStatus.Unavailable, ReadRequestStatus.Unknown], result.Items.Select(i => i.Status).ToList());
        Assert.Equal(0, wakes);
    }

    [Fact]
    public async Task ReportOnRequest_GoesToTheNotifierThroughCloud_OncePerMinute()
    {
        SyncedView();
        var panel = Panel();

        Assert.Equal(ReportRequestStatus.Queued, (await panel.RequestReportAsync(Ct)).Status);
        Assert.Equal([("report_now", (string?)null)], api.CreatedCommands);
        var tooSoon = await panel.RequestReportAsync(Ct);
        Assert.Equal((ReportRequestStatus.TooSoon, Fixtures.T0 + Minute), (tooSoon.Status, tooSoon.AvailableAt!.Value));
        var onRequest = (await panel.StatusAsync(Host, Ct)).Reports.OnRequest;
        Assert.Equal((Fixtures.T0, (string?)null, Fixtures.T0 + Minute), (onRequest.LastAttemptAt!.Value, onRequest.LastError, onRequest.AvailableFrom!.Value));

        clock.Advance(Minute);
        api.CommandFailures.Enqueue(new(CloudErrorKind.Unavailable, "command_create: brak sieci"));
        Assert.Equal(ReportRequestStatus.Unavailable, (await panel.RequestReportAsync(Ct)).Status);
        Assert.Equal("command_create: brak sieci", (await panel.StatusAsync(Host, Ct)).Reports.OnRequest.LastError);
    }
}
