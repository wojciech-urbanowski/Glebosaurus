using Glebosaurus.Core.Domain;
using Glebosaurus.Data;
using Microsoft.Data.Sqlite;

namespace Glebosaurus.Tests;

public class SqliteMonitorStoreTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task ReadingRoundTripKeepsNullsAndTemperatureTenths()
    {
        using var db = new TempStore();
        var full = Fixtures.Reading("s1", Fixtures.T0, moisture: 18, battery: 33, temperature: 23.7m, state: 1);
        var sparse = new StoredReading("s1", Fixtures.T0.AddMinutes(15), 40, null, null, null);
        await db.Store.AddReadingAsync(full, Ct);
        await db.Store.AddReadingAsync(sparse, Ct);

        var recent = await db.Store.GetRecentAsync("s1", 10, Ct);

        Assert.Equal([sparse, full], recent);
        Assert.Equal(sparse, await db.Store.GetLatestAsync("s1", Ct));
        Assert.Null(await db.Store.GetLatestAsync("nieznany", Ct));
    }

    [Fact]
    public async Task RecentReadingsAreNewestFirstAndLimited()
    {
        using var db = new TempStore();
        for (var i = 0; i < 5; i++) await db.Store.AddReadingAsync(Fixtures.Reading("s1", Fixtures.T0.AddMinutes(i), moisture: i), Ct);
        await db.Store.AddReadingAsync(Fixtures.Reading("inny", Fixtures.T0.AddMinutes(99), moisture: 99), Ct);

        var recent = await db.Store.GetRecentAsync("s1", 3, Ct);

        Assert.Equal([4, 3, 2], recent.Select(r => r.MoisturePercent));
    }

    [Fact]
    public async Task DuplicateReadingForTheSameMomentIsIgnored()
    {
        using var db = new TempStore();
        await db.Store.AddReadingAsync(Fixtures.Reading("s1", Fixtures.T0, moisture: 10), Ct);
        await db.Store.AddReadingAsync(Fixtures.Reading("s1", Fixtures.T0, moisture: 99), Ct);

        var recent = await db.Store.GetRecentAsync("s1", 10, Ct);

        Assert.Equal(10, Assert.Single(recent).MoisturePercent);
    }

    [Fact]
    public async Task RangeIsOldestFirstInclusiveAndPerSensor()
    {
        using var db = new TempStore();
        for (var i = 0; i < 6; i++) await db.Store.AddReadingAsync(Fixtures.Reading("s1", Fixtures.T0.AddMinutes(15 * i), moisture: i), Ct);
        await db.Store.AddReadingAsync(Fixtures.Reading("inny", Fixtures.T0.AddMinutes(30), moisture: 99), Ct);

        var range = await db.Store.GetRangeAsync("s1", Fixtures.T0.AddMinutes(15), Fixtures.T0.AddMinutes(60), Ct);

        Assert.Equal([1, 2, 3, 4], range.Select(r => r.MoisturePercent));
        Assert.Empty(await db.Store.GetRangeAsync("s1", Fixtures.T0.AddDays(1), Fixtures.T0.AddDays(2), Ct));
    }

    [Fact]
    public async Task BoundsAreTheOldestAndNewestReadingOfTheSensor()
    {
        using var db = new TempStore();
        Assert.Null(await db.Store.GetReadingBoundsAsync("s1", Ct));
        await db.Store.AddReadingAsync(Fixtures.Reading("s1", Fixtures.T0.AddDays(-30)), Ct);
        await db.Store.AddReadingAsync(Fixtures.Reading("s1", Fixtures.T0), Ct);
        await db.Store.AddReadingAsync(Fixtures.Reading("inny", Fixtures.T0.AddDays(-90)), Ct);

        Assert.Equal((Fixtures.T0.AddDays(-30), Fixtures.T0), await db.Store.GetReadingBoundsAsync("s1", Ct));
    }

    [Fact]
    public async Task OnlyOneOpenAlertPerSensorAndKindAndItCanReopenAfterResolve()
    {
        using var db = new TempStore();
        var first = await db.Store.OpenAlertAsync("s1", AlertKind.NeedsWater, "18%", Fixtures.T0, Ct);
        await Assert.ThrowsAsync<SqliteException>(() => db.Store.OpenAlertAsync("s1", AlertKind.NeedsWater, "17%", Fixtures.T0, Ct));
        await db.Store.OpenAlertAsync("s1", AlertKind.LowBattery, "15%", Fixtures.T0, Ct); // inny rodzaj - dozwolone

        await db.Store.ResolveAlertAsync(first.Id, Fixtures.T0.AddHours(1), Ct);
        var again = await db.Store.OpenAlertAsync("s1", AlertKind.NeedsWater, "16%", Fixtures.T0.AddHours(2), Ct);

        var open = await db.Store.GetOpenAlertsAsync(Ct);
        Assert.Equal(2, open.Count);
        Assert.Contains(open, a => a.Id == again.Id && a.Kind == AlertKind.NeedsWater);
        Assert.DoesNotContain(open, a => a.Id == first.Id);
    }

    [Fact]
    public async Task MarkNotifiedStoresTheMoment()
    {
        using var db = new TempStore();
        var alert = await db.Store.OpenAlertAsync("s1", AlertKind.NeedsWater, "18%", Fixtures.T0, Ct);
        Assert.Null(Assert.Single(await db.Store.GetOpenAlertsAsync(Ct)).LastNotifiedAt);

        await db.Store.MarkNotifiedAsync([alert.Id], Fixtures.T0.AddMinutes(5), Ct);

        Assert.Equal(Fixtures.T0.AddMinutes(5), Assert.Single(await db.Store.GetOpenAlertsAsync(Ct)).LastNotifiedAt);
    }

    [Fact]
    public async Task ReportRunsAreRecordedByKey()
    {
        using var db = new TempStore();
        Assert.False(await db.Store.HasReportRunAsync("07:00|2026-11-13", Ct));
        await db.Store.RecordReportRunAsync("07:00|2026-11-13", Fixtures.T0, Ct);
        Assert.True(await db.Store.HasReportRunAsync("07:00|2026-11-13", Ct));
        Assert.False(await db.Store.HasReportRunAsync("07:00|2026-11-14", Ct));
    }

    [Fact]
    public async Task PurgeRemovesOldDataOnly()
    {
        using var db = new TempStore();
        await db.Store.AddReadingAsync(Fixtures.Reading("s1", Fixtures.T0.AddDays(-500)), Ct);
        await db.Store.AddReadingAsync(Fixtures.Reading("s1", Fixtures.T0), Ct);
        var oldAlert = await db.Store.OpenAlertAsync("s1", AlertKind.NeedsWater, "x", Fixtures.T0.AddDays(-500), Ct);
        await db.Store.ResolveAlertAsync(oldAlert.Id, Fixtures.T0.AddDays(-499), Ct);
        var openAlert = await db.Store.OpenAlertAsync("s1", AlertKind.LowBattery, "y", Fixtures.T0.AddDays(-500), Ct); // otwarty zostaje

        await db.Store.PurgeAsync(Fixtures.T0.AddDays(-400), Ct);

        Assert.Single(await db.Store.GetRecentAsync("s1", 10, Ct));
        Assert.Equal(openAlert.Id, Assert.Single(await db.Store.GetOpenAlertsAsync(Ct)).Id);
    }

    [Fact]
    public async Task Outbox_GivesUnsentOldestFirst_AndForgetsAcceptedAndRejected()
    {
        using var db = new TempStore();
        var first = Fixtures.Reading("s1", Fixtures.T0);
        var second = Fixtures.Reading("s2", Fixtures.T0.AddMinutes(1));
        var third = Fixtures.Reading("s1", Fixtures.T0.AddMinutes(2));
        foreach (var reading in new[] { third, first, second }) await db.Store.AddReadingAsync(reading, Ct);

        Assert.Equal([first, second], await db.Store.GetUnsentAsync(2, Ct));
        await db.Store.MarkSentAsync([first], [second], Ct);

        Assert.Equal([third], await db.Store.GetUnsentAsync(10, Ct));
        Assert.Equal(1, await db.Store.CountUnsentAsync(Ct));
        Assert.Equal(3, (await db.Store.GetRecentAsync("s1", 10, Ct)).Count + (await db.Store.GetRecentAsync("s2", 10, Ct)).Count);
    }

    [Fact]
    public async Task DatabaseOfVersion011_GetsSyncState_StaysOpenableByOldVersion()
    {
        var path = Fixtures.TempDatabase();
        try
        {
            // Baza i zapis dokładnie jak w wersji 0.1.1 (bez sync_state).
            await ExecuteAsync(path, """
                CREATE TABLE readings (sensor_id TEXT NOT NULL, at INTEGER NOT NULL, moisture INTEGER NULL, temp_tenths INTEGER NULL,
                    battery INTEGER NULL, battery_state INTEGER NULL, PRIMARY KEY (sensor_id, at)) WITHOUT ROWID;
                CREATE TABLE alerts (id INTEGER PRIMARY KEY AUTOINCREMENT, sensor_id TEXT NOT NULL, kind TEXT NOT NULL, detail TEXT NOT NULL,
                    opened_at INTEGER NOT NULL, last_notified_at INTEGER NULL, resolved_at INTEGER NULL);
                CREATE TABLE report_runs (key TEXT PRIMARY KEY, sent_at INTEGER NOT NULL);
                INSERT INTO readings (sensor_id, at, moisture, temp_tenths, battery, battery_state) VALUES ('s1', 1000, 40, 215, 80, 2);
                PRAGMA user_version = 1;
                """);

            var store = new SqliteMonitorStore(path);
            await store.InitializeAsync(Ct);
            await store.InitializeAsync(Ct);   // ponowne otwarcie nie dodaje kolumny drugi raz

            Assert.Equal("s1", Assert.Single(await store.GetUnsentAsync(10, Ct)).SensorId);   // historia też pójdzie do chmury
            Assert.Equal(1L, await ScalarAsync(path, "PRAGMA user_version;"));                 // 0.1.x dalej ją otworzy
            await ExecuteAsync(path, "INSERT INTO readings (sensor_id, at, moisture, temp_tenths, battery, battery_state) VALUES ('s1', 2000, 41, NULL, NULL, NULL);");
            Assert.Equal(2, await store.CountUnsentAsync(Ct));                                 // zapis starej wersji = do wysłania
        }
        finally { Fixtures.DeleteDatabase(path); }
    }

    private static async Task ExecuteAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<object?> ScalarAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(Ct);
    }

    [Fact]
    public async Task DatabaseFromNewerVersionIsRejectedAndReopeningKeepsData()
    {
        using var db = new TempStore();
        await db.Store.AddReadingAsync(Fixtures.Reading("s1", Fixtures.T0), Ct);

        await new SqliteMonitorStore(db.Path).InitializeAsync(Ct); // ponowne otwarcie nie kasuje danych
        Assert.NotNull(await db.Store.GetLatestAsync("s1", Ct));

        await using (var connection = new SqliteConnection($"Data Source={db.Path};Pooling=False"))
        {
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 99;";
            await command.ExecuteNonQueryAsync(Ct);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SqliteMonitorStore(db.Path).InitializeAsync(Ct));
    }
}
