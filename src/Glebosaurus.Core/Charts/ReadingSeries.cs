using Glebosaurus.Core.Config;
using Glebosaurus.Core.Domain;
using Glebosaurus.Core.Monitoring;

namespace Glebosaurus.Core.Charts;

/// <summary>
/// Punkt wykresu: pojedynczy odczyt (<paramref name="Count"/> = 1, min = max = wartość) albo przedział uśredniony
/// (średnia oraz najmniejsza i największa wartość z odczytów w nim). Brak wartości (null) = żaden odczyt jej nie zgłosił.
/// </summary>
/// <param name="At">Chwila punktu: czas odczytu albo średni czas odczytów z przedziału (zawsze w obrębie okna, także dla
/// przedziału, który dopiero trwa).</param>
/// <param name="GapBefore">Przed tym punktem jest dziura w danych (brak odczytów dłużej niż kilka interwałów) – linia wykresu
/// jej nie przeskakuje.</param>
public sealed record SeriesPoint(
    DateTimeOffset At, int Count,
    double? Moisture, double? MoistureMin, double? MoistureMax,
    double? Temperature, double? TemperatureMin, double? TemperatureMax,
    bool GapBefore)
{
    /// <summary>Światło w luksach (Flower Care): średnia zaokrąglona do całości, min, max.</summary>
    public double? Light { get; init; }
    public double? LightMin { get; init; }
    public double? LightMax { get; init; }

    /// <summary>Przewodność gleby – żyzność – w µS/cm (Flower Care): średnia zaokrąglona do całości, min, max.</summary>
    public double? Conductivity { get; init; }
    public double? ConductivityMin { get; init; }
    public double? ConductivityMax { get; init; }
}

/// <summary>Odczyty czujnika z okna czasu, przygotowane do wykresu (panel WWW; w przyszłości także raport mailowy).</summary>
/// <param name="BucketMinutes">Długość przedziału uśredniania; 0 = punkty to pojedyncze odczyty.</param>
/// <param name="FirstAt">Najstarszy odczyt czujnika w bazie (cała historia, nie tylko to okno); null = brak odczytów.</param>
/// <param name="LastAt">Najnowszy odczyt czujnika w bazie.</param>
public sealed record ReadingSeries(
    string SensorId, DateTimeOffset From, DateTimeOffset To, int BucketMinutes,
    DateTimeOffset? FirstAt, DateTimeOffset? LastAt, IReadOnlyList<SeriesPoint> Points);

/// <summary>
/// Dobiera rozdzielczość do liczby punktów, którą wykres potrafi pokazać (zwykle tyle, ile ma pikseli szerokości): dopóki
/// odczytów jest nie więcej, zwraca je wprost, a przy dłuższych oknach uśrednia je w przedziałach z <see cref="BucketLadder"/>.
/// Przedziały liczone są w czasie lokalnym, więc dobowe zaczynają się o północy, a tygodniowe w poniedziałek.
/// </summary>
public static class ReadingSeriesBuilder
{
    public const int MinPoints = 10;
    public const int MaxPoints = 2000;

    /// <summary>Dziura w danych = brak odczytów dłużej niż przedział plus tyle interwałów odczytu (czyli co najmniej 3 odczyty z rzędu).</summary>
    public const int GapReadIntervals = 3;

    /// <summary>
    /// Kolejne długości przedziałów w minutach. Każda dzieli dobę albo jest wielokrotnością doby, więc granice wypadają na
    /// pełnych godzinach i o północy; 7, 14 i 28 dni zaczynają się w poniedziałek.
    /// </summary>
    public static readonly IReadOnlyList<int> BucketLadder = [30, 60, 120, 180, 360, 720, 1440, 2 * 1440, 7 * 1440, 14 * 1440, 28 * 1440];

    public static async Task<ReadingSeries?> LoadAsync(
        AgentConfig config, IMonitorStore store, string sensorId, DateTimeOffset from, DateTimeOffset to, int maxPoints,
        TimeZoneInfo zone, CancellationToken cancellationToken)
    {
        var sensor = config.Sensors.FirstOrDefault(s => s.Id == sensorId);
        if (sensor is null) return null;

        var interval = TimeSpan.FromMinutes(sensor.ReadIntervalMinutes ?? config.ReadIntervalMinutes);
        var readings = await store.GetRangeAsync(sensorId, from, to, cancellationToken);
        var bounds = await store.GetReadingBoundsAsync(sensorId, cancellationToken);
        var (bucket, points) = Build(readings, from, to, maxPoints, interval, zone);
        return new(sensorId, from, to, bucket, bounds?.First, bounds?.Last, points);
    }

