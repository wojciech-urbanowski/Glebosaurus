using Glebosaurus.Core.Cloud;
using Microsoft.Extensions.Logging;

namespace Glebosaurus.Tests;

/// <summary>Dzierżawy po stronie agenta: ważność od wysłania prośby, przydział czujników i blokada nadawcy na ręcznym czasie.</summary>
public sealed class CloudLeaseTests
{
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);
    private const string S1 = "sensor:s1";

    private static LeaseResult Granted(string resource, long epoch, double seconds) => new(resource, true) { Epoch = epoch, ValidSeconds = seconds };

    private static SyncResponse Response(string me = "bestia5", bool canSendMail = false, int? priority = null,
        IReadOnlyList<SensorLeaseView>? sensors = null, NotifierView? notifier = null, IReadOnlyList<AgentView>? agents = null) =>
        new(1, Fixtures.T0, 1, new AgentIdentity(me, canSendMail, priority))
        {
            Sensors = sensors ?? [], Notifier = notifier, Agents = agents ?? []
        };

    private static Dictionary<string, SensorHearing> Heard(ManualTimeProvider clock, int? rssi, int failures = 0, TimeSpan? ago = null) =>
        new() { ["s1"] = new(clock.GetUtcNow() - (ago ?? TimeSpan.FromSeconds(10)), rssi, failures) };

    // ---- LeaseLedger ------------------------------------------------------------------------------------------------

    [Fact]
    public void Ledger_CountsValidityFromSendingTheRequest()
    {
        var clock = new ManualTimeProvider(Fixtures.T0);
        var ledger = new LeaseLedger(clock);
        var sentAt = ledger.Now();
        clock.Advance(5 * Second);   // odpowiedź przyszła po 5 s

        ledger.Apply(sentAt, [Granted(S1, 3, 1800)]);

        Assert.Equal(TimeSpan.FromSeconds(1795), ledger.Remaining(S1));
        Assert.Equal(3, ledger.Epoch(S1));
    }

    [Fact]
    public void Ledger_ExpiresWithoutSync_ButRemembersEpochUntilDenied()
    {
        var clock = new ManualTimeProvider(Fixtures.T0);
        var ledger = new LeaseLedger(clock);
        ledger.Apply(ledger.Now(), [Granted(S1, 3, 1800)]);

        clock.Advance(31 * Minute);
        Assert.Equal(TimeSpan.Zero, ledger.Remaining(S1));
        Assert.False(ledger.Holds(S1, TimeSpan.Zero));
        Assert.Equal(3, ledger.Epoch(S1));

        ledger.Apply(ledger.Now(), [new LeaseResult(S1, false) { Holder = "pi" }]);
        Assert.Null(ledger.Epoch(S1));
    }

    // ---- SensorLeasePolicy ------------------------------------------------------------------------------------------

    [Fact]
    public void Sensor_OnlyHeardSensorsAreClaimed()
    {
        var clock = new ManualTimeProvider(Fixtures.T0);
        var policy = new SensorLeasePolicy(clock, new LeaseLedger(clock));

        Assert.Empty(policy.Plan(["s1"], new Dictionary<string, SensorHearing>()).Claims);
        Assert.Empty(policy.Plan(["s1"], Heard(clock, -60, ago: TimeSpan.FromMinutes(11))).Claims);
        Assert.Equal([S1], policy.Plan(["s1"], Heard(clock, -60)).Claims);
    }

    [Fact]
    public void Sensor_WeakerSignalWaitsLongerForFreeSensor()
    {
        var clock = new ManualTimeProvider(Fixtures.T0);
        var policy = new SensorLeasePolicy(clock, new LeaseLedger(clock));
        policy.Observe(Response(sensors: [new("s1", null, null, null)]));

        Assert.Empty(policy.Plan(["s1"], Heard(clock, -80)).Claims);
        clock.Advance(59 * Second);
        Assert.Empty(policy.Plan(["s1"], Heard(clock, -80)).Claims);
        clock.Advance(Second);
        Assert.Equal([S1], policy.Plan(["s1"], Heard(clock, -80)).Claims);
    }

    [Theory]
    [InlineData(-60, 0)]
    [InlineData(-70, 0)]
    [InlineData(-80, 60)]
    [InlineData(null, 150)]
    [InlineData(-110, 180)]
    public void Sensor_WaitDependsOnSignal(int? rssi, int seconds)
    {
        var clock = new ManualTimeProvider(Fixtures.T0);
        Assert.Equal(TimeSpan.FromSeconds(seconds), new SensorLeasePolicy(clock, new LeaseLedger(clock)).WaitFor(rssi));
    }

    [Fact]
    public void Sensor_HeldByAnotherAgent_IsLeftAlone_UnlessItIsMeAfterRestart()
    {
        var clock = new ManualTimeProvider(Fixtures.T0);
        var policy = new SensorLeasePolicy(clock, new LeaseLedger(clock));

        policy.Observe(Response(sensors: [new("s1", "pi", 600, null)]));
        Assert.Empty(policy.Plan(["s1"], Heard(clock, -50)).Claims);

        policy.Observe(Response(sensors: [new("s1", "bestia5", 600, null)]));
        Assert.Equal([S1], policy.Plan(["s1"], Heard(clock, -90)).Claims);
    }

    [Fact]
    public void Sensor_HolderRenews_AndReadsOnlyWithTwoMinutesLeft()
    {
        var clock = new ManualTimeProvider(Fixtures.T0);
        var ledger = new LeaseLedger(clock);
        var policy = new SensorLeasePolicy(clock, ledger);
        ledger.Apply(ledger.Now(), [Granted(S1, 1, 1800)]);

        Assert.Equal([S1], policy.Plan(["s1"], Heard(clock, -75)).Claims);
        clock.Advance(28 * Minute);
        Assert.True(policy.CanRead("s1"));
        clock.Advance(Second);
        Assert.False(policy.CanRead("s1"));
    }

    [Fact]
    public void Sensor_ReleasedAfterThreeFailures_ThenCooldown()
    {
        var clock = new ManualTimeProvider(Fixtures.T0);
        var ledger = new LeaseLedger(clock);
        var policy = new SensorLeasePolicy(clock, ledger);
        ledger.Apply(ledger.Now(), [Granted(S1, 7, 1800)]);

        Assert.Equal([S1], policy.Plan(["s1"], Heard(clock, -75, failures: 2)).Claims);
        var plan = policy.Plan(["s1"], Heard(clock, -75, failures: 3));

        Assert.Empty(plan.Claims);
        Assert.Equal([new LeaseRelease(S1, 7)], plan.Releases);
        Assert.False(policy.CanRead("s1"));
        // Oddanie powtarza się, dopóki synchronizacja się nie uda…
        Assert.Equal([new LeaseRelease(S1, 7)], policy.Plan(["s1"], Heard(clock, -75)).Releases);
        policy.Observe(Response(sensors: [new("s1", null, null, null)]));
        Assert.Empty(policy.Plan(["s1"], Heard(clock, -75)).Releases);
        // …a o czujnik agent nie ubiega się przez 10 min.
        clock.Advance(9 * Minute);
        Assert.Empty(policy.Plan(["s1"], Heard(clock, -60)).Claims);
        clock.Advance(Minute);
        Assert.Equal([S1], policy.Plan(["s1"], Heard(clock, -60)).Claims);
    }

    [Fact]
    public void Sensor_HandedOverWhenAnotherHearsItMuchBetterForLong()
    {
        var clock = new ManualTimeProvider(Fixtures.T0);
        var ledger = new LeaseLedger(clock);
        var policy = new SensorLeasePolicy(clock, ledger);
        ledger.Apply(ledger.Now(), [Granted(S1, 4, 1800)]);
        SyncResponse Seen(int otherRssi) => Response(sensors: [new("s1", "bestia5", 1700, new("android", otherRssi, 20))]);

        policy.Observe(Seen(-73));   // 5 dB lepiej – za mało
        Assert.Equal([S1], policy.Plan(["s1"], Heard(clock, -78)).Claims);

        policy.Observe(Seen(-72));   // 6 dB lepiej – liczy się czas
        Assert.Equal([S1], policy.Plan(["s1"], Heard(clock, -78)).Claims);
        clock.Advance(9 * Minute);
        Assert.Equal([S1], policy.Plan(["s1"], Heard(clock, -78)).Claims);

        policy.Observe(Seen(-75));   // przerwa – liczenie od nowa
        Assert.Equal([S1], policy.Plan(["s1"], Heard(clock, -78)).Claims);
        policy.Observe(Seen(-70));
        Assert.Equal([S1], policy.Plan(["s1"], Heard(clock, -78)).Claims);
        clock.Advance(10 * Minute);
        var plan = policy.Plan(["s1"], Heard(clock, -78));

        Assert.Empty(plan.Claims);
        Assert.Equal([new LeaseRelease(S1, 4)], plan.Releases);
    }

    [Fact]
    public void Sensor_ReleaseAll_GivesBackSensorsButNotNotifier()
    {
        var clock = new ManualTimeProvider(Fixtures.T0);
        var ledger = new LeaseLedger(clock);
        var policy = new SensorLeasePolicy(clock, ledger);
        ledger.Apply(ledger.Now(), [Granted(S1, 2, 1800), Granted("sensor:s2", 5, 1800), Granted("notifier", 9, 300)]);

        var plan = policy.ReleaseAll();

        Assert.Equal([new LeaseRelease(S1, 2), new LeaseRelease("sensor:s2", 5)], plan.Releases.OrderBy(r => r.Resource));
        Assert.Equal(9, ledger.Epoch("notifier"));
    }

    // ---- NotifierLockPolicy -----------------------------------------------------------------------------------------

    [Fact]
    public void Notifier_CollectorNeverClaims()
    {
        var clock = new ManualTimeProvider(Fixtures.T0);
        var policy = new NotifierLockPolicy(clock, new LeaseLedger(clock));

        Assert.Empty(policy.Plan().Claims);   // przed pierwszą synchronizacją
        policy.Observe(Response(me: "android"));
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Empty(policy.Plan().Claims);
        Assert.False(policy.CanSendMail);
    }

    [Fact]
    public void Notifier_CollectorOnlyDevice_IgnoresSenderRoleFromCloud()
    {
        // Android nie umie wysyłać maili: rola nadawcy nadana przez pomyłkę nie może zabrać blokady prawdziwemu nadawcy.
        var clock = new ManualTimeProvider(Fixtures.T0);
        var policy = new NotifierLockPolicy(clock, new LeaseLedger(clock), new NotifierLockOptions { CollectorOnly = true });
        policy.Observe(Response(me: "mi11", canSendMail: true, priority: 1));

        clock.Advance(TimeSpan.FromHours(1));
        Assert.Empty(policy.Plan().Claims);
        Assert.False(policy.CanSendMail);
    }

    [Fact]
    public void Notifier_FreeLock_TakenAfterPriorityTimesMinute()
    {
        var clock = new ManualTimeProvider(Fixtures.T0);
        var policy = new NotifierLockPolicy(clock, new LeaseLedger(clock));
        policy.Observe(Response(canSendMail: true, priority: 2));

        Assert.Empty(policy.Plan().Claims);
        clock.Advance(119 * Second);
        Assert.Empty(policy.Plan().Claims);
        clock.Advance(Second);
        Assert.Equal(["notifier"], policy.Plan().Claims);
    }

    [Fact]
    public void Notifier_HeldByOther_IsLeftAlone()
    {
        var clock = new ManualTimeProvider(Fixtures.T0);
        var policy = new NotifierLockPolicy(clock, new LeaseLedger(clock));
        policy.Observe(Response(canSendMail: true, priority: 1, notifier: new("pi", 3, 200)));

        clock.Advance(TimeSpan.FromHours(1));
        Assert.Empty(policy.Plan().Claims);
    }

    [Fact]
    public void Notifier_MailOnlyWithAtLeastOneMinuteLeft()
    {
        var clock = new ManualTimeProvider(Fixtures.T0);
        var ledger = new LeaseLedger(clock);
        var policy = new NotifierLockPolicy(clock, ledger);
        ledger.Apply(ledger.Now(), [Granted("notifier", 6, 300)]);

        Assert.True(policy.CanSendMail);
        Assert.Equal(6, policy.Epoch);
        clock.Advance(4 * Minute);
        Assert.True(policy.CanSendMail);
        clock.Advance(Second);
        Assert.False(policy.CanSendMail);
    }

    [Fact]
    public void Notifier_GivingTheLockAway_IsLoggedWithReason()
    {
        // Rasp3 2026-11-19: bestia5 oddał blokadę po cichu – przejęcie było widać tylko w chmurze.
        var clock = new ManualTimeProvider(Fixtures.T0);
        var ledger = new LeaseLedger(clock);
        var log = new CapturingLogger();
        var policy = new NotifierLockPolicy(clock, ledger, logger: log);
        ledger.Apply(ledger.Now(), [Granted("notifier", 4, 300)]);
        policy.Observe(Response(canSendMail: true, priority: 2, notifier: new("bestia5", 4, 290),
            agents: [new("rasp3", true, 1, 20), new("bestia5", true, 2, 0)]));

        policy.Plan();
        Assert.Empty(log.Entries);   // jeszcze nie: ważniejszy musi być obecny 2 min
        clock.Advance(2 * Minute);
        policy.Plan();

        var (level, message) = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Information, level);
        Assert.Contains("oddaję blokadę (epoka 4)", message);
        Assert.Contains("„rasp3” (priorytet 1, mój 2) jest obecny od 2 min", message);

        // Zatrzymanie i odebrana rola – też z przyczyną; bez blokady nic do oddania, więc bez wpisu.
        ledger.Apply(ledger.Now(), [Granted("notifier", 5, 300)]);
        policy.ReleaseForShutdown();
        Assert.Contains("zatrzymanie agenta", log.Entries[^1].Message);
        policy.ReleaseForShutdown();
        Assert.Equal(2, log.Entries.Count);
        ledger.Apply(ledger.Now(), [Granted("notifier", 6, 300)]);
        policy.Observe(Response(canSendMail: false));
        policy.Plan();
        Assert.Contains("rola nadawcy odebrana w chmurze", log.Entries[^1].Message);
    }

    [Fact]
    public void Notifier_HandsOverToReturningMoreImportantCandidate_AtSafePoint()
    {
        var clock = new ManualTimeProvider(Fixtures.T0);
        var ledger = new LeaseLedger(clock);
        var policy = new NotifierLockPolicy(clock, ledger);
        ledger.Apply(ledger.Now(), [Granted("notifier", 6, 300)]);
        SyncResponse WithPi(double? piSeenAgo) => Response(canSendMail: true, priority: 2, notifier: new("bestia5", 6, 290),
            agents: piSeenAgo is { } ago ? [new("pi", true, 1, ago), new("bestia5", true, 2, 0)] : [new("bestia5", true, 2, 0)]);

        policy.Observe(WithPi(null));
        Assert.Equal(["notifier"], policy.Plan().Claims);

        policy.Observe(WithPi(30));
        Assert.Equal(["notifier"], policy.Plan().Claims);
        clock.Advance(2 * Minute);
        Assert.Equal(["notifier"], policy.Plan(safePoint: false).Claims);   // w trakcie wysyłki – jeszcze nie
        var plan = policy.Plan();
        Assert.Empty(plan.Claims);
        Assert.Equal([new LeaseRelease("notifier", 6)], plan.Releases);
        Assert.False(policy.CanSendMail);

        // Pi jest obecne: bestia5 nie sięga po wolną blokadę…
        policy.Observe(Response(canSendMail: true, priority: 2, agents: [new("pi", true, 1, 20)]));
        clock.Advance(5 * Minute);
        Assert.Empty(policy.Plan().Claims);
        // …a gdy Pi zniknie, przejmuje ją jak zwykle (po swoich 2 min).
        policy.Observe(Response(canSendMail: true, priority: 2, agents: [new("pi", true, 1, 400)]));
        Assert.Empty(policy.Plan().Claims);
        clock.Advance(2 * Minute);
        Assert.Equal(["notifier"], policy.Plan().Claims);
    }
}
