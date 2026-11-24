using System.Runtime.InteropServices;
using Glebosaurus.Core.Config;
using Glebosaurus.Core.Monitoring;
using Microsoft.Extensions.Logging;

namespace Glebosaurus.Tests;

/// <summary>Nasłuch na przemian z odczytem: czujnik poza zasięgiem nie opóźnia tych w zasięgu, okno liczy tylko nasłuch.</summary>
public class ListenThenReadSensorReaderTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ReadTime = TimeSpan.FromSeconds(14);

    private static readonly SensorConfig A = Fixtures.Sensor("a", "Cyprysek", "0000000000000001");
    private static readonly SensorConfig B = Fixtures.Sensor("b", "Cisy", "0000000000000002");
    private static readonly SensorConfig C = Fixtures.Sensor("c", "Zioła", "0000000000000003");

    /// <summary>
    /// Atrapa radia: czujnik „pojawia się w reklamach” po określonym łącznym czasie nasłuchu (brak wpisu = poza zasięgiem).
    /// Nasłuch przesuwa zegar o czas słuchania, odczyt o <see cref="ReadTime"/>. Każde wywołanie trafia do wspólnego dziennika.
    /// </summary>
    private sealed class FakeRadio(ManualTimeProvider clock, List<string> events, Dictionary<string, TimeSpan> appearsAfter) : ISensorRadio<string>
    {
        private TimeSpan listened;

        public List<TimeSpan> Limits { get; } = [];

        /// <summary>Wyjątek dla wybranych wywołań nasłuchu (numer od 0); null = nasłuch normalny.</summary>
        public Func<int, Exception?> ListenFailure { get; set; } = _ => null;

        /// <summary>Ile nasłuchu mija, zanim radio zgłosi błąd.</summary>
        public TimeSpan FailAfter { get; set; }

        public Task<IReadOnlyList<(SensorConfig Sensor, string Found)>> ListenAsync(
            IReadOnlyList<SensorConfig> wanted, TimeSpan limit, CancellationToken cancellationToken)
        {
            Limits.Add(limit);
            if (ListenFailure(Limits.Count - 1) is { } failure)
            {
                events.Add($"nasłuch {string.Join(",", wanted.Select(s => s.Id))}: błąd");
                clock.Advance(FailAfter);
                listened += FailAfter;
                throw failure;
            }
            events.Add($"nasłuch {string.Join(",", wanted.Select(s => s.Id))}");
            var visible = wanted.Where(s => appearsAfter.TryGetValue(s.Id, out var at) && at <= listened + limit).ToList();
            var until = visible.Count == 0 ? listened + limit : visible.Min(s => appearsAfter[s.Id]);
            if (until < listened) until = listened;
            clock.Advance(until - listened);
            listened = until;
            IReadOnlyList<(SensorConfig, string)> found = wanted
                .Where(s => appearsAfter.TryGetValue(s.Id, out var at) && at <= listened)
                .Select(s => (s, $"reklama {s.Id}")).ToList();
            return Task.FromResult(found);
        }

        public Task<SensorReadOutcome> ReadAsync(SensorConfig sensor, string found, DateTimeOffset? historySince, CancellationToken cancellationToken)
        {
            events.Add($"odczyt {sensor.Id}");
            clock.Advance(ReadTime);
            return Task.FromResult(Fixtures.Ok(sensor, 50));
        }
    }

    private static Task<(List<string> Events, FakeRadio Radio, List<SensorReadOutcome> Outcomes)> RunAsync(
        Dictionary<string, TimeSpan> appearsAfter, params SensorConfig[] sensors) => RunAsync(appearsAfter, null, null, sensors);

    private static async Task<(List<string> Events, FakeRadio Radio, List<SensorReadOutcome> Outcomes)> RunAsync(
        Dictionary<string, TimeSpan> appearsAfter, Action<FakeRadio>? setup, ILogger? logger, params SensorConfig[] sensors)
    {
        var clock = new ManualTimeProvider(Fixtures.T0);
        var events = new List<string>();
        var radio = new FakeRadio(clock, events, appearsAfter);
        setup?.Invoke(radio);
        var outcomes = new List<SensorReadOutcome>();
        var reader = new ListenThenReadSensorReader<string>(radio, clock, logger, listenRetryPause: TimeSpan.Zero);
        await foreach (var outcome in reader.ReadAsync(sensors, Window, new Dictionary<string, DateTimeOffset>(), TestContext.Current.CancellationToken))
        {
            events.Add($"wynik {outcome.Sensor.Id}: {outcome.Failure}");
            outcomes.Add(outcome);
        }
        return (events, radio, outcomes);
    }

    [Fact]
    public async Task SensorInRangeIsReadRightAwayAndTheMissingOneDoesNotDelayIt()
    {
        var (events, radio, _) = await RunAsync(new() { ["a"] = TimeSpan.FromSeconds(2) }, A, B);

        Assert.Equal(["nasłuch a,b", "odczyt a", "wynik a: None", "nasłuch b", "wynik b: NotFound"], events);
        // Okno liczy tylko nasłuch: 2 s w pierwszej rundzie, więc brakujący czujnik dostaje jeszcze 58 s (odczyt się nie wlicza).
        Assert.Equal([Window, Window - TimeSpan.FromSeconds(2)], radio.Limits);
    }

    [Fact]
    public async Task SensorsFoundTogetherAreReadInConfigOrderWithoutAnotherListen()
    {
        var (events, _, _) = await RunAsync(new() { ["b"] = TimeSpan.FromSeconds(3), ["a"] = TimeSpan.FromSeconds(3) }, A, B);

        Assert.Equal(["nasłuch a,b", "odczyt a", "wynik a: None", "odczyt b", "wynik b: None"], events);
    }

    [Fact]
    public async Task LaterSensorIsFoundInTheNextRoundAfterTheFirstIsRead()
    {
        var (events, radio, outcomes) = await RunAsync(
            new() { ["a"] = TimeSpan.FromSeconds(1), ["c"] = TimeSpan.FromSeconds(25) }, A, B, C);

        Assert.Equal(
            ["nasłuch a,b,c", "odczyt a", "wynik a: None", "nasłuch b,c", "odczyt c", "wynik c: None", "nasłuch b", "wynik b: NotFound"],
            events);
        Assert.Equal([Window, Window - TimeSpan.FromSeconds(1), Window - TimeSpan.FromSeconds(25)], radio.Limits);
        Assert.Equal(["a", "c", "b"], outcomes.Select(o => o.Sensor.Id));
    }

    [Fact]
    public async Task NothingInRangeListensOnceForTheWholeWindow()
    {
        var (events, radio, _) = await RunAsync([], A, B);

        Assert.Equal(["nasłuch a,b", "wynik a: NotFound", "wynik b: NotFound"], events);
        Assert.Equal([Window], radio.Limits);
    }

    [Fact]
    public async Task SensorAppearingAfterTheWindowIsNotFound()
    {
        var (events, _, _) = await RunAsync(new() { ["a"] = TimeSpan.FromSeconds(5), ["b"] = TimeSpan.FromSeconds(61) }, A, B);

        Assert.Equal(["nasłuch a,b", "odczyt a", "wynik a: None", "nasłuch b", "wynik b: NotFound"], events);
    }

    // ---- błędy nasłuchu (Shiny na Windows potrafi przerwać skan COMException, 2026-11-14) ---------------------------

    private static COMException ScanFailure() => new("Nieokreślony błąd. (0x80004005)", unchecked((int)0x80004005));

    [Fact]
    public async Task FailedListenIsResumedWithinTheSameWindow()
    {
        var log = new CapturingLogger();
        var (events, radio, _) = await RunAsync(new() { ["a"] = TimeSpan.FromSeconds(10) }, radio =>
        {
            radio.ListenFailure = call => call == 0 ? ScanFailure() : null;
            radio.FailAfter = TimeSpan.FromSeconds(5);
        }, log, A, B);

        Assert.Equal(["nasłuch a,b: błąd", "nasłuch a,b", "odczyt a", "wynik a: None", "nasłuch b", "wynik b: NotFound"], events);
        // Nieudany nasłuch też zużywa okno: 5 s przed błędem, potem jeszcze 5 s do znalezienia „a”.
        Assert.Equal([Window, Window - TimeSpan.FromSeconds(5), Window - TimeSpan.FromSeconds(10)], radio.Limits);
        var warning = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("wznawiam", warning.Message);
    }

    [Fact]
    public async Task ListenThatKeepsFailingEndsWithAnErrorOnlyForTheSensorsStillWaiting()
    {
        var log = new CapturingLogger();
        var (events, _, outcomes) = await RunAsync(new() { ["a"] = TimeSpan.FromSeconds(1) },
            radio => radio.ListenFailure = call => call >= 1 ? ScanFailure() : null, log, A, B, C);

        Assert.Equal(
            ["nasłuch a,b,c", "odczyt a", "wynik a: None", "nasłuch b,c: błąd", "nasłuch b,c: błąd", "nasłuch b,c: błąd", "wynik b: Error", "wynik c: Error"],
            events);
        Assert.All(outcomes.Skip(1), o => Assert.Equal("nasłuch BLE przerwany błędem: Nieokreślony błąd. (0x80004005)", o.Error));
        Assert.Equal(ListenThenReadSensorReader<string>.MaxListenFailures, log.Entries.Count);
        Assert.Contains("kolejna próba w następnym cyklu", log.Entries[^1].Message);
    }

    [Fact]
    public async Task SuccessfulListenResetsTheFailureCount()
    {
        // Trzy błędy, ale nigdy trzy z rzędu – odczyt idzie do końca, a brakujący czujnik to zwykłe „nie znaleziono”.
        var (events, _, _) = await RunAsync(new() { ["a"] = TimeSpan.FromSeconds(1), ["c"] = TimeSpan.FromSeconds(2) },
            radio => radio.ListenFailure = call => call is 0 or 1 or 3 ? ScanFailure() : null, null, A, B, C);

        Assert.Equal(
            ["nasłuch a,b,c: błąd", "nasłuch a,b,c: błąd", "nasłuch a,b,c", "odczyt a", "wynik a: None", "nasłuch b,c: błąd", "nasłuch b,c",
             "odczyt c", "wynik c: None", "nasłuch b", "wynik b: NotFound"],
            events);
    }

    [Fact]
    public async Task CancellationIsNotTreatedAsAListenFailure()
    {
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();
        var clock = new ManualTimeProvider(Fixtures.T0);
        var radio = new FakeRadio(clock, [], []) { ListenFailure = _ => new OperationCanceledException(stop.Token) };
        var reader = new ListenThenReadSensorReader<string>(radio, clock, listenRetryPause: TimeSpan.Zero);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in reader.ReadAsync([A], Window, new Dictionary<string, DateTimeOffset>(), stop.Token)) { }
        });
        Assert.Single(radio.Limits);
    }
}
