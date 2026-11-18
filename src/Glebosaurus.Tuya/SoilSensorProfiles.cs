using System.Globalization;

namespace Glebosaurus.Tuya;

/// <param name="At">Czas pomiaru wg zegara czujnika, a dla zwykłego raportu stanu (bez czasu) chwila odbioru.</param>
/// <param name="BatteryState">
/// Stan baterii z DP14: 0 niski, 1 średni, 2 wysoki (opis jak w roquerodrigo/ha-tuya-ble; nasza baza się z nim zgadza –
/// 2026-11-15: 0 tylko przy DP15 16–19%, 1 przy 21–56%, 2 jeszcze nie wystąpiło). Patrz <see cref="SoilSensorProfiles.BatteryStateName"/>.
/// </param>
/// <param name="RawDataPoints">Wszystkie otrzymane punkty danych (id → wartość), także niezmapowane.</param>
public sealed record SoilReading(
    DateTimeOffset At, string Profile, int? MoisturePercent, decimal? SoilTemperatureCelsius,
    int? BatteryPercent, int? BatteryState, IReadOnlyDictionary<int, string> RawDataPoints);

/// <summary>Pomiary z jednej sesji (<see cref="SoilSensorProfiles.MapSession"/>).</summary>
/// <param name="Current">Stan z chwili połączenia; null, gdy sesja urwała się w trakcie historii czujnika.</param>
/// <param name="Earlier">Zaległe pomiary z pamięci czujnika, od najstarszego (czujnik kasuje je po odebraniu).</param>
public sealed record SoilSession(SoilReading? Current, IReadOnlyList<SoilReading> Earlier);

/// <summary>Mapowanie punktów danych Tuya na pomiary gleby, per profil produktu.</summary>
public static class SoilSensorProfiles
{
    /// <summary>ATLO SMS1 / SGS01: DP3 wilgotność %, DP5 temperatura ×10 °C, DP15 bateria %, DP14 stan baterii (enum 0–2).</summary>
    public const string Sgs01 = "sgs01";

    /// <summary>
    /// Nazwa stanu baterii z DP14 („niski”, „średni”, „wysoki”); null dla braku wartości albo wartości spoza zakresu.
    /// DP14 to stopień wyznaczony przez czujnik – mniej skacze niż procent z DP15 (szacunek z napięcia, zaniżany pod obciążeniem radia).
    /// </summary>
    public static string? BatteryStateName(int? state) => state switch
    {
        0 => "niski",
        1 => "średni",
        2 => "wysoki",
        _ => null
    };

    /// <summary>Czujnik gleby `ojzlzzsw` (z ha_tuya_ble): DP1 temperatura ×10 °C, DP2 wilgotność %. Niesprawdzony na sprzęcie.</summary>
    public const string Ojzlzzsw = "ojzlzzsw";

    /// <summary>Punkty danych, bez których odczyt jest niepełny; po ich otrzymaniu można zakończyć połączenie.</summary>
    public static IReadOnlySet<int> RequiredDataPoints(string profile) => profile switch
    {
        Sgs01 => new HashSet<int> { 3, 5, 15 },
        Ojzlzzsw => new HashSet<int> { 1, 2 },
        _ => throw new ArgumentException($"Nieznany profil czujnika: {profile}.", nameof(profile))
    };

    /// <summary>Predykat dla <see cref="TuyaSession.ReadAsync(Func{IReadOnlyList{TuyaDataPoint}, bool}?, CancellationToken)"/>.</summary>
    public static Func<IReadOnlyList<TuyaDataPoint>, bool> IsComplete(string profile)
    {
        var required = RequiredDataPoints(profile);
        return points => required.All(id => points.Any(p => p.Id == id));
    }

    public static string? ForProduct(string? productId) => productId switch
    {
        "gvygg3m8" or "jabotj1z" => Sgs01,
        "ojzlzzsw" => Ojzlzzsw,
        _ => null
    };

