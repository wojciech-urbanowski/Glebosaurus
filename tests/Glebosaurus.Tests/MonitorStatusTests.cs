using System.Text.Json;
using Glebosaurus.Core.Config;
using Glebosaurus.Core.Domain;
using Glebosaurus.Core.Logging;
using Glebosaurus.Core.Mail;
using Glebosaurus.Core.Monitoring;
using Glebosaurus.Core.Reports;

namespace Glebosaurus.Tests;

/// <summary>Stan silnika dla panelu WWW (migawka), składanie statusu, końcówka dziennika, najbliższy raport, sekcja „web”.</summary>
public class MonitorStatusTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly MonitorHostInfo Host = new("Test BLE", "C:/cfg/glebosaurus.config.json", "C:/cfg/glebosaurus.db", "C:/cfg/logs");

    // ---- migawka stanu silnika ----------------------------------------------------------------------------------

    [Fact]
    public async Task SuccessfulCycleIsVisibleInTheState()
    {
        using var db = new TempStore();
        var reader = new FakeSensorReader();
        var clock = new ManualTimeProvider(Fixtures.T0);
        var engine = Engines.Create(Fixtures.Config(), db.Store, reader, new FakeMail(), clock);
        Assert.Same(MonitorRuntimeState.NotStarted, engine.State);

        await engine.CycleAsync(Ct);

        var state = engine.State;
        Assert.Equal(Fixtures.T0, state.StartedAt);
        Assert.Equal(Fixtures.T0, state.LastCycleAt);
        Assert.Null(state.LastCycleError);
        Assert.Empty(state.ReadingNow);
        var sensor = state.Sensors["s1"];
        Assert.Equal(SensorReadFailure.None, sensor.LastResult);
        Assert.Equal(Fixtures.T0, sensor.LastAttemptAt);
        Assert.Equal(0, sensor.Failures);
        Assert.Null(sensor.RetryAt);
    }

    [Fact]
    public async Task StateShowsWhichSensorIsBeingReadWhileTheReadLasts()
    {
        using var db = new TempStore();
        var reader = new FakeSensorReader();
        var engine = Engines.Create(Fixtures.Config(), db.Store, reader, new FakeMail(), new ManualTimeProvider(Fixtures.T0));
        MonitorRuntimeState? during = null;
        reader.Script = sensor =>
        {
            during = engine.State;
            return Fixtures.Ok(sensor, 50);
        };

        await engine.CycleAsync(Ct);

        Assert.NotNull(during);
        Assert.Equal(["s1"], during.ReadingNow);
        Assert.Equal(Fixtures.T0, during.ReadingSince);
        Assert.Empty(engine.State.ReadingNow);
    }

    [Fact]
    public async Task FailedReadShowsResultFailuresRetryAndLastSignal()
    {
        using var db = new TempStore();
        var reader = new FakeSensorReader();
        var clock = new ManualTimeProvider(Fixtures.T0);
        var engine = Engines.Create(Fixtures.Config(), db.Store, reader, new FakeMail(), clock);

        reader.Script = sensor => Fixtures.Ok(sensor, 50) with { SignalDbm = -77 };
        await engine.CycleAsync(Ct);
        clock.Advance(TimeSpan.FromMinutes(16));
        reader.Script = sensor => new(sensor, null, SensorReadFailure.NotFound);
        await engine.CycleAsync(Ct);

        var sensorState = engine.State.Sensors["s1"];
        Assert.Equal(SensorReadFailure.NotFound, sensorState.LastResult);
        Assert.Equal(1, sensorState.Failures);
        Assert.Equal(clock.GetUtcNow() + TimeSpan.FromMinutes(1), sensorState.RetryAt);
        Assert.Equal(-77, sensorState.LastSignalDbm);
        Assert.Equal(Fixtures.T0, sensorState.LastSignalAt);

        reader.Script = sensor => new(sensor, null, SensorReadFailure.Error, "brak odpowiedzi");
        clock.Advance(TimeSpan.FromMinutes(2));
        await engine.CycleAsync(Ct);
        Assert.Equal(SensorReadFailure.Error, engine.State.Sensors["s1"].LastResult);
        Assert.Equal("brak odpowiedzi", engine.State.Sensors["s1"].LastError);
        Assert.Equal(2, engine.State.Sensors["s1"].Failures);
    }

    [Fact]
    public async Task MailFailureAndLaterSuccessAreVisibleInTheState()
    {
        using var db = new TempStore();
        var reader = new FakeSensorReader { Script = sensor => Fixtures.Ok(sensor, 18) };
        var mail = new FakeMail { Failure = new MailTransportException("brak sieci", new IOException()) };
        var clock = new ManualTimeProvider(Fixtures.T0);
        var engine = Engines.Create(Fixtures.Config(), db.Store, reader, mail, clock);

        await engine.CycleAsync(Ct);
        clock.Advance(TimeSpan.FromMinutes(15));
        await engine.CycleAsync(Ct);                    // alert „czas podlać”, wysyłka się nie udaje

        var failed = engine.State.Mail;
        Assert.Contains("brak sieci", failed.LastError);
        Assert.Equal(clock.GetUtcNow(), failed.LastErrorAt);
        Assert.Equal(clock.GetUtcNow() + MonitorEngine.MailRetryDelay, failed.RetryAt);
        Assert.Null(failed.LastSentAt);

        mail.Failure = null;
        clock.Advance(MonitorEngine.MailRetryDelay);
        await engine.CycleAsync(Ct);
        Assert.Equal(clock.GetUtcNow(), engine.State.Mail.LastSentAt);
        Assert.Null(engine.State.Mail.RetryAt);
    }

    [Fact]
    public async Task CycleErrorIsRecordedInTheState()
    {
        using var db = new TempStore();
        var reader = new FakeSensorReader { Script = _ => throw new InvalidOperationException("adapter BLE zniknął") };
        var engine = Engines.Create(Fixtures.Config(), db.Store, reader, new FakeMail(), new ManualTimeProvider(Fixtures.T0));

        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.CycleAsync(Ct));

        Assert.Equal("adapter BLE zniknął", engine.State.LastCycleError);
        Assert.Equal(Fixtures.T0, engine.State.LastCycleErrorAt);
        Assert.Null(engine.State.LastCycleAt);
        Assert.Empty(engine.State.ReadingNow);
    }

    // ---- status dla panelu --------------------------------------------------------------------------------------

    [Fact]
    public async Task StatusCombinesStoredReadingsAlertsAndRuntimeState()
    {
        using var db = new TempStore();
        var fikus = Fixtures.Sensor("s2", "Fikus", "17aaad78f40b0a74");
        fikus.Tuya.Mac = "DC:23:51:CC:03:CA";
        var config = Fixtures.Config(Fixtures.Sensor("s1", "Monstera"), fikus);
        config.Mail!.ReportRecipients = [];
        config.Reports.Times = ["07:00", "19:00"];
        await db.Store.AddReadingAsync(Fixtures.Reading("s1", Fixtures.T0 - TimeSpan.FromMinutes(5), moisture: 20, battery: 15), Ct);
        await db.Store.OpenAlertAsync("s1", AlertKind.NeedsWater, "Wilgotność 20% (próg 25%).", Fixtures.T0 - TimeSpan.FromMinutes(1), Ct);

        var retryAt = Fixtures.T0 + TimeSpan.FromMinutes(4);
        var state = MonitorRuntimeState.NotStarted with
        {
            StartedAt = Fixtures.T0 - TimeSpan.FromHours(1),
            ReadingNow = ["s2"],
            Sensors = new Dictionary<string, SensorRuntimeState>
            {
                ["s2"] = new(Fixtures.T0, SensorReadFailure.NotFound, null, 3, retryAt, -90, Fixtures.T0 - TimeSpan.FromMinutes(30))
            }
        };

        var status = await MonitorStatusBuilder.BuildAsync(config, db.Store, state, Host, Fixtures.T0, TimeZoneInfo.Utc, Ct);

        var monstera = status.Sensors[0];
        Assert.Equal("Monstera", monstera.Name);
        Assert.Equal(20, monstera.Latest!.MoisturePercent);
        Assert.Equal("red", monstera.MoistureTone);
        Assert.True(monstera.BatteryLow);
        Assert.Equal(Fixtures.T0 + TimeSpan.FromMinutes(10), monstera.NextReadAt);   // 15 min od odczytu
        Assert.Equal(["czas podlać"], monstera.OpenAlerts);
        Assert.Null(monstera.LastResult);                                           // od startu jeszcze nie próbował

        var fikusStatus = status.Sensors[1];
        Assert.Null(fikusStatus.Latest);
        Assert.Equal(retryAt, fikusStatus.NextReadAt);                              // brak odczytu, ale trwa odstęp po porażce
        Assert.True(fikusStatus.ReadingNow);
        Assert.Equal("notFound", fikusStatus.LastResult);
        Assert.Equal(3, fikusStatus.Failures);
        Assert.Equal(-90, fikusStatus.LastSignalDbm);

        var alert = Assert.Single(status.Alerts);
        Assert.Equal("Monstera", alert.SensorName);
        Assert.Equal("NeedsWater", alert.Kind);
        Assert.Equal(["Fikus"], status.Monitor.ReadingNow);
        Assert.Equal(["ktos@example.com"], status.Mail.ReportRecipients);           // pusta lista = odbiorcy alertów
        Assert.Equal(new DateTimeOffset(2026, 11, 13, 7, 0, 0, TimeSpan.Zero), status.Reports.NextAt);
    }

    [Fact]
    public async Task StatusShowsRequestedReadAndWhenTheNextRequestIsAllowed()
    {
        using var db = new TempStore();
        await db.Store.AddReadingAsync(Fixtures.Reading("s1", Fixtures.T0 - TimeSpan.FromMinutes(1)), Ct);
        var state = MonitorRuntimeState.NotStarted with
        {
            Sensors = new Dictionary<string, SensorRuntimeState>
            {
                ["s1"] = new(Fixtures.T0 - TimeSpan.FromSeconds(20), SensorReadFailure.None, null, 0, null, null, null)
            },
            RequestedReads = ["s1"]
        };

        var sensor = (await MonitorStatusBuilder.BuildAsync(Fixtures.Config(), db.Store, state, Host, Fixtures.T0, TimeZoneInfo.Utc, Ct)).Sensors[0];

        Assert.True(sensor.ReadRequested);
        Assert.Equal(Fixtures.T0, sensor.NextReadAt);                                   // zlecony – w najbliższym cyklu
        Assert.Equal(Fixtures.T0 + TimeSpan.FromSeconds(40), sensor.ReadOnRequestFrom);  // minuta od ostatniej próby

        var later = (await MonitorStatusBuilder.BuildAsync(Fixtures.Config(), db.Store, MonitorRuntimeState.NotStarted, Host, Fixtures.T0, TimeZoneInfo.Utc, Ct)).Sensors[0];
        Assert.False(later.ReadRequested);
        Assert.Null(later.ReadOnRequestFrom);
    }

    [Fact]
    public async Task StatusShowsTheReportOnRequestAndWhenTheNextOneIsAllowed()
    {
        using var db = new TempStore();
        var state = MonitorRuntimeState.NotStarted with
        {
            ReportOnRequest = new(null, Fixtures.T0 - TimeSpan.FromSeconds(15), "brak sieci")
        };

        var report = (await MonitorStatusBuilder.BuildAsync(Fixtures.Config(), db.Store, state, Host, Fixtures.T0, TimeZoneInfo.Utc, Ct)).Reports.OnRequest;

        Assert.Equal(new ReportOnRequestStatus(true, false, Fixtures.T0 - TimeSpan.FromSeconds(15), "brak sieci", Fixtures.T0 + TimeSpan.FromSeconds(45)), report);

        var queued = state with { ReportOnRequest = new(Fixtures.T0, null, null) };
        var noMail = Fixtures.Config();
        noMail.Mail = null;
        Assert.Equal(new ReportOnRequestStatus(true, true, null, null, null),
            (await MonitorStatusBuilder.BuildAsync(Fixtures.Config(), db.Store, queued, Host, Fixtures.T0, TimeZoneInfo.Utc, Ct)).Reports.OnRequest);
        Assert.False((await MonitorStatusBuilder.BuildAsync(noMail, db.Store, MonitorRuntimeState.NotStarted, Host, Fixtures.T0, TimeZoneInfo.Utc, Ct)).Reports.OnRequest.Available);
    }

    [Fact]
    public async Task StatusNeverContainsSensorKeysOrTheGmailPassword()
    {
        using var db = new TempStore();
        var config = Fixtures.Config();
        config.Mail!.AppPassword = "sekret-gmail-xyz";
        var sensor = config.Sensors[0];

        var status = await MonitorStatusBuilder.BuildAsync(config, db.Store, MonitorRuntimeState.NotStarted, Host, Fixtures.T0, TimeZoneInfo.Utc, Ct);
        var json = JsonSerializer.Serialize(status, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.DoesNotContain("sekret-gmail-xyz", json);
        Assert.DoesNotContain(sensor.Tuya.LocalKeyHex!, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(sensor.Tuya.DeviceId, json);
        Assert.Contains(sensor.Tuya.Mac, json);
    }

    // ---- końcówka dziennika -------------------------------------------------------------------------------------

    private const string SampleLog = """
        2026-11-13 14:15:08.818 [INF] Glebosaurus: Odczyt czujników: Cisy.
        2026-11-13 14:15:30.100 [WRN] Glebosaurus: Cisy: nie znaleziono w reklamach BLE.
        2026-11-13 14:16:00.000 [ERR] Glebosaurus: Cykl monitora zakończył się błędem; kolejna próba za chwilę.
        System.InvalidOperationException: adapter
           at Glebosaurus.Something()
        2026-11-13 14:17:00.000 [INF] Glebosaurus.Web: Panel WWW: zalogowano z 192.168.1.20.
        """;

    [Fact]
    public void LogTailKeepsStackTraceWithItsEntryAndFiltersWarnings()
    {
        var all = LogTail.Parse(SampleLog, 100, warningsOnly: false);
        Assert.Equal(4, all.Count);
        Assert.Equal(new LogLine("14:17:00", "INF", "Glebosaurus.Web", "Panel WWW: zalogowano z 192.168.1.20."), all[3]);
        Assert.Contains("System.InvalidOperationException: adapter", all[2].Message);
        Assert.Contains("at Glebosaurus.Something()", all[2].Message);

        var warnings = LogTail.Parse(SampleLog, 100, warningsOnly: true);
        Assert.Equal(["WRN", "ERR"], warnings.Select(e => e.Level));

        var last = LogTail.Parse(SampleLog, 2, warningsOnly: false);
        Assert.Equal(["14:16:00", "14:17:00"], last.Select(e => e.Time));
    }

    [Fact]
    public void LogTailCanKeepOnlyEntriesAboutOneSensor()
    {
        var cisy = LogTail.Parse(SampleLog, 100, warningsOnly: false, contains: "Cisy");
        Assert.Equal(["14:15:08", "14:15:30"], cisy.Select(e => e.Time));

        var limited = LogTail.Parse(SampleLog, 1, warningsOnly: false, contains: "Cisy");     // limit liczy się po filtrze
        Assert.Equal("14:15:30", Assert.Single(limited).Time);

        Assert.Equal("WRN", Assert.Single(LogTail.Parse(SampleLog, 100, warningsOnly: true, contains: "Cisy")).Level);
        Assert.Empty(LogTail.Parse(SampleLog, 100, warningsOnly: false, contains: "cisy"));          // wielkość liter ma znaczenie
    }

    // ---- najbliższy raport ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(6, 0, 7, 0, 0)]       // przed poranną porą → dziś 07:00
    [InlineData(10, 0, 19, 0, 0)]     // po porannej → dziś 19:00
    [InlineData(20, 30, 7, 0, 1)]     // po wieczornej → jutro 07:00
    public void NextReportIsTheNearestFutureTime(int hour, int minute, int expectedHour, int expectedMinute, int addDays)
    {
        var now = new DateTimeOffset(2026, 11, 13, hour, minute, 0, TimeSpan.Zero);
        var expected = new DateTimeOffset(2026, 11, 13 + addDays, expectedHour, expectedMinute, 0, TimeSpan.Zero);
        Assert.Equal(expected, ReportSchedule.Next(["19:00", "07:00"], now, TimeZoneInfo.Utc));
    }

    [Fact]
    public void NoReportTimesMeansNoNextReport() =>
        Assert.Null(ReportSchedule.Next([], Fixtures.T0, TimeZoneInfo.Utc));

    // ---- sekcja „web” w konfiguracji ------------------------------------------------------------------------------

    [Theory]
    [InlineData(80, 12, null)]
    [InlineData(70000, 12, null)]
    [InlineData(8470, 0, null)]
    [InlineData(8470, 12, "192.168.1.0/33")]
    [InlineData(8470, 12, "siec-domowa")]
    public void InvalidWebSectionIsRejected(int port, double sessionHours, string? network)
    {
        var config = Fixtures.Config();
        config.Web = new() { Port = port, SessionHours = sessionHours, AllowedNetworks = network is null ? [] : [network] };
        Assert.Throws<InvalidDataException>(() => AgentConfigLoader.Validate(config));
    }

    [Fact]
    public void WebSectionAcceptsNetworksAndSingleAddressesAndAlwaysAllowsLoopback()
    {
        var config = Fixtures.Config();
        config.Web = new() { AllowedNetworks = ["192.168.1.0/24", "10.1.2.3", "fd12::/16"] };
        AgentConfigLoader.Validate(config);

        var networks = config.Web.ParseAllowedNetworks().Select(n => n.ToString()).ToList();
        Assert.Equal(["192.168.1.0/24", "10.1.2.3/32", "fd12::/16", "127.0.0.0/8", "::1/128"], networks);
        Assert.Equal(WebConfig.PrivateNetworks.Count, new WebConfig().ParseAllowedNetworks().Count);
    }

    [Fact]
    public void WebSectionIsReadFromTheConfigFile()
    {
        var directory = Directory.CreateTempSubdirectory("glebosaurus-config-");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "s1.credentials.json"),
                """{ "mac": "DC:23:51:CC:03:C9", "uuid": "07aaad78f40b0a74", "deviceId": "1234567890123456789012", "localKeyHex": "a1b2c3d4e5f6", "productId": "gvygg3m8" }""");
            var path = Path.Combine(directory.FullName, "glebosaurus.config.json");
            File.WriteAllText(path, """
                {
                  "sensors": [ { "id": "s1", "tuyaFile": "s1.credentials.json" } ],
                  // panel WWW
                  "web": { "port": 9443, "allowedNetworks": ["192.168.0.0/16"], "sessionHours": 2 },
                }
                """);

            var config = AgentConfigLoader.Load(path);

            Assert.NotNull(config.Web);
            Assert.True(config.Web.Enabled);
            Assert.Equal(9443, config.Web.Port);
            Assert.Equal(2, config.Web.SessionHours);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
