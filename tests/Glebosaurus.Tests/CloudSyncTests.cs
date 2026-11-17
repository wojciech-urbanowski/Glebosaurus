using Glebosaurus.Core;
using Glebosaurus.Core.Cloud;
using Microsoft.Extensions.Logging.Abstractions;

namespace Glebosaurus.Tests;

/// <summary>Bufor odczytów i synchronizacja z chmurą (w pamięci) na ręcznie przesuwanym czasie.</summary>
public sealed class CloudSyncTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly TempStore db = new();
    private readonly ManualTimeProvider clock = new(Fixtures.T0);
    private readonly FakeCloudApi api;
    private readonly FakeHearing hearing = new();
    private readonly LeaseLedger ledger;
    private readonly SensorLeasePolicy sensors;
    private readonly NotifierLockPolicy notifier;
    private readonly CloudSync sync;

    public CloudSyncTests()
    {
        api = new(clock);
        ledger = new(clock);
        sensors = new(clock, ledger);
        notifier = new(clock, ledger);
        sync = new(api, db.Store, hearing, ledger, sensors, notifier, () => ["s1", "s2"], () => new AgentStatus(AppInfo.Version, "test"),
            clock, NullLogger.Instance, new CloudSyncOptions { BatchSize = 500 });
    }

    public void Dispose() => db.Dispose();

    private async Task StoreReadingsAsync(string sensor, int count)
    {
        for (var i = 0; i < count; i++)
            await db.Store.AddReadingAsync(Fixtures.Reading(sensor, Fixtures.T0.AddMinutes(-15 * (count - i))), Ct);
    }

    [Fact]
    public async Task Backlog_IsSentInBatches_AndNeverAgain()
    {
        await StoreReadingsAsync("s1", 1200);

        Assert.Equal(SyncOutcome.Backlog, await sync.SyncOnceAsync(Ct));
        Assert.Equal(SyncOutcome.Backlog, await sync.SyncOnceAsync(Ct));
        Assert.Equal(SyncOutcome.Synced, await sync.SyncOnceAsync(Ct));
        Assert.Equal(SyncOutcome.Synced, await sync.SyncOnceAsync(Ct));

        Assert.Equal([500, 500, 200, 0], api.Requests.Select(r => r.Readings.Count));
        Assert.Equal(1200, api.Stored.Count);
        Assert.Equal(0, await db.Store.CountUnsentAsync(Ct));
        Assert.Equal(TimeSpan.FromSeconds(2), sync.NextDelay(SyncOutcome.Backlog));
        // Najstarsze idą pierwsze.
        Assert.Equal(Fixtures.T0.AddMinutes(-15 * 1200), api.Requests[0].Readings[0].At);
    }

    [Fact]
    public async Task View_FromLastSync_StaysAfterAFailure()
    {
        Assert.Null(sync.State.View);
        await sync.SyncOnceAsync(Ct);
        var view = sync.State.View!;
        Assert.Equal(("bestia5", Fixtures.T0), (view.Agent.Name, view.At));

        clock.Advance(TimeSpan.FromMinutes(1));
        api.Failures.Enqueue(new(CloudErrorKind.Unavailable, "brak sieci"));
        await sync.SyncOnceAsync(Ct);
        Assert.Same(view, sync.State.View);
        Assert.Equal("brak sieci", sync.State.LastError);
    }

    [Fact]
    public async Task RejectedReadings_AreNotResent()
    {
        await StoreReadingsAsync("s1", 2);
        await StoreReadingsAsync("nieznany", 1);

        await sync.SyncOnceAsync(Ct);
        await sync.SyncOnceAsync(Ct);

        Assert.Equal([3, 0], api.Requests.Select(r => r.Readings.Count));
        Assert.Equal(2, api.Stored.Count);
    }

    [Fact]
    public async Task NetworkOutage_KeepsReadings_BacksOff_ThenCatchesUp()
    {
        await StoreReadingsAsync("s1", 4);
        api.Failures.Enqueue(new(CloudErrorKind.Unavailable, "brak sieci"));
        api.Failures.Enqueue(new(CloudErrorKind.Unavailable, "brak sieci"));
        api.Failures.Enqueue(new(CloudErrorKind.Unavailable, "brak sieci"));

        Assert.Equal(SyncOutcome.Failed, await sync.SyncOnceAsync(Ct));
        Assert.Equal(TimeSpan.FromSeconds(30), sync.NextDelay(SyncOutcome.Failed));
        await db.Store.AddReadingAsync(Fixtures.Reading("s1", Fixtures.T0), Ct);   // agent dalej czyta
        Assert.Equal(SyncOutcome.Failed, await sync.SyncOnceAsync(Ct));
        Assert.Equal(TimeSpan.FromMinutes(1), sync.NextDelay(SyncOutcome.Failed));
        Assert.Equal(SyncOutcome.Failed, await sync.SyncOnceAsync(Ct));
        Assert.Equal(TimeSpan.FromMinutes(2), sync.NextDelay(SyncOutcome.Failed));
        Assert.Equal(3, sync.State.ConsecutiveFailures);
        Assert.Equal(5, sync.State.UnsentReadings);
        Assert.Equal(CloudErrorKind.Unavailable, sync.State.LastErrorKind);

        Assert.Equal(SyncOutcome.Synced, await sync.SyncOnceAsync(Ct));
        Assert.Equal(5, api.Stored.Count);
        Assert.Equal(0, sync.State.ConsecutiveFailures);
        Assert.Null(sync.State.LastError);
        Assert.Equal(TimeSpan.FromSeconds(60), sync.NextDelay(SyncOutcome.Synced));
    }

    [Fact]
    public async Task LostResponse_ResendingIsHarmless()
    {
        await StoreReadingsAsync("s1", 3);
        api.LoseNextResponse = true;

        Assert.Equal(SyncOutcome.Failed, await sync.SyncOnceAsync(Ct));
        Assert.Equal(3, api.Stored.Count);            // baza przyjęła, agent nie wie
        Assert.Equal(3, await db.Store.CountUnsentAsync(Ct));

        Assert.Equal(SyncOutcome.Synced, await sync.SyncOnceAsync(Ct));
        Assert.Equal(3, api.Stored.Count);
        Assert.Equal(0, await db.Store.CountUnsentAsync(Ct));
    }

    [Fact]
    public async Task HeardSensor_IsClaimed_AndValidityCountsFromSending()
    {
        hearing.Current["s1"] = new(clock.GetUtcNow().AddSeconds(-20), -65, null, null, 0);
        hearing.Current["poza-konfiguracja"] = new(clock.GetUtcNow(), -50, null, null, 0);
        api.Latency = TimeSpan.FromSeconds(5);

        await sync.SyncOnceAsync(Ct);

        var request = Assert.Single(api.Requests);
        Assert.Equal(["sensor:s1"], request.Claims);
        var sighting = Assert.Single(request.Sightings);
        Assert.Equal(("s1", -65, 20.0), (sighting.Sensor, sighting.Rssi, sighting.HeardSecondsAgo!.Value));
        Assert.Equal(TimeSpan.FromSeconds(1795), ledger.Remaining("sensor:s1"));
        Assert.True(sensors.CanRead("s1"));
        Assert.Equal(["sensor:s1"], sync.State.Leases);
    }

    [Fact]
    public async Task WithoutNetwork_LeaseRunsOut_AndReadingStops()
    {
        hearing.Current["s1"] = new(clock.GetUtcNow(), -65, null, null, 0);
        await sync.SyncOnceAsync(Ct);
        Assert.True(sensors.CanRead("s1"));

        for (var minute = 0; minute < 29; minute++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            api.Failures.Enqueue(new(CloudErrorKind.Unavailable, "brak sieci"));
            await sync.SyncOnceAsync(Ct);
        }

        Assert.False(sensors.CanRead("s1"));   // mniej niż 2 min ważności – odczytów już nie zaczyna
    }

    [Fact]
    public async Task RevokedToken_StopsSynchronisation()
    {
        api.Failures.Enqueue(new(CloudErrorKind.Unauthorized, "agent_sync: 403 nieznany lub unieważniony token agenta", "28000"));

        Assert.Equal(SyncOutcome.Revoked, await sync.SyncOnceAsync(Ct));
        Assert.Equal(SyncOutcome.Revoked, await sync.SyncOnceAsync(Ct));

        Assert.Single(api.Requests);
        Assert.True(sync.State.Revoked);
        Assert.Equal(Timeout.InfiniteTimeSpan, sync.NextDelay(SyncOutcome.Revoked));
    }

    [Fact]
    public async Task Commands_AreHandedOutOncePerId()
    {
        CloudCommand Command(long id) => new(id, "read_now", "s1", null, clock.GetUtcNow(), 600);
        api.Commands = [Command(1), Command(2)];
        await sync.SyncOnceAsync(Ct);
        Assert.Equal([1L, 2L], sync.TakeNewCommands().Select(c => c.Id));

        await sync.SyncOnceAsync(Ct);
        Assert.Empty(sync.TakeNewCommands());

        api.Commands = [Command(2), Command(3)];
        await sync.SyncOnceAsync(Ct);
        Assert.Equal([3L], sync.TakeNewCommands().Select(c => c.Id));
    }

    [Fact]
    public async Task Shutdown_ReleasesSensorsAndNotifier_AndSendsLastReadings()
    {
        api.Identity = new("bestia5", true, 1);
        hearing.Current["s1"] = new(clock.GetUtcNow(), -65, null, null, 0);
        await sync.SyncOnceAsync(Ct);                    // czujnik + poznanie roli
        await sync.SyncOnceAsync(Ct);                    // blokada wolna – liczenie priorytetu
        clock.Advance(TimeSpan.FromMinutes(1));
        await sync.SyncOnceAsync(Ct);                    // blokada nadawcy po 1 min (priorytet 1)
        Assert.True(notifier.CanSendMail);
        await db.Store.AddReadingAsync(Fixtures.Reading("s1", clock.GetUtcNow()), Ct);

        await sync.ShutdownAsync(Ct);

        var last = api.Requests[^1];
        Assert.Empty(last.Claims);
        Assert.Equal(["notifier", "sensor:s1"], last.Releases.Select(r => r.Resource).Order());
        Assert.Single(last.Readings);
        Assert.False(notifier.CanSendMail);
        Assert.False(sensors.CanRead("s1"));
    }

    [Fact]
    public async Task ConfigVersion_AndClockOffset_AreExposed()
    {
        api.ConfigVersion = 42;
        await sync.SyncOnceAsync(Ct);

        Assert.Equal(42, sync.ConfigVersion);
        Assert.Equal(TimeSpan.Zero, sync.State.ClockOffset);
        Assert.Equal(Fixtures.T0, sync.State.LastSuccessAt);
    }
}
