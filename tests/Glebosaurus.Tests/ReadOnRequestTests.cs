using Glebosaurus.Core.Monitoring;

namespace Glebosaurus.Tests;

/// <summary>Odczyt czujników na żądanie (panel WWW): kolejka w silniku, odstęp chroniący baterię, budzenie pętli.</summary>
public class ReadOnRequestTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private sealed class Harness : IDisposable
    {
        private readonly TempStore db = new();
        public FakeSensorReader Reader { get; } = new();
        public ManualTimeProvider Clock { get; } = new(Fixtures.T0);
        public MonitorEngine Engine { get; }

        public Harness(TimeSpan? tick = null)
        {
            var config = Fixtures.Config(Fixtures.Sensor("s1", "Monstera"), Fixtures.Sensor("s2", "Fikus", "17aaad78f40b0a74"));
            config.Sensors[1].Tuya.Mac = "DC:23:51:CC:03:CA";
            Engine = new(config, db.Store, Reader, new FakeMail(), Clock, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, TimeZoneInfo.Utc)
            {
                TickInterval = tick ?? TimeSpan.FromSeconds(30)
            };
        }

        public void Dispose() => db.Dispose();
    }

    [Fact]
    public async Task RequestedSensorIsReadInTheNextCycleEvenBeforeItsInterval()
    {
        using var h = new Harness();
        await h.Engine.CycleAsync(Ct);                                    // odczyt obu o T0
        h.Clock.Advance(TimeSpan.FromMinutes(5));                         // do zwykłego odczytu jeszcze 10 min

        var result = h.Engine.RequestRead(["s1"]);

        Assert.Equal([new ReadRequestItem("s1", "Monstera", ReadRequestStatus.Queued)], result.Items);
        Assert.Equal(["s1"], h.Engine.State.RequestedReads);
        await h.Engine.CycleAsync(Ct);
        Assert.Equal(["s1"], h.Reader.Calls[^1]);                         // tylko zlecony, Fikus czeka na swoją kolej
        Assert.Empty(h.Engine.State.RequestedReads);
    }

    [Fact]
    public async Task RequestRightAfterAnAttemptIsTooSoon()
    {
        using var h = new Harness();
        await h.Engine.CycleAsync(Ct);
        h.Clock.Advance(TimeSpan.FromSeconds(30));

        var item = Assert.Single(h.Engine.RequestRead(["s1"]).Items);

        Assert.Equal(ReadRequestStatus.TooSoon, item.Status);
        Assert.Equal(Fixtures.T0 + MonitorEngine.ForcedReadCooldown, item.AvailableAt);
        Assert.Empty(h.Engine.State.RequestedReads);
    }

    [Fact]
    public void AllSensorsDuplicatesAndUnknownIdsAreReportedPerSensor()
    {
        using var h = new Harness();

        var all = h.Engine.RequestRead(null);
        Assert.Equal([ReadRequestStatus.Queued, ReadRequestStatus.Queued], all.Items.Select(i => i.Status));

        var again = h.Engine.RequestRead(["s2", "nie-ma", "s2"]);
        Assert.Equal(
            [new ReadRequestItem("s2", "Fikus", ReadRequestStatus.AlreadyQueued), new ReadRequestItem("nie-ma", null, ReadRequestStatus.Unknown)],
            again.Items);
        Assert.Equal(["s1", "s2"], h.Engine.State.RequestedReads);
    }

    [Fact]
    public async Task RequestOverridesTheRetryDelayAfterFailures()
    {
        using var h = new Harness();
        h.Reader.Script = sensor => new(sensor, null, SensorReadFailure.Unbound);    // ponowienie dopiero za 30 min
        await h.Engine.CycleAsync(Ct);
        h.Clock.Advance(TimeSpan.FromMinutes(2));
        await h.Engine.CycleAsync(Ct);
        Assert.Single(h.Reader.Calls);

        h.Engine.RequestRead(["s1"]);
        await h.Engine.CycleAsync(Ct);

        Assert.Equal(2, h.Reader.Calls.Count);
        Assert.Equal(["s1"], h.Reader.Calls[^1]);
    }

    [Fact]
    public async Task SensorBeingReadReportsInProgress()
    {
        using var h = new Harness();
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        ReadRequestResult? during = null;
        h.Reader.Script = sensor =>
        {
            during ??= h.Engine.RequestRead(["s1"]);
            return Fixtures.Ok(sensor, 50);
        };

        await h.Engine.CycleAsync(Ct);

        Assert.Equal(ReadRequestStatus.InProgress, Assert.Single(during!.Items).Status);
        Assert.Empty(h.Engine.State.RequestedReads);
    }

    [Fact]
    public async Task RequestWakesTheLoopWithoutWaitingForTheNextTick()
    {
        using var h = new Harness(tick: TimeSpan.FromHours(1));
        var firstCycle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestedRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        h.Reader.Script = sensor =>
        {
            if (Interlocked.Increment(ref reads) == 2) firstCycle.TrySetResult();      // oba czujniki w pierwszym cyklu
            if (reads == 3) requestedRead.TrySetResult();
            return Fixtures.Ok(sensor, 50);
        };
        using var stop = new CancellationTokenSource();
        var run = h.Engine.RunAsync(stop.Token);

        await firstCycle.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        h.Clock.Advance(TimeSpan.FromMinutes(2));
        await WaitUntilIdleAsync(h.Engine);
        h.Engine.RequestRead(["s2"]);

        await requestedRead.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(["s2"], h.Reader.Calls[^1]);
    }

    [Fact]
    public async Task LoopKeepsTickingAfterAWakeUp()
    {
        // Prawdziwy zegar dla odstępu pętli (100 ms), ręczny dla „teraz” (odstęp baterii). Liczymy cykle po zapytaniach o alerty.
        using var db = new TempStore();
        var store = new CountingStore(db.Store);
        var clock = new ManualTimeProvider(Fixtures.T0);
        var engine = new MonitorEngine(Fixtures.Config(), store, new FakeSensorReader(), new FakeMail(), clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, TimeZoneInfo.Utc) { TickInterval = TimeSpan.FromMilliseconds(100) };
        using var stop = new CancellationTokenSource();
        var run = engine.RunAsync(stop.Token);

        await WaitUntilIdleAsync(engine);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(ReadRequestStatus.Queued, Assert.Single(engine.RequestRead(["s1"]).Items).Status);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        var afterWake = store.Cycles;
        await Task.Delay(1000, TestContext.Current.CancellationToken);

        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(store.Cycles - afterWake >= 5, $"po przebudzeniu pętla wykonała tylko {store.Cycles - afterWake} cykli w 1 s");
    }

    /// <summary>Magazyn, który liczy cykle silnika (każdy cykl raz pyta o otwarte alerty przy ich przeliczaniu).</summary>
    private sealed class CountingStore(IMonitorStore inner) : IMonitorStore
    {
        private int cycles;
        public int Cycles => Volatile.Read(ref cycles);

        public Task InitializeAsync(CancellationToken ct) => inner.InitializeAsync(ct);
        public Task AddReadingAsync(Glebosaurus.Core.Domain.StoredReading reading, CancellationToken ct) => inner.AddReadingAsync(reading, ct);
        public Task<Glebosaurus.Core.Domain.StoredReading?> GetLatestAsync(string sensorId, CancellationToken ct) => inner.GetLatestAsync(sensorId, ct);
        public Task<IReadOnlyList<Glebosaurus.Core.Domain.StoredReading>> GetRecentAsync(string sensorId, int count, CancellationToken ct) => inner.GetRecentAsync(sensorId, count, ct);
        public Task<IReadOnlyList<Glebosaurus.Core.Domain.StoredReading>> GetRangeAsync(string sensorId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
            inner.GetRangeAsync(sensorId, from, to, ct);
        public Task<(DateTimeOffset First, DateTimeOffset Last)?> GetReadingBoundsAsync(string sensorId, CancellationToken ct) => inner.GetReadingBoundsAsync(sensorId, ct);
        public Task<IReadOnlyList<Glebosaurus.Core.Domain.AlertRecord>> GetOpenAlertsAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref cycles);
            return inner.GetOpenAlertsAsync(ct);
        }
        public Task<Glebosaurus.Core.Domain.AlertRecord> OpenAlertAsync(string sensorId, Glebosaurus.Core.Domain.AlertKind kind, string detail, DateTimeOffset at, CancellationToken ct) =>
            inner.OpenAlertAsync(sensorId, kind, detail, at, ct);
        public Task ResolveAlertAsync(long alertId, DateTimeOffset at, CancellationToken ct) => inner.ResolveAlertAsync(alertId, at, ct);
        public Task MarkNotifiedAsync(IReadOnlyCollection<long> alertIds, DateTimeOffset at, CancellationToken ct) => inner.MarkNotifiedAsync(alertIds, at, ct);
        public Task<bool> HasReportRunAsync(string key, CancellationToken ct) => inner.HasReportRunAsync(key, ct);
        public Task RecordReportRunAsync(string key, DateTimeOffset at, CancellationToken ct) => inner.RecordReportRunAsync(key, at, ct);
        public Task PurgeAsync(DateTimeOffset olderThan, CancellationToken ct) => inner.PurgeAsync(olderThan, ct);
    }

    /// <summary>Czeka, aż pierwszy cykl się skończy (stan go odnotuje), żeby zlecenie trafiło na oczekiwanie pętli.</summary>
    private static async Task WaitUntilIdleAsync(MonitorEngine engine)
    {
        for (var i = 0; i < 200 && engine.State.LastCycleAt is null; i++)
            await Task.Delay(25, TestContext.Current.CancellationToken);
        Assert.NotNull(engine.State.LastCycleAt);
    }
}
