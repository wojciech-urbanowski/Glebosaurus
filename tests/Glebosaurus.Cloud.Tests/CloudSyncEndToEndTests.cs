using Glebosaurus.Core;
using Glebosaurus.Core.Cloud;
using Glebosaurus.Core.Domain;
using Glebosaurus.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Glebosaurus.Cloud.Tests;

/// <summary>
/// Kryterium z ROADMAP 2.1: przerwa sieci kończy się dosłaniem bez strat. Prawdziwy bufor SQLite, prawdziwe Data API
/// próbnego projektu i <see cref="CloudSync"/>; przerwę udaje opakowanie klienta.
/// </summary>
public sealed class CloudSyncEndToEndTests(CloudDatabase db) : IDisposable
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"glebosaurus-e2e-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        http.Dispose();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(databasePath + suffix); } catch (IOException) { }
    }

    [Fact]
    public async Task NetworkOutage_EndsWithEverythingUploaded_AndShutdownHandsSensorBack()
    {
        db.SkipIfUnavailable();
        Assert.SkipWhen(db.Settings.PublishableKey is null, "Brak klucza publikowalnego w pliku próbnego projektu.");
        var ct = TestContext.Current.CancellationToken;
        var setup = new Scenario(db, DateTimeOffset.UtcNow);
        var sensor = await setup.SensorAsync();
        var name = Scenario.Unique("e2e");
        var code = await setup.CodeAsync(name, false, null, DateTimeOffset.UtcNow);
        var enrolled = await SupabaseCloudApi.EnrollAsync(http, db.Settings.Url, db.Settings.PublishableKey!, code, ct);
        var api = new FlakyApi(new SupabaseCloudApi(http, db.Settings.Url, db.Settings.PublishableKey!, enrolled.Token)) { SyncFailuresLeft = 2 };

        var store = new SqliteMonitorStore(databasePath);
        await store.InitializeAsync(ct);
        var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        for (var i = 30; i >= 1; i--)
            await store.AddReadingAsync(new StoredReading(sensor, now.AddMinutes(-2 * i), 30 + i, 20.5m, 70, 1), ct);

        var clock = TimeProvider.System;
        var ledger = new LeaseLedger(clock);
        var sensors = new SensorLeasePolicy(clock, ledger);
        var hearing = new StaticHearing(sensor, new(DateTimeOffset.UtcNow, -60, null, null, 0));
        var sync = new CloudSync(api, store, hearing, ledger, sensors, new NotifierLockPolicy(clock, ledger), () => [sensor],
            () => new AgentStatus(AppInfo.Version, "test"), clock, NullLogger.Instance);

        Assert.Equal(SyncOutcome.Failed, await sync.SyncOnceAsync(ct));
        Assert.Equal(SyncOutcome.Failed, await sync.SyncOnceAsync(ct));
        Assert.Equal(30, sync.State.UnsentReadings);
        Assert.False(sensors.CanRead(sensor));

        Assert.Equal(SyncOutcome.Synced, await sync.SyncOnceAsync(ct));
        Assert.Equal(0, await store.CountUnsentAsync(ct));
        Assert.True(sensors.CanRead(sensor));
        Assert.InRange(ledger.Remaining(CloudProtocol.SensorResource(sensor)), TimeSpan.FromMinutes(29), TimeSpan.FromMinutes(30));
        var inCloud = await api.Inner.ReadingsRangeAsync([sensor], now.AddHours(-2), now, ct);
        Assert.Equal(await store.GetRangeAsync(sensor, now.AddHours(-2), now, ct), inCloud);

        await store.AddReadingAsync(new StoredReading(sensor, now.AddSeconds(1), 33, null, null, null), ct);
        await sync.ShutdownAsync(ct);

        Assert.Equal(31, (await api.Inner.ReadingsRangeAsync([sensor], now.AddHours(-2), now.AddMinutes(1), ct)).Count);
        var holder = await db.AdminAsync("select coalesce(to_jsonb(holder_id), 'null'::jsonb)::text from glebosaurus.leases where resource = @r",
            null, ("r", CloudProtocol.SensorResource(sensor)));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, holder.ValueKind);
    }

    private sealed class StaticHearing(string sensor, SensorObservation observation) : ISensorHearingSource
    {
        public IReadOnlyDictionary<string, SensorObservation> Observations() => new Dictionary<string, SensorObservation> { [sensor] = observation };
    }
}