    /// <summary>Najkrótszy przedział, przy którym okno mieści się w <paramref name="maxPoints"/> punktach; 0 = odczyty wprost.</summary>
    public static int ChooseBucketMinutes(int readingCount, TimeSpan window, int maxPoints)
    {
        if (readingCount <= maxPoints) return 0;
        foreach (var minutes in BucketLadder)
            if (window.TotalMinutes / minutes <= maxPoints) return minutes;
        return BucketLadder[^1];
    }

    public static (int BucketMinutes, IReadOnlyList<SeriesPoint> Points) Build(
        IReadOnlyList<StoredReading> readings, DateTimeOffset from, DateTimeOffset to, int maxPoints, TimeSpan readInterval, TimeZoneInfo zone)
    {
        var inWindow = readings.Where(r => r.At >= from && r.At <= to).OrderBy(r => r.At).ToList();
        var bucket = ChooseBucketMinutes(inWindow.Count, to - from, Math.Clamp(maxPoints, MinPoints, MaxPoints));
        var gapAfter = TimeSpan.FromMinutes(bucket) + GapReadIntervals * readInterval;

        var points = new List<SeriesPoint>();
        DateTimeOffset? previous = null;
        foreach (var group in Group(inWindow, bucket, zone))
        {
            // Średnia z przesunięć względem pierwszego odczytu – małe liczby, więc bez utraty dokładności typu double.
            var at = group[0].At + TimeSpan.FromTicks((long)Math.Round(group.Average(r => (double)(r.At - group[0].At).Ticks)));
            var moisture = group.Where(r => r.MoisturePercent is not null).Select(r => (double)r.MoisturePercent!.Value).ToList();
            var temperature = group.Where(r => r.SoilTemperatureCelsius is not null).Select(r => (double)r.SoilTemperatureCelsius!.Value).ToList();
            var light = group.Where(r => r.LightLux is not null).Select(r => (double)r.LightLux!.Value).ToList();
            var conductivity = group.Where(r => r.ConductivityMicroSiemens is not null).Select(r => (double)r.ConductivityMicroSiemens!.Value).ToList();
            points.Add(new(at, group.Count,
                Average(moisture, 1), moisture.Count > 0 ? moisture.Min() : null, moisture.Count > 0 ? moisture.Max() : null,
                Average(temperature, 2), temperature.Count > 0 ? temperature.Min() : null, temperature.Count > 0 ? temperature.Max() : null,
                previous is { } p && at - p > gapAfter)
            {
                Light = Average(light, 0), LightMin = light.Count > 0 ? light.Min() : null, LightMax = light.Count > 0 ? light.Max() : null,
                Conductivity = Average(conductivity, 0), ConductivityMin = conductivity.Count > 0 ? conductivity.Min() : null,
                ConductivityMax = conductivity.Count > 0 ? conductivity.Max() : null
            });
            previous = at;
        }
        return (bucket, points);
    }

    /// <summary>
    /// Kolejne grupy odczytów: po jednym odczycie albo po przedziale czasu lokalnego. Przy cofnięciu zegara (koniec czasu
    /// letniego) powtórzona godzina trafia do tego samego przedziału co pierwsza – to tylko uśrednienie, kolejność zostaje.
    /// </summary>
    private static IEnumerable<List<StoredReading>> Group(List<StoredReading> readings, int bucketMinutes, TimeZoneInfo zone)
    {
        if (bucketMinutes == 0) return readings.Select(r => new List<StoredReading> { r });
        return readings.GroupBy(r => LocalBucketStart(r.At, bucketMinutes, zone)).Select(g => g.ToList());
    }

    /// <summary>
    /// Początek przedziału, w którym leży chwila <paramref name="at"/>, jako takty czasu lokalnego liczone od 0001-01-01
    /// (poniedziałek, północ) – stąd granice o północy i w poniedziałki. Wspólne dla wykresów w panelu i w mailu.
    /// </summary>
    public static long LocalBucketStart(DateTimeOffset at, int bucketMinutes, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(at, zone).DateTime.Ticks;
        return local - local % TimeSpan.FromMinutes(bucketMinutes).Ticks;
    }

    private static double? Average(List<double> values, int decimals) =>
        values.Count == 0 ? null : Math.Round(values.Average(), decimals, MidpointRounding.AwayFromZero);
}
