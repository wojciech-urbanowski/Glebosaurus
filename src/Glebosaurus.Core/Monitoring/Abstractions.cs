using Glebosaurus.Core.Config;
using Glebosaurus.Core.Domain;
using Glebosaurus.Tuya;

namespace Glebosaurus.Core.Monitoring;

public enum SensorReadFailure
{
    None,

    /// <summary>Czujnika nie było w reklamach w oknie skanowania (poza zasięgiem lub śpi).</summary>
    NotFound,

    /// <summary>Czujnik zgłosił się jako niesparowany (np. po resecie przyciskiem) – trzeba go sparować zapisanym kluczem.</summary>
    Unbound,

    /// <summary>Połączenie, uwierzytelnienie lub odczyt się nie powiodły.</summary>
    Error,

    /// <summary>
    /// Sesja urwała się (limit czasu) w trakcie historii czujnika: są zaległe pomiary (<see cref="SensorReadOutcome.Earlier"/>,
    /// czujnik już je skasował – trzeba je zapisać), ale nie ma stanu bieżącego; resztę czujnik odda przy następnym połączeniu.
    /// </summary>
    Incomplete
}

/// <param name="Reading">Najnowszy pomiar – to jest „stan teraz”. null przy błędzie.</param>
public sealed record SensorReadOutcome(SensorConfig Sensor, SoilReading? Reading, SensorReadFailure Failure, string? Error = null)
{
    /// <summary>
    /// Zaległe pomiary, które czujnik zgromadził od ostatniego połączenia (od najstarszego), bez <see cref="Reading"/>.
    /// Czujnik kasuje je po odebraniu, więc trzeba je zapisać od razu.
    /// </summary>
    public IReadOnlyList<SoilReading> Earlier { get; init; } = [];

    /// <summary>
    /// Moc sygnału (RSSI, dBm) z reklam zebranych, zanim czujnik został znaleziony – zwykle z jednego–dwóch pakietów, bo
    /// skan kończy się na pierwszym dopasowaniu. null = brak ważnej wartości (np. Windows podał −127) albo czujnika nie znaleziono.
    /// </summary>
    public int? SignalDbm { get; init; }

    /// <summary>Uwagi sesji z czujnikiem (np. pominięty nieczytelny raport, przekroczony limit sesji) – trafiają do dziennika.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>Warstwa radia: znajduje czujniki w reklamach BLE i odczytuje je po kolei.</summary>
public interface ISensorReader
{
    /// <summary>Zwraca po jednym wyniku dla każdego z <paramref name="sensors"/>, w miarę jak są gotowe.</summary>
    IAsyncEnumerable<SensorReadOutcome> ReadAsync(IReadOnlyList<SensorConfig> sensors, TimeSpan scanWindow, CancellationToken cancellationToken);
}

/// <param name="Rssi">Średnia z ważnych próbek (dBm); null – czujnik był słyszany, ale platforma nie podała mocy.</param>
public sealed record HeardSensor(string SensorId, int? Rssi, int Packets);

/// <summary>
/// Sam nasłuch reklam (bez łączenia) – do przydziału czujników między agentów: kto i jak dobrze słyszy który czujnik.
/// </summary>
public interface ISensorListener
{
    /// <summary>Słucha przez całe okno i zwraca wszystkie słyszane czujniki z <paramref name="sensors"/>.</summary>
    Task<IReadOnlyList<HeardSensor>> HearAsync(IReadOnlyList<SensorConfig> sensors, TimeSpan window, CancellationToken cancellationToken);
}

/// <summary>Archiwum dziennika agenta, z którego zbieracz bierze dziennik z poprzedniego dnia (do chmury, dla administratora).</summary>
public interface ILogArchive
{
    /// <returns>Pełna treść dziennika z <paramref name="day"/> (dzień czasu lokalnego) albo null, gdy tego dnia nic nie zapisano (agent nie działał).</returns>
    Task<string?> ReadDayAsync(DateOnly day, CancellationToken cancellationToken);
}

/// <summary>
/// Lokalna baza agenta: własne odczyty (bufor do wysłania do chmury) i kopia odczytów innych agentów. Alerty, raporty i
/// znaczniki wysyłek są wspólne – w chmurze.
/// </summary>
public interface IMonitorStore
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task AddReadingAsync(StoredReading reading, CancellationToken cancellationToken);
    Task<StoredReading?> GetLatestAsync(string sensorId, CancellationToken cancellationToken);

    /// <summary>Ostatnie odczyty od najnowszego.</summary>
    Task<IReadOnlyList<StoredReading>> GetRecentAsync(string sensorId, int count, CancellationToken cancellationToken);

    /// <summary>Odczyty z przedziału [<paramref name="from"/>, <paramref name="to"/>] (oba końce włącznie), od najstarszego – do wykresów.</summary>
    Task<IReadOnlyList<StoredReading>> GetRangeAsync(string sensorId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);

    /// <summary>Czas najstarszego i najnowszego odczytu czujnika w bazie; null = brak odczytów.</summary>
    Task<(DateTimeOffset First, DateTimeOffset Last)?> GetReadingBoundsAsync(string sensorId, CancellationToken cancellationToken);

    /// <summary>Usuwa odczyty starsze niż podana chwila.</summary>
    Task PurgeAsync(DateTimeOffset olderThan, CancellationToken cancellationToken);
}
