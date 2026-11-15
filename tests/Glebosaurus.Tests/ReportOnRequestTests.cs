using Glebosaurus.Core.Mail;
using Glebosaurus.Core.Monitoring;
using Microsoft.Extensions.Logging.Abstractions;

namespace Glebosaurus.Tests;

/// <summary>Raport mailowy na żądanie (panel WWW): kolejka w silniku, odstęp chroniący skrzynki, niezależność od harmonogramu.</summary>
public class ReportOnRequestTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private sealed class Harness : IDisposable
    {
        private readonly TempStore db = new();
        public FakeSensorReader Reader { get; } = new();
        public FakeMail Mail { get; } = new();
        public ManualTimeProvider Clock { get; } = new(Fixtures.T0);
        public MonitorEngine Engine { get; }

        public Harness(Action<Glebosaurus.Core.Config.AgentConfig>? configure = null, TimeSpan? tick = null)
        {
            var config = Fixtures.Config();
            configure?.Invoke(config);
            Engine = new(config, db.Store, Reader, config.Mail is null ? null : Mail, Clock, NullLogger.Instance, TimeZoneInfo.Utc)
            {
                TickInterval = tick ?? TimeSpan.FromSeconds(30)
            };
        }

        public void Dispose() => db.Dispose();
    }

    [Fact]
    public async Task RequestedReportGoesToReportRecipientsAndTheScheduledReportStillGoesOut()
    {
        using var h = new Harness(config =>
        {
            config.Mail!.ReportRecipients = ["raporty@example.com"];
            config.Reports.Times = ["07:00"];
        });
        await h.Engine.CycleAsync(Ct);                                    // 06:00 – odczyt, raportu jeszcze nie ma
        h.Clock.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(new ReportRequestResult(ReportRequestStatus.Queued), h.Engine.RequestReport());
        Assert.Equal(h.Clock.GetUtcNow(), h.Engine.State.ReportOnRequest.RequestedAt);
        await h.Engine.CycleAsync(Ct);

        var report = Assert.Single(h.Mail.Sent);
        Assert.Equal(["raporty@example.com"], report.To);
        Assert.Equal("Poranny raport – wszystko w porządku", report.Subject);
        Assert.Equal(new ReportOnRequestState(null, h.Clock.GetUtcNow(), null), h.Engine.State.ReportOnRequest);
        Assert.Equal(h.Clock.GetUtcNow(), h.Engine.State.Mail.LastSentAt);

        h.Clock.Advance(TimeSpan.FromMinutes(56));                        // 07:01 – zaplanowany raport idzie jak zwykle
        await h.Engine.CycleAsync(Ct);
        Assert.Equal(2, h.Mail.Sent.Count);
        Assert.Equal(h.Mail.Sent[0].Subject, h.Mail.Sent[1].Subject);
    }

    [Fact]
    public async Task ReportFromTheMonitorHasAChartOfTheLastDays()
    {
        using var h = new Harness();
        for (var i = 0; i < 24; i++)                                      // 6 godzin odczytów co 15 min, wilgotność spada
        {
            var moisture = 60 - i;
            h.Reader.Script = sensor => new SensorReadOutcome(sensor, Fixtures.Soil(h.Clock.GetUtcNow(), moisture), SensorReadFailure.None);
            await h.Engine.CycleAsync(Ct);
            h.Clock.Advance(TimeSpan.FromMinutes(15));
        }

        h.Engine.RequestReport();
        await h.Engine.CycleAsync(Ct);

        var report = Assert.Single(h.Mail.Sent);
        Assert.Contains("Wykresy: ostatnie 3 doby, przedziały po 2 godz.", report.HtmlBody);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(report.HtmlBody, "cellspacing=\"1\""));
        Assert.Contains("  3 doby: ", report.TextBody);
    }

    [Fact]
    public async Task SnapshotsCarryOnlyTheLastDaysOfHistory()
    {
        using var db = new TempStore();
        foreach (var hours in new[] { 100, 76, 74, 1 })
            await db.Store.AddReadingAsync(Fixtures.Reading("s1", Fixtures.T0.AddHours(-hours)), Ct);

        var snapshot = Assert.Single(await SnapshotBuilder.BuildAsync(Fixtures.Config(), db.Store, Fixtures.T0, Ct));

        Assert.Equal([Fixtures.T0.AddHours(-74), Fixtures.T0.AddHours(-1)], snapshot.History.Select(r => r.At).Order());
        Assert.Equal(Fixtures.T0.AddHours(-1), snapshot.Latest?.At);
    }

    [Fact]
    public async Task ReportShowsTheReadingTakenInTheSameCycle()
    {
        using var h = new Harness();
        await h.Engine.CycleAsync(Ct);                                    // wilgotność 50%
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        h.Reader.Script = sensor => Fixtures.Ok(sensor, 42);

        h.Engine.RequestRead(["s1"]);
        h.Engine.RequestReport();
        await h.Engine.CycleAsync(Ct);

        Assert.Contains("wilgotność 42%", Assert.Single(h.Mail.Sent).TextBody);
    }

    [Fact]
    public async Task RepeatedRequestsAreQueuedOnceAndThenLimitedByTheCooldown()
    {
        using var h = new Harness();
        Assert.Equal(ReportRequestStatus.Queued, h.Engine.RequestReport().Status);
        Assert.Equal(ReportRequestStatus.AlreadyQueued, h.Engine.RequestReport().Status);
        await h.Engine.CycleAsync(Ct);
        Assert.Single(h.Mail.Sent);

        h.Clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(new ReportRequestResult(ReportRequestStatus.TooSoon, Fixtures.T0 + MonitorEngine.ReportOnRequestCooldown), h.Engine.RequestReport());
        await h.Engine.CycleAsync(Ct);
        Assert.Single(h.Mail.Sent);

        h.Clock.Advance(TimeSpan.FromSeconds(40));
        Assert.Equal(ReportRequestStatus.Queued, h.Engine.RequestReport().Status);
        await h.Engine.CycleAsync(Ct);
        Assert.Equal(2, h.Mail.Sent.Count);
    }

    [Fact]
    public void WithoutMailTheRequestIsRefused()
    {
        using var h = new Harness(config => config.Mail = null);

        Assert.Equal(ReportRequestStatus.MailNotConfigured, h.Engine.RequestReport().Status);
        Assert.Same(ReportOnRequestState.None, h.Engine.State.ReportOnRequest);
    }

    [Fact]
    public async Task FailedSendIsVisibleAndDoesNotPauseOtherMail()
    {
        using var h = new Harness();
        h.Mail.Failure = new MailTransportException("brak sieci", new IOException());

        h.Engine.RequestReport();
        await h.Engine.CycleAsync(Ct);

        var failed = h.Engine.State.ReportOnRequest;
        Assert.Null(failed.RequestedAt);
        Assert.Equal(Fixtures.T0, failed.LastAttemptAt);
        Assert.Contains("brak sieci", failed.LastError);
        Assert.Contains("brak sieci", h.Engine.State.Mail.LastError);
        Assert.Null(h.Engine.State.Mail.RetryAt);                         // alerty i raporty z harmonogramu nie czekają

        h.Mail.Failure = null;
        h.Clock.Advance(MonitorEngine.ReportOnRequestCooldown);
        Assert.Equal(ReportRequestStatus.Queued, h.Engine.RequestReport().Status);
        await h.Engine.CycleAsync(Ct);
        Assert.Single(h.Mail.Sent);
        Assert.Null(h.Engine.State.ReportOnRequest.LastError);
    }

    [Fact]
    public async Task ReportGoesOutEvenWhileMailIsPausedAfterAnError()
    {
        using var h = new Harness();
        h.Reader.Script = sensor => Fixtures.Ok(sensor, 18);
        h.Mail.Failure = new MailTransportException("brak sieci", new IOException());
        await h.Engine.CycleAsync(Ct);
        h.Clock.Advance(TimeSpan.FromMinutes(15));
        await h.Engine.CycleAsync(Ct);                                    // alert „czas podlać” – wysyłka nieudana, wstrzymanie 5 min
        var pausedUntil = h.Engine.State.Mail.RetryAt;
        Assert.NotNull(pausedUntil);

        h.Mail.Failure = null;
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        h.Engine.RequestReport();
        await h.Engine.CycleAsync(Ct);

        Assert.Contains("raport", Assert.Single(h.Mail.Sent).Subject);    // alert czeka dalej na koniec wstrzymania
        Assert.Equal(pausedUntil, h.Engine.State.Mail.RetryAt);
    }

    [Fact]
    public async Task CycleErrorEndsThePendingReportWithThatError()
    {
        using var h = new Harness();
        h.Reader.Script = _ => throw new InvalidOperationException("adapter BLE zniknął");

        h.Engine.RequestReport();
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Engine.CycleAsync(Ct));

        var state = h.Engine.State.ReportOnRequest;
        Assert.Null(state.RequestedAt);
        Assert.Contains("adapter BLE zniknął", state.LastError);
        Assert.Empty(h.Mail.Sent);
    }

    [Fact]
    public async Task BrokenBleListenDoesNotStopTheCycleOrTheReport()
    {
        // Na żywo 2026-11-14: Shiny przerwał skan COMException, a cykl (razem z czekającym raportem) kończył się błędem.
        using var db = new TempStore();
        var clock = new ManualTimeProvider(Fixtures.T0);
        var mail = new FakeMail();
        var reader = new ListenThenReadSensorReader<string>(new BrokenRadio(), clock, listenRetryPause: TimeSpan.Zero);
        var engine = new MonitorEngine(Fixtures.Config(), db.Store, reader, mail, clock, NullLogger.Instance, TimeZoneInfo.Utc);

        engine.RequestReport();
        await engine.CycleAsync(Ct);

        Assert.Null(engine.State.LastCycleError);
        var sensor = engine.State.Sensors["s1"];
        Assert.Equal(SensorReadFailure.Error, sensor.LastResult);
        Assert.Contains("nasłuch BLE przerwany błędem", sensor.LastError);
        Assert.NotNull(sensor.RetryAt);                                   // zwykły odstęp ponowień, nie próba co cykl
        Assert.Contains("raport", Assert.Single(mail.Sent).Subject);
        Assert.Null(engine.State.ReportOnRequest.LastError);
    }

    private sealed class BrokenRadio : ISensorRadio<string>
    {
        public Task<IReadOnlyList<(Glebosaurus.Core.Config.SensorConfig Sensor, string Found)>> ListenAsync(
            IReadOnlyList<Glebosaurus.Core.Config.SensorConfig> wanted, TimeSpan limit, CancellationToken cancellationToken) =>
            throw new System.Runtime.InteropServices.COMException("Nieokreślony błąd. (0x80004005)", unchecked((int)0x80004005));

        public Task<SensorReadOutcome> ReadAsync(Glebosaurus.Core.Config.SensorConfig sensor, string found, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("nasłuch nic nie znalazł, więc odczytu być nie powinno");
    }

    [Fact]
    public async Task RequestWakesTheLoopWithoutWaitingForTheNextTick()
    {
        using var h = new Harness(tick: TimeSpan.FromHours(1));
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Mail.FailWhen = _ =>
        {
            sent.TrySetResult();
            return null;
        };
        using var stop = new CancellationTokenSource();
        var run = h.Engine.RunAsync(stop.Token);

        for (var i = 0; i < 200 && h.Engine.State.LastCycleAt is null; i++)
            await Task.Delay(25, TestContext.Current.CancellationToken);
        Assert.NotNull(h.Engine.State.LastCycleAt);
        Assert.Equal(ReportRequestStatus.Queued, h.Engine.RequestReport().Status);

        await sent.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Contains("raport", Assert.Single(h.Mail.Sent).Subject);
    }

    [Fact]
    public async Task FailingCyclesDoNotSpinTheLoopWhileAReportWasRequested()
    {
        // Bez zakończenia zlecenia porażką raport zostałby w kolejce, a pętla ruszałaby od razu po każdym nieudanym cyklu.
        using var h = new Harness(tick: TimeSpan.FromHours(1));
        h.Reader.Script = _ => throw new InvalidOperationException("adapter BLE zniknął");
        h.Engine.RequestReport();
        using var stop = new CancellationTokenSource();

        var run = h.Engine.RunAsync(stop.Token);
        await Task.Delay(500, TestContext.Current.CancellationToken);
        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Single(h.Reader.Calls);
        Assert.Contains("adapter BLE zniknął", h.Engine.State.ReportOnRequest.LastError);
    }
}
