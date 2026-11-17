using Glebosaurus.Core.Config;
using Glebosaurus.Core.Domain;

namespace Glebosaurus.Core.Alerts;

/// <summary>Alert, który w tej chwili powinien być otwarty.</summary>
public sealed record DesiredAlert(AlertKind Kind, string Detail);

/// <summary>
/// Czysta funkcja: z ostatnich odczytów i aktualnie otwartych alertów wylicza, jakie alerty mają być otwarte.
/// Różnicę (otworzyć / zamknąć) stosuje wywołujący. Histereza zapobiega miganiu alertu wokół progu.
/// </summary>
public static class AlertEvaluator
{
    /// <summary>Podnosi próg wygaszenia alertu o baterii względem progu otwarcia.</summary>
    public const int BatteryHysteresisPercent = 10;

    /// <param name="recentNewestFirst">Ostatnie udane odczyty, od najnowszego.</param>
    /// <param name="silentReference">Punkt odniesienia dla „brak odczytów”, gdy czujnik nie miał jeszcze żadnego odczytu (np. start monitora).</param>
    /// <param name="holdSilent">
    /// Okres ochronny po starcie albo przerwie monitora (<see cref="SilenceGrace"/>): nowego alertu „brak odczytów” nie
    /// otwiera, a otwarty zostaje, dopóki cisza trwa – bez zamykania i ponownego otwierania (drugiego maila).
    /// </param>
    public static IReadOnlyList<DesiredAlert> Evaluate(
        SensorConfig sensor, IReadOnlyList<StoredReading> recentNewestFirst, DateTimeOffset silentReference,
        DateTimeOffset now, IReadOnlySet<AlertKind> open, bool holdSilent = false)
    {
        var desired = new List<DesiredAlert>();

        var moisture = recentNewestFirst.Where(r => r.MoisturePercent is not null).ToList();
        if (Needed(moisture.Select(r => r.MoisturePercent!.Value).ToList(), sensor.ConsecutiveReadings,
                open.Contains(AlertKind.NeedsWater), below: sensor.WateringBelowPercent, clearAbove: sensor.WateringClearAbovePercent))
        {
            var latest = moisture.FirstOrDefault()?.MoisturePercent;
            desired.Add(new(AlertKind.NeedsWater,
                latest is { } m ? $"wilgotność gleby {m}% (próg {sensor.WateringBelowPercent}%)" : "wilgotność gleby poniżej progu"));
        }

        var battery = recentNewestFirst.Where(r => r.BatteryPercent is not null).ToList();
        if (Needed(battery.Select(r => r.BatteryPercent!.Value).ToList(), Math.Max(2, sensor.ConsecutiveReadings),
                open.Contains(AlertKind.LowBattery), below: sensor.LowBatteryPercent + 1, clearAbove: sensor.LowBatteryPercent + BatteryHysteresisPercent))
        {
            var latest = battery.FirstOrDefault()?.BatteryPercent;
            desired.Add(new(AlertKind.LowBattery,
                latest is { } b ? $"bateria czujnika {b}% (próg {sensor.LowBatteryPercent}% lub mniej)" : "niski poziom baterii czujnika"));
        }

        var reference = recentNewestFirst.FirstOrDefault()?.At ?? silentReference;
        var silentFor = now - reference;
        if (silentFor > TimeSpan.FromHours(sensor.SilentAfterHours) && (!holdSilent || open.Contains(AlertKind.SensorSilent)))
            desired.Add(new(AlertKind.SensorSilent, $"brak udanego odczytu od {Describe(silentFor)}"));

        return desired;
    }

    /// <summary>
    /// Otwarty alert trwa, dopóki najnowsza wartość nie przekroczy progu wygaszenia; zamknięty otwiera się dopiero,
    /// gdy wszystkie z ostatnich <paramref name="consecutive"/> wartości są poniżej progu.
    /// </summary>
    private static bool Needed(IReadOnlyList<int> newestFirst, int consecutive, bool isOpen, int below, int clearAbove)
    {
        if (isOpen) return newestFirst.Count == 0 || newestFirst[0] < clearAbove;
        return newestFirst.Count >= consecutive && newestFirst.Take(consecutive).All(v => v < below);
    }

    private static string Describe(TimeSpan span) =>
        span.TotalHours < 48 ? $"{(int)span.TotalHours} h" : $"{(int)span.TotalDays} dni";
}
