using System.Runtime.CompilerServices;
using Glebosaurus.Core;
using Glebosaurus.Core.Cloud;
using Glebosaurus.Core.Config;
using Glebosaurus.Core.Domain;
using Glebosaurus.Core.Monitoring;
using Glebosaurus.Data;
using Glebosaurus.Tuya;
using Microsoft.Extensions.Logging.Abstractions;

namespace Glebosaurus.Cloud.Tests;

/// <summary>
/// Zbieracz z synchronizacją na prawdziwej chmurze (radio udawane): agent słyszy czujnik, dostaje dzierżawę, czyta,
/// wysyła odczyt; inny agent zleca „odczytaj teraz”, chmura kieruje polecenie do właściciela dzierżawy, a wynik wraca.
/// </summary>
public sealed class CollectorEndToEndTests(CloudDatabase db) : IDisposable
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"glebosaurus-collector-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        http.Dispose();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(databasePath + suffix); } catch (IOException) { }
    }

    [Fact]
    public async Task HearClaimReadUpload_ThenReadNowFromAnotherAgent()
    {
        db.SkipIfUnavailable();
        Assert.SkipWhen(db.Settings.PublishableKey is null, "Brak klucza publikowalnego w pliku próbnego projektu.");
        var ct = TestContext.Current.CancellationToken;
        var setup = new Scenario(db, DateTimeOffset.UtcNow);
        var sensorId = await setup.SensorAsync();
        var collectorApi = await EnrollAsync(setup, "zbieracz", ct);
        var panelApi = await EnrollAsync(setup, "panel", ct);

        var store = new SqliteMonitorStore(databasePath);
        await store.InitializeAsync(ct);
        var clock = TimeProvider.System;
        var ledger = new LeaseLedger(clock);
        var leases = new SensorLeasePolicy(clock, ledger);
        var hearing = new SensorHearingTracker(clock);
        var sensor = new SensorConfig { Id = sensorId, Name = "Czujnik testowy", Tuya = new TuyaCredentials { Uuid = "0123456789abcdef", DeviceId = "x", LocalKeyHex = "a1b2c3d4e5f6" } };
        var config = new CloudAgentConfig(new AgentConfig { Sensors = [sensor] }, TimeZoneInfo.Utc, 1, new("zbieracz", false, null), null, []);
        var sync = new CloudSync(collectorApi, store, hearing, ledger, leases, new NotifierLockPolicy(clock, ledger), () => [sensorId],
            () => new AgentStatus(AppInfo.Version, "test"), clock, NullLogger.Instance);
        var collector = new CollectorEngine(() => config, store, new OkReader(), new SilentListener(), leases, hearing, collectorApi, null,
            clock, NullLogger.Instance);

        hearing.RecordHeard(sensorId, -60);
        Assert.Equal(SyncOutcome.Synced, await sync.SyncOnceAsync(ct));          // dzierżawa
        await collector.CycleAsync(ct);                                          // odczyt
        Assert.Equal(1, await store.CountUnsentAsync(ct));
        await sync.SyncOnceAsync(ct);                                            // wysyłka
        Assert.Equal(0, await store.CountUnsentAsync(ct));

        var order = await panelApi.CommandCreateAsync("read_now", sensorId, ct);  // np. lokalny panel innego agenta
        await sync.SyncOnceAsync(ct);
        foreach (var command in sync.TakeNewCommands()) Assert.True(await collector.HandleCommandAsync(command, ct));
        await collector.CycleAsync(ct);                                          // odczyt na żądanie, mimo odstępu
        await sync.SyncOnceAsync(ct);

        var now = DateTimeOffset.UtcNow;
        Assert.Equal(2, (await collectorApi.ReadingsRangeAsync([sensorId], now.AddHours(-1), now.AddMinutes(1), ct)).Count);
        var done = await db.AdminAsync("select jsonb_build_object('done', done_at is not null, 'ok', result->'ok')::text from glebosaurus.commands where id = @id",
            null, ("id", order.Id));
        Assert.True(done.GetProperty("done").GetBoolean());
        Assert.True(done.GetProperty("ok").GetBoolean());
    }

    private async Task<SupabaseCloudApi> EnrollAsync(Scenario setup, string prefix, CancellationToken ct)
    {
        var code = await setup.CodeAsync(Scenario.Unique(prefix), false, null, DateTimeOffset.UtcNow);
        var enrolled = await SupabaseCloudApi.EnrollAsync(http, db.Settings.Url, db.Settings.PublishableKey!, code, ct);
        return new SupabaseCloudApi(http, db.Settings.Url, db.Settings.PublishableKey!, enrolled.Token);
    }

    private sealed class OkReader : ISensorReader
    {
        public async IAsyncEnumerable<SensorReadOutcome> ReadAsync(IReadOnlyList<SensorConfig> sensors, TimeSpan scanWindow,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var sensor in sensors)
            {
                await Task.Yield();
                var reading = new SoilReading(DateTimeOffset.UtcNow, SoilSensorProfiles.Sgs01, 44, 19.5m, 77, 2, new Dictionary<int, string>());
                yield return new(sensor, reading, SensorReadFailure.None) { SignalDbm = -61 };
            }
        }
    }

    private sealed class SilentListener : ISensorListener
    {
        public Task<IReadOnlyList<HeardSensor>> HearAsync(IReadOnlyList<SensorConfig> sensors, TimeSpan window, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<HeardSensor>>([]);
    }
}
