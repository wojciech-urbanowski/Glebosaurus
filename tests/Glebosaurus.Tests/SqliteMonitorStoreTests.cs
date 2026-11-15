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
