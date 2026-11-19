using System.Text.Json;
using Glebosaurus.Core.Config;
using Glebosaurus.Core.Domain;
using Glebosaurus.Core.Logging;
using Glebosaurus.Core.Mail;
using Glebosaurus.Core.Monitoring;
using Glebosaurus.Core.Reports;

namespace Glebosaurus.Tests;

/// <summary>Składanie statusu dla panelu WWW, końcówka dziennika, najbliższy raport, sekcja „web”.</summary>
public class MonitorStatusTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly MonitorHostInfo Host = new("Test BLE", "C:/cfg/glebosaurus.config.json", "C:/cfg/glebosaurus.db", "C:/cfg/logs");

    // ---- status dla panelu --------------------------------------------------------------------------------------

    [Fact]
    public async Task StatusCombinesStoredReadingsAlertsAndRuntimeState()
    {
        using var db = new TempStore();
        var fikus = Fixtures.Sensor("s2", "Fikus", "17aaad78f40b0a74");
        fikus.Tuya.Mac = "DC:23:51:CC:03:CA";
        var config = Fixtures.Config(Fixtures.Sensor("s1", "Monstera"), fikus);
        config.Reports.Times = ["07:00", "19:00"];
        await db.Store.AddReadingAsync(Fixtures.Reading("s1", Fixtures.T0 - TimeSpan.FromMinutes(5), moisture: 20, battery: 15), Ct);
        AlertRecord[] alerts = [new(7, "s1", AlertKind.NeedsWater, "Wilgotność 20% (próg 25%).", Fixtures.T0 - TimeSpan.FromMinutes(1), null, null)];

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

        var status = await MonitorStatusBuilder.BuildAsync(config, db.Store, state, Host, Fixtures.T0, TimeZoneInfo.Utc, alerts, Ct);

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
        Assert.Equal(new DateTimeOffset(2026, 11, 13, 7, 0, 0, TimeSpan.Zero), status.Reports.NextAt);
        Assert.False(status.Mail.Configured);                                       // pocztę i raport na żądanie dokłada CloudPanel
        Assert.False(status.Reports.OnRequest.Available);
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

        var sensor = (await MonitorStatusBuilder.BuildAsync(Fixtures.Config(), db.Store, state, Host, Fixtures.T0, TimeZoneInfo.Utc, [], Ct)).Sensors[0];

        Assert.True(sensor.ReadRequested);
        Assert.Equal(Fixtures.T0, sensor.NextReadAt);                                   // zlecony – teraz
        Assert.Equal(Fixtures.T0 + TimeSpan.FromSeconds(40), sensor.ReadOnRequestFrom);  // minuta od ostatniej próby

        var later = (await MonitorStatusBuilder.BuildAsync(Fixtures.Config(), db.Store, MonitorRuntimeState.NotStarted, Host, Fixtures.T0, TimeZoneInfo.Utc, [], Ct)).Sensors[0];
        Assert.False(later.ReadRequested);
        Assert.Null(later.ReadOnRequestFrom);
    }

    [Fact]
    public async Task StatusNeverContainsSensorKeys()
    {
        using var db = new TempStore();
        var config = Fixtures.Config();
        var sensor = config.Sensors[0];

        var status = await MonitorStatusBuilder.BuildAsync(config, db.Store, MonitorRuntimeState.NotStarted, Host, Fixtures.T0, TimeZoneInfo.Utc, [], Ct);
        var json = JsonSerializer.Serialize(status, new JsonSerializerOptions(JsonSerializerDefaults.Web));

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
            var path = Path.Combine(directory.FullName, "glebosaurus.config.json");
            File.WriteAllText(path, """
                {
                  "cloud": { "url": "https://abcdefghijklmnopqrst.supabase.co", "publishableKey": "sb_publishable_x" },
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
