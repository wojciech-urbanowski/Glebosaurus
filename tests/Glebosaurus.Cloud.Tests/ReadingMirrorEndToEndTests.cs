using Glebosaurus.Core.Cloud;
using Glebosaurus.Core.Domain;
using Glebosaurus.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Glebosaurus.Cloud.Tests;

/// <summary>
/// Kopia odczytów dla lokalnego panelu: odczyty agenta A wysłane przez prawdziwe Data API trafiają do bazy SQLite agenta B
/// (po 30 s, gdy są „ustalone”) jako już przyjęte przez chmurę – B ich nie odsyła. Czas prawdziwy, więc test czeka ~35 s.
/// </summary>
public sealed class ReadingMirrorEndToEndTests(CloudDatabase db) : IDisposable
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"glebosaurus-mirror-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        http.Dispose();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(databasePath + suffix); } catch (IOException) { }
    }

    private async Task<SupabaseCloudApi> AgentAsync(Scenario setup, string prefix, CancellationToken ct)
    {
        var code = await setup.CodeAsync(Scenario.Unique(prefix), false, null, DateTimeOffset.UtcNow);
        var enrolled = await SupabaseCloudApi.EnrollAsync(http, db.Settings.Url, db.Settings.PublishableKey!, code, ct);
        return new SupabaseCloudApi(http, db.Settings.Url, db.Settings.PublishableKey!, enrolled.Token);
    }

    [Fact]
    public async Task ReadingsOfAnotherAgent_LandInLocalCopy_WithoutBeingUploadedAgain()
    {
        db.SkipIfUnavailable();
        Assert.SkipWhen(db.Settings.PublishableKey is null, "Brak klucza publikowalnego w pliku próbnego projektu.");
        var ct = TestContext.Current.CancellationToken;
        var setup = new Scenario(db, DateTimeOffset.UtcNow);
        var sensor = await setup.SensorAsync();
        var collector = await AgentAsync(setup, "zbieracz", ct);
        var withPanel = await AgentAsync(setup, "panel", ct);

        // Kopia zaczyna od bieżącego końca – w próbnej bazie leżą odczyty innych testów z czasem „z przyszłości”.
        var store = new SqliteMonitorStore(databasePath);
        await store.InitializeAsync(ct);
        var start = (await db.AdminAsync("select to_jsonb(coalesce(max(seq), 0))::text from glebosaurus.readings", null)).GetInt64();
        await store.AddFromCloudAsync(db.Settings.Url.Host, [], start, ct);

        var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var readings = Enumerable.Range(1, 3).Select(i => new StoredReading(sensor, now.AddMinutes(-i), 40 + i, 19.5m, 77, 2)).ToList();
        await collector.SyncAsync(new SyncRequest(new AgentStatus("0.2.0-test", "test")) { Readings = [.. readings.Select(ReadingReport.From)] }, ct);

        var mirror = new CloudReadingMirror(withPanel, store, db.Settings.Url.Host, TimeSpan.FromDays(30), TimeProvider.System, NullLogger.Instance);
        Assert.True(await mirror.PullAsync(ct));
        Assert.Equal(0, mirror.State.Added);   // jeszcze nieustalone

        await Task.Delay(TimeSpan.FromSeconds(33), ct);
        Assert.True(await mirror.PullAsync(ct));

        Assert.Equal(3, mirror.State.Added);
        Assert.Equal(readings.OrderBy(r => r.At), await store.GetRangeAsync(sensor, now.AddHours(-1), now, ct));
        Assert.Equal(0, await store.CountUnsentAsync(ct));
        Assert.True(await store.GetFeedCursorAsync(db.Settings.Url.Host, ct) > start);
    }
}
