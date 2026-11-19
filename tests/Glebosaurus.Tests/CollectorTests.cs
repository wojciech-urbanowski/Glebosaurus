using System.IO.Compression;
using System.Text;
using Glebosaurus.Core.Cloud;
using Glebosaurus.Core.Monitoring;
using Microsoft.Extensions.Logging.Abstractions;

namespace Glebosaurus.Tests;

/// <summary>Zbieracz: odczyty tylko z dzierżawą, nasłuch, polecenia i dziennik dnia poprzedniego.</summary>
public sealed class CollectorTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    private readonly TempStore db = new();
    private readonly ManualTimeProvider clock = new(Fixtures.T0);
    private readonly LeaseLedger ledger;
    private readonly SensorLeasePolicy leases;
    private readonly SensorHearingTracker hearing;
    private readonly FakeSensorReader reader = new();
    private readonly FakeListener listener = new();
    private readonly FakeCloudApi api;
    private readonly FakeLogArchive logs = new();
    private readonly CollectorEngine collector;
    private int storedSignals;
    private int leaseChanges;

    public CollectorTests()
    {
        ledger = new(clock);
        leases = new(clock, ledger);
        hearing = new(clock);
        api = new(clock);
        var config = new CloudAgentConfig(Fixtures.Config(Fixtures.Sensor("s1"), Fixtures.Sensor("s2", uuid: "07aaad78f40b0a75")),
            TimeZoneInfo.Utc, 1, new("bestia5", false, null), null, []);
        collector = new(() => config, db.Store, reader, listener, leases, hearing, api, logs, clock, NullLogger.Instance)
        {
            ReadingsStored = () => storedSignals++,
            LeasesChanged = () => leaseChanges++
        };
    }

    public void Dispose() => db.Dispose();

    private void Grant(string sensor, double seconds = 1800) =>
        ledger.Apply(ledger.Now(), [new LeaseResult(CloudProtocol.SensorResource(sensor), true) { Epoch = 1, ValidSeconds = seconds }]);

    private static CloudCommand Command(long id, string kind, string? sensor = null) =>
        new(id, kind, sensor, null, Fixtures.T0, 600);

    [Fact]
    public async Task ReadsOnlySensorsWithLease_AndStoresForUpload()
    {
        Grant("s1");

        await collector.CycleAsync(Ct);

        Assert.Equal([["s1"]], reader.Calls);
        Assert.Equal(1, await db.Store.CountUnsentAsync(Ct));
        Assert.Equal(1, storedSignals);
        var observed = hearing.Observations()["s1"];
        Assert.Equal((0, Fixtures.T0), (observed.ConsecutiveFailures, observed.LastSuccessAt!.Value));
    }

    [Fact]
    public async Task State_ShowsAttemptsResultsAndSignal_ForThePanel()
    {
        Grant("s1");
        Grant("s2");
        reader.Script = sensor => sensor.Id == "s1"
            ? Fixtures.Ok(sensor, 41) with { SignalDbm = -77 }
            : new SensorReadOutcome(sensor, null, SensorReadFailure.NotFound);
        Assert.Null(collector.State.StartedAt);

        await collector.CycleAsync(Ct);

        var state = collector.State;
        Assert.Equal((Fixtures.T0, Fixtures.T0), (state.StartedAt!.Value, state.LastCycleAt!.Value));
        Assert.Empty(state.ReadingNow);
        var s1 = state.Sensors["s1"];
        Assert.Equal((SensorReadFailure.None, 0, -77), (s1.LastResult!.Value, s1.Failures, s1.LastSignalDbm!.Value));
        var s2 = state.Sensors["s2"];
        Assert.Equal((SensorReadFailure.NotFound, 1, Fixtures.T0 + Minute), (s2.LastResult!.Value, s2.Failures, s2.RetryAt!.Value));
    }

    [Fact]
    public async Task WithoutLease_NothingIsRead()
    {
        await collector.CycleAsync(Ct);
        Assert.Empty(reader.Calls);
    }

    [Fact]
    public async Task ReadInterval_IsRespected()
    {
        Grant("s1");
        await collector.CycleAsync(Ct);
        clock.Advance(10 * Minute);
        Grant("s1");
        await collector.CycleAsync(Ct);
        clock.Advance(5 * Minute);
        Grant("s1");
        await collector.CycleAsync(Ct);

        Assert.Equal(2, reader.Calls.Count);
    }

    [Fact]
    public async Task LeaseMustCoverListeningAndQueue()
    {
        Grant("s1", seconds: 179);   // 2 min odczytu + 60 s nasłuchu = 180 s
        await collector.CycleAsync(Ct);
        Assert.Empty(reader.Calls);

        Grant("s1", seconds: 190);
        Grant("s2", seconds: 190);   // drugi w kolejce potrzebuje jeszcze 20 s
        await collector.CycleAsync(Ct);
        Assert.Equal([["s1"]], reader.Calls);
    }

    [Fact]
    public async Task ListensToAllSensorsEveryThreeMinutes()
    {
        listener.Script = sensors => [new("s2", -71, 4)];

        await collector.CycleAsync(Ct);
        clock.Advance(2 * Minute);
        await collector.CycleAsync(Ct);
        clock.Advance(Minute);
        listener.Script = sensors => [new("s2", -75, 3)];
        await collector.CycleAsync(Ct);

        Assert.Equal([["s1", "s2"], ["s1", "s2"]], listener.Calls);
        var s2 = hearing.Observations()["s2"];
        Assert.Equal(-73, s2.Rssi);   // średnia wykładnicza (-71, -75)
        Assert.Equal(Fixtures.T0 + 3 * Minute, s2.LastHeardAt);
        Assert.False(hearing.Observations().ContainsKey("s1"));
    }

    [Fact]
    public async Task Failures_BackOff_AndAfterThreeTheSensorIsHandedOver()
    {
        Grant("s1");
        reader.Script = sensor => new(sensor, null, SensorReadFailure.NotFound);

        await collector.CycleAsync(Ct);
        await collector.CycleAsync(Ct);                 // wstrzymanie 1 min
        clock.Advance(Minute);
        await collector.CycleAsync(Ct);
        clock.Advance(2 * Minute);
        await collector.CycleAsync(Ct);

        Assert.Equal(3, reader.Calls.Count);
        Assert.Equal(3, hearing.Observations()["s1"].ConsecutiveFailures);
        var plan = leases.Plan(["s1", "s2"], hearing.Observations().ToDictionary(o => o.Key, o => o.Value.ToHearing()));
        Assert.Equal("sensor:s1", Assert.Single(plan.Releases).Resource);
    }

    [Fact]
    public async Task SessionCutInHistory_StoresHistoryWithSensorTimes_KeepsLease_AndRetriesAfterAMinute()
    {
        // Alpinarium 2026-11-18: przerwana sesja oddała najstarszą część historii; agent wpisał jej ostatni rekord jako „teraz”.
        Grant("s1");
        var threeDaysAgo = Fixtures.T0 - TimeSpan.FromDays(3);
        reader.Script = sensor => new(sensor, null, SensorReadFailure.Incomplete, "sesja urwana w trakcie historii czujnika")
            { Earlier = [Fixtures.Soil(threeDaysAgo, 94), Fixtures.Soil(threeDaysAgo + TimeSpan.FromHours(1), 93)] };

        await collector.CycleAsync(Ct);
        await collector.CycleAsync(Ct);                 // przed upływem minuty – bez ponownego połączenia

        var stored = await db.Store.GetRecentAsync("s1", 10, Ct);
        Assert.Equal([threeDaysAgo + TimeSpan.FromHours(1), threeDaysAgo], stored.Select(r => r.At));
        Assert.Equal(2, await db.Store.CountUnsentAsync(Ct));
        Assert.Equal(1, storedSignals);
        Assert.Equal(0, hearing.Observations()["s1"].ConsecutiveFailures); // połączenie działało – czujnik zostaje u tego agenta
        Assert.Single(reader.Calls);

        reader.Script = sensor => Fixtures.Ok(sensor, 96);
        clock.Advance(Minute);
        await collector.CycleAsync(Ct);
        Assert.Equal(2, reader.Calls.Count);
        Assert.Equal(96, (await db.Store.GetLatestAsync("s1", Ct))!.MoisturePercent);
    }

    [Fact]
    public async Task ReadNow_SkipsInterval_AndReportsResult()
    {
        Grant("s1");
        await collector.CycleAsync(Ct);
        clock.Advance(Minute);

        Assert.True(await collector.HandleCommandAsync(Command(7, "read_now", "s1"), Ct));
        await collector.CycleAsync(Ct);

        Assert.Equal(2, reader.Calls.Count);
        var (id, result) = Assert.Single(api.Done);
        Assert.Equal(7, id);
        Assert.True((bool)result!["ok"]!);
        Assert.Equal(50, (int)result["moisture"]!);
    }

    [Fact]
    public async Task ReadNow_WithoutLeaseOrForUnknownSensor_ReportsFailure()
    {
        await collector.HandleCommandAsync(Command(1, "read_now", "s1"), Ct);
        await collector.HandleCommandAsync(Command(2, "read_now", "nieznany"), Ct);
        await collector.CycleAsync(Ct);

        Assert.Empty(reader.Calls);
        Assert.Equal([1L, 2L], api.Done.Select(d => d.Id).Order());
        Assert.All(api.Done, d => Assert.False((bool)d.Result!["ok"]!));
    }

    [Fact]
    public async Task Release_HandsSensorOverImmediately()
    {
        Grant("s1");

        Assert.True(await collector.HandleCommandAsync(Command(3, "release", "s1"), Ct));

        Assert.False(leases.CanRead("s1"));
        Assert.Equal(1, leaseChanges);
        Assert.Equal(3, Assert.Single(api.Done).Id);
        Assert.Equal("sensor:s1", Assert.Single(leases.Plan(["s1"], new Dictionary<string, SensorHearing>()).Releases).Resource);
        await collector.CycleAsync(Ct);
        Assert.Empty(reader.Calls);
    }

    [Fact]
    public async Task SendLog_UploadsGzippedLog()
    {
        var today = DateOnly.FromDateTime(Fixtures.T0.UtcDateTime);
        logs.Days[today] = "06:00:00 info: zażółć gęślą jaźń\n";

        Assert.True(await collector.HandleCommandAsync(Command(4, "send_log"), Ct));

        var (day, gzip) = Assert.Single(api.Logs);
        Assert.Equal(today, day);
        using var stream = new GZipStream(new MemoryStream(gzip), CompressionMode.Decompress);
        Assert.Equal(logs.Days[today], new StreamReader(stream, Encoding.UTF8).ReadToEnd());
        Assert.True((bool)Assert.Single(api.Done).Result!["ok"]!);
    }

    [Fact]
    public async Task YesterdaysLog_IsUploadedOncePerDay_AndRetriedAfterFailure()
    {
        var today = DateOnly.FromDateTime(Fixtures.T0.UtcDateTime);
        logs.Days[today.AddDays(-1)] = "wczoraj\n";
        api.LogFailures.Enqueue(new(CloudErrorKind.Unavailable, "brak sieci"));

        await collector.CycleAsync(Ct);                 // porażka
        clock.Advance(Minute);
        await collector.CycleAsync(Ct);                 // jeszcze czeka
        clock.Advance(4 * Minute);
        await collector.CycleAsync(Ct);                 // ponowienie po 5 min
        clock.Advance(Minute);
        await collector.CycleAsync(Ct);                 // już wysłany

        Assert.Equal(today.AddDays(-1), Assert.Single(api.Logs).Day);

        clock.Advance(TimeSpan.FromDays(1));            // następny dzień: dziennika nie ma – nic nie wysyła i nie pyta co cykl
        await collector.CycleAsync(Ct);
        await collector.CycleAsync(Ct);
        Assert.Single(api.Logs);
        Assert.Equal(1, logs.Reads.Count(d => d == today));
    }

    [Fact]
    public async Task OtherCommands_AreNotForCollector()
    {
        Assert.False(await collector.HandleCommandAsync(Command(5, "report_now"), Ct));
        Assert.False(await collector.HandleCommandAsync(Command(6, "pair", "s1"), Ct));
        Assert.Empty(api.Done);
    }

    [Fact]
    public async Task ResultThatCouldNotBeReported_IsRetried()
    {
        Grant("s1");
        api.DoneFailures.Enqueue(new(CloudErrorKind.Unavailable, "brak sieci"));

        await collector.HandleCommandAsync(Command(8, "release", "s1"), Ct);
        Assert.Empty(api.Done);
        await collector.CycleAsync(Ct);

        Assert.Equal(8, Assert.Single(api.Done).Id);
    }
}
