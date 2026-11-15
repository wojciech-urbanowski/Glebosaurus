using Glebosaurus.Core.Charts;
using Glebosaurus.Core.Domain;

namespace Glebosaurus.Tests;

/// <summary>Przygotowanie odczytów do wykresu: rozdzielczość, uśrednianie w przedziałach czasu lokalnego, dziury w danych.</summary>
public class ReadingSeriesTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    /// <summary>Strefa „UTC+1” bez czasu letniego – wynik nie zależy od strefy komputera, na którym idą testy.</summary>
    private static readonly TimeZoneInfo PlusOne = TimeZoneInfo.CreateCustomTimeZone("Test+1", TimeSpan.FromHours(1), "Test+1", "Test+1");

    private static List<StoredReading> Every15Minutes(DateTimeOffset start, int count, Func<int, int?>? moisture = null, Func<int, decimal?>? temperature = null) =>
        Enumerable.Range(0, count)
            .Select(i => new StoredReading("s1", start + i * Interval, moisture is null ? 40 : moisture(i), temperature is null ? 20m : temperature(i), 80, 2))
            .ToList();

    [Theory]
    [InlineData(288, 300, 0)]         // 3 doby co 15 min mieszczą się w 300 punktach – odczyty wprost
    [InlineData(288, 240, 30)]        // karta na pulpicie: ok. 240 px na 3 doby → przedziały 30 min
    [InlineData(38400, 450, 1440)]    // 400 dni na szerokim ekranie → doby
    [InlineData(38400, 100, 7 * 1440)]
    public void BucketGrowsWithTheWindowAndShrinksWithAvailablePoints(int readings, int maxPoints, int expected)
    {
        var window = readings * Interval;
        Assert.Equal(expected, ReadingSeriesBuilder.ChooseBucketMinutes(readings, window, maxPoints));
    }

    [Fact]
    public void FewReadingsAreReturnedAsTheyAre()
    {
        var readings = Every15Minutes(Fixtures.T0, 4, moisture: i => 40 + i, temperature: i => 20.5m + i);

        var (bucket, points) = ReadingSeriesBuilder.Build(readings, Fixtures.T0, Fixtures.T0.AddHours(1), 300, Interval, TimeZoneInfo.Utc);

        Assert.Equal(0, bucket);
        Assert.Equal(readings.Select(r => r.At), points.Select(p => p.At));
        Assert.All(points, p => Assert.Equal(1, p.Count));
        Assert.Equal([40.0, 41, 42, 43], points.Select(p => p.Moisture!.Value));
        Assert.Equal(points.Select(p => p.Moisture), points.Select(p => p.MoistureMin));
        Assert.Equal(23.5, points[^1].TemperatureMax);
        Assert.DoesNotContain(points, p => p.GapBefore);
    }

    [Fact]
    public void ReadingsOutsideTheWindowAreIgnored()
    {
        var readings = Every15Minutes(Fixtures.T0, 8);

        var (_, points) = ReadingSeriesBuilder.Build(readings, Fixtures.T0.AddMinutes(30), Fixtures.T0.AddMinutes(60), 300, Interval, TimeZoneInfo.Utc);

        Assert.Equal([Fixtures.T0.AddMinutes(30), Fixtures.T0.AddMinutes(45), Fixtures.T0.AddMinutes(60)], points.Select(p => p.At));
    }

    [Fact]
    public void BucketsAverageAndKeepMinimumAndMaximum()
    {
        // 4 doby co 15 min, najwyżej 20 punktów: 4 doby / 20 = 4,8 h → najbliższy szczebel to 6 h.
        var start = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
        var readings = Every15Minutes(start, 4 * 96, moisture: i => 30 + i % 24, temperature: i => 10m + i % 24 / 10m);

        var (bucket, points) = ReadingSeriesBuilder.Build(readings, start, start.AddDays(4), 20, Interval, TimeZoneInfo.Utc);

        Assert.Equal(360, bucket);
        Assert.Equal(16, points.Count);
        var first = points[0];
        Assert.Equal(24, first.Count);
        Assert.Equal(41.5, first.Moisture);                                   // średnia z 30…53
        Assert.Equal(30, first.MoistureMin);
        Assert.Equal(53, first.MoistureMax);
        Assert.Equal(11.15, first.Temperature);
        Assert.Equal(10, first.TemperatureMin);
        Assert.Equal(12.3, first.TemperatureMax);
        Assert.Equal(start.AddMinutes(172.5), first.At);                      // średni czas odczytów z przedziału 00:00–05:45
        Assert.DoesNotContain(points, p => p.GapBefore);
    }

    [Fact]
    public void DailyBucketsStartAtLocalMidnight()
    {
        // W strefie UTC+1 doba lokalna 11.11 to 10.11 23:00 – 11.11 23:00 UTC.
        var start = new DateTimeOffset(2026, 11, 10, 23, 0, 0, TimeSpan.Zero);
        var readings = Every15Minutes(start, 3 * 96, moisture: i => i < 96 ? 10 : i < 192 ? 50 : 90);

        // Okno 10 dni przy 10 punktach wymusza przedziały dobowe (odczyty są tylko z pierwszych trzech).
        var (bucket, points) = ReadingSeriesBuilder.Build(readings, start, start.AddDays(10), 10, Interval, PlusOne);

        Assert.Equal(1440, bucket);
        Assert.Equal([10.0, 50, 90], points.Select(p => p.Moisture!.Value));
        Assert.All(points, p => Assert.Equal(96, p.Count));
    }

    [Fact]
    public void WeeklyBucketsStartOnMonday()
    {
        // 2026-11-16 to poniedziałek; odczyty od czwartku 12.11 do niedzieli 22.11 → dwa tygodnie: 12–15.11 i 16–22.11.
        var start = new DateTimeOffset(2026, 11, 12, 0, 0, 0, TimeSpan.Zero);
        var readings = Every15Minutes(start, 11 * 96, moisture: i => start + i * Interval < new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero) ? 20 : 60);

        // Okno 30 dni przy 10 punktach: 2 doby dałyby 15 punktów, więc przedziały tygodniowe.
        var (bucket, weeks) = ReadingSeriesBuilder.Build(readings, start, start.AddDays(30), 10, Interval, TimeZoneInfo.Utc);

        Assert.Equal(7 * 1440, bucket);
        Assert.Equal([20.0, 60], weeks.Select(p => p.Moisture!.Value));
        Assert.Equal([4 * 96, 7 * 96], weeks.Select(p => p.Count));
    }

    [Fact]
    public void MissingValuesDoNotCountAsZero()
    {
        var readings = Every15Minutes(Fixtures.T0, 3, moisture: i => i == 1 ? null : 40, temperature: _ => null);

        var (_, points) = ReadingSeriesBuilder.Build(readings, Fixtures.T0, Fixtures.T0.AddHours(1), 300, Interval, TimeZoneInfo.Utc);

        Assert.Null(points[1].Moisture);
        Assert.All(points, p => Assert.Null(p.Temperature));

        var (bucket, buckets) = ReadingSeriesBuilder.Build(Every15Minutes(Fixtures.T0, 40, moisture: i => i % 2 == 0 ? null : 50), Fixtures.T0, Fixtures.T0.AddMinutes(40 * 15), 10, Interval, TimeZoneInfo.Utc);
        Assert.NotEqual(0, bucket);
        Assert.All(buckets, p => Assert.Equal(50, p.Moisture));
    }

    [Fact]
    public void GapsAreMarkedOnlyAfterSeveralMissedReadings()
    {
        // Odczyty co 15 min, potem brak 30 min (dwa brakujące odczyty – jeszcze nie dziura), potem brak 2 h (dziura).
        var times = new[] { 0, 15, 30, 75, 90, 210, 225 }.Select(m => Fixtures.T0.AddMinutes(m));
        var readings = times.Select(t => Fixtures.Reading("s1", t)).ToList();

        var (_, points) = ReadingSeriesBuilder.Build(readings, Fixtures.T0, Fixtures.T0.AddHours(4), 300, Interval, TimeZoneInfo.Utc);

        Assert.Equal([false, false, false, false, false, true, false], points.Select(p => p.GapBefore));
    }

    [Fact]
    public void AGapInAveragedDataNeedsAWholeEmptyBucketMore()
    {
        // Przedziały 1 h: dziura dopiero, gdy odstęp przekracza 1 h + 3 interwały odczytu.
        var start = Fixtures.T0;
        var readings = Every15Minutes(start, 4 * 12)                           // 12 h co 15 min
            .Where(r => r.At < start.AddHours(5) || r.At >= start.AddHours(8))  // przerwa 5:00–8:00
            .ToList();

        var (bucket, points) = ReadingSeriesBuilder.Build(readings, start, start.AddHours(12), 12, Interval, TimeZoneInfo.Utc);

        Assert.Equal(60, bucket);
        Assert.Equal(9, points.Count);
        Assert.Equal(5, points.ToList().FindIndex(p => p.GapBefore));
        Assert.Single(points, p => p.GapBefore);
    }

    [Fact]
    public async Task LoadAsyncUsesTheSensorIntervalAndReportsHistoryBounds()
    {
        using var db = new TempStore();
        var config = Fixtures.Config();
        config.Sensors[0].ReadIntervalMinutes = 60;
        foreach (var minutes in new[] { 0, 60, 120, 300 })
            await db.Store.AddReadingAsync(Fixtures.Reading("s1", Fixtures.T0.AddMinutes(minutes)), Ct);

        var series = await ReadingSeriesBuilder.LoadAsync(config, db.Store, "s1", Fixtures.T0.AddMinutes(60), Fixtures.T0.AddHours(6), 300, TimeZoneInfo.Utc, Ct);

        Assert.NotNull(series);
        Assert.Equal(Fixtures.T0, series.FirstAt);                              // najstarszy odczyt spoza okna
        Assert.Equal(Fixtures.T0.AddMinutes(300), series.LastAt);
        Assert.Equal(3, series.Points.Count);
        Assert.DoesNotContain(series.Points, p => p.GapBefore);                 // 3 h przy odczytach co 1 h to jeszcze nie dziura
        Assert.Null(await ReadingSeriesBuilder.LoadAsync(config, db.Store, "nie-ma", Fixtures.T0, Fixtures.T0.AddHours(1), 300, TimeZoneInfo.Utc, Ct));
    }
}