    /// <summary>O ile czas ostatniego rekordu może odbiegać od chwili połączenia, żeby uznać zegar czujnika za dobry.</summary>
    public static readonly TimeSpan ClockTolerance = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Zamienia wszystkie punkty danych sesji na rekordy pomiarów w kolejności odbioru, czyli od najstarszego. Czujnik po
    /// połączeniu oddaje historię w ramkach ze znacznikiem czasu (<see cref="TuyaDataPoint.MeasuredAt"/>): punkty z tym samym
    /// czasem tworzą jeden rekord, a punkty bez czasu (zwykły raport stanu) dostają <paramref name="connectedAt"/> i idą na koniec. Rekordy
    /// bez wilgotności i temperatury (np. same DP15 i DP14) są pomijane – to nie są pomiary gleby.
    /// <para>
    /// Ostatni rekord serii jest stanem z chwili połączenia. Kolejność odbioru jest wiarygodniejsza niż czasy: po utracie
    /// zasilania (wymiana baterii) czujnik liczy czas od nowa, więc rekordy po restarcie mają godziny z przeszłości i
    /// mogłyby wyprzedzić stare w sortowaniu po czasie (Cisy 2026-11-13: 15:14:52, potem 15:14:06 i 15:24:42 przy połączeniu
    /// o 15:48). Dlatego gdy czas ostatniego rekordu odbiega od <paramref name="connectedAt"/> o więcej niż
    /// <see cref="ClockTolerance"/>, przesuwamy o tę różnicę końcówkę serii liczoną od ostatniego cofnięcia czasu.
    /// </para>
    /// </summary>
    public static IReadOnlyList<SoilReading> MapAll(string profile, IEnumerable<TuyaDataPoint> points, DateTimeOffset connectedAt)
    {
        var readings = Records(profile, points.ToList(), connectedAt);
        if (readings.Length == 0) return readings;

        var drift = connectedAt - readings[^1].At;
        if (drift.Duration() <= ClockTolerance) return readings;

        var start = readings.Length - 1;
        while (start > 0 && readings[start - 1].At < readings[start].At) start--;
        for (var i = start; i < readings.Length; i++) readings[i] = readings[i] with { At = readings[i].At + drift };
        return readings;
    }

    /// <summary>
    /// Wynik jednej sesji: stan bieżący i zaległe pomiary (od najstarszego). Zwykle to <see cref="MapAll"/> – ostatni rekord
    /// jest stanem z chwili połączenia. Wyjątek: sesja przerwana limitem czasu (<paramref name="truncated"/>) bez zwykłego
    /// raportu stanu urwała się w trakcie historii – czujnik oddaje ją od najstarszego rekordu, a raport stanu na końcu.
    /// Wtedy ostatni odebrany rekord bywa sprzed dni, więc nie jest stanem bieżącym (<see cref="SoilSession.Current"/> = null)
    /// i nie wolno kotwiczyć na nim serii: rekordy zachowują czasy czujnika (ustawia je ta sesja na prośbę czujnika albo
    /// aplikacja), a te z przyszłości – zły zegar – odpadają. Resztę historii czujnik odda przy następnym połączeniu.
    /// Alpinarium 2026-11-18: przerwana sesja przesunięta „na teraz” wstawiła 3 dni historii o 3 dni i 6 h za późno,
    /// przeplatając je z prawdziwymi pomiarami z następnej sesji.
    /// </summary>
    public static SoilSession MapSession(string profile, IEnumerable<TuyaDataPoint> points, DateTimeOffset connectedAt, bool truncated)
    {
        var all = points.ToList();
        if (!truncated || all.Any(p => p.MeasuredAt is null))
        {
            var readings = MapAll(profile, all, connectedAt);
            return readings.Count == 0 ? new(null, []) : new(readings[^1], readings.Take(readings.Count - 1).ToArray());
        }
        return new(null, Records(profile, all, connectedAt).Where(r => r.At <= connectedAt + ClockTolerance).ToArray());
    }

    /// <summary>Rekordy pomiarów gleby w kolejności odbioru, z czasem czujnika; zwykły raport stanu na końcu z <paramref name="connectedAt"/>.</summary>
    private static SoilReading[] Records(string profile, List<TuyaDataPoint> all, DateTimeOffset connectedAt)
    {
        var records = all.Where(p => p.MeasuredAt is not null).GroupBy(p => p.MeasuredAt!.Value)
            .Select(group => Map(profile, group, group.Key))
            .ToList();
        // Zwykły raport stanu (bez czasu) to z definicji „teraz”, więc zawsze idzie na koniec, niezależnie od kolejności odbioru.
        if (all.Where(p => p.MeasuredAt is null).ToList() is { Count: > 0 } live) records.Add(Map(profile, live, connectedAt));
        return records.Where(reading => reading.MoisturePercent is not null || reading.SoilTemperatureCelsius is not null).ToArray();
    }

    public static SoilReading Map(string profile, IEnumerable<TuyaDataPoint> points, DateTimeOffset at)
    {
        var byId = points.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.Last());
        int? Value(int id, int type) =>
            byId.TryGetValue(id, out var p) && p.Type == type && int.TryParse(p.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
        static decimal? Tenths(int? value) => value is { } v ? v / 10m : null;

        var raw = byId.ToDictionary(p => p.Key, p => p.Value.Value);
        return profile switch
        {
            Sgs01 => new(at, profile, Value(3, 2), Tenths(Value(5, 2)), Value(15, 2), Value(14, 4), raw),
            Ojzlzzsw => new(at, profile, Value(2, 2), Tenths(Value(1, 2)), null, null, raw),
            _ => throw new ArgumentException($"Nieznany profil czujnika: {profile}.", nameof(profile))
        };
    }
}
