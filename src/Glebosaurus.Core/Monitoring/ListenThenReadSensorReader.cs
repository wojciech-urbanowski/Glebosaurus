using System.Runtime.CompilerServices;
using Glebosaurus.Core.Config;
using Microsoft.Extensions.Logging;

namespace Glebosaurus.Core.Monitoring;

/// <summary>
/// Radio BLE z jednym zajęciem naraz: albo słucha reklam, albo rozmawia z jednym czujnikiem. Implementacja sprzętowa jest w
/// agencie (Shiny); tu tylko kontrakt, żeby kolejność „nasłuch → odczyt” dało się testować bez sprzętu.
/// </summary>
/// <typeparam name="TFound">To, co nasłuch wie o znalezionym czujniku (w agencie: złożone reklamy i urządzenie do połączenia).</typeparam>
public interface ISensorRadio<TFound>
{
    /// <summary>
    /// Słucha reklam, aż któryś z <paramref name="wanted"/> będzie gotowy do odczytu (znaleziony, z poziomem sygnału), albo
    /// do upływu <paramref name="limit"/>.
    /// </summary>
    /// <returns>Wszystkie znalezione czujniki z <paramref name="wanted"/> (także bez poziomu sygnału, gdy minął limit); pusta lista – żadnego.</returns>
    Task<IReadOnlyList<(SensorConfig Sensor, TFound Found)>> ListenAsync(IReadOnlyList<SensorConfig> wanted, TimeSpan limit, CancellationToken cancellationToken);

    /// <summary>Łączy się z czujnikiem, odczytuje go i rozłącza; porażki zwraca jako wynik, nie wyjątek.</summary>
    Task<SensorReadOutcome> ReadAsync(SensorConfig sensor, TFound found, CancellationToken cancellationToken);
}

/// <summary>
/// Odczyt grupy czujników na przemian z nasłuchem: gdy w reklamach pojawi się którykolwiek z czekających czujników, nasłuch
/// się kończy, czujnik jest od razu czytany, a potem nasłuch wraca po pozostałe. Dzięki temu czujnik poza zasięgiem nie
/// opóźnia odczytu tych, które są w zasięgu, a radio nigdy nie słucha i nie łączy się jednocześnie (czujnik i tak przyjmuje
/// jedno połączenie, a na Raspberry Pi Wi-Fi i BLE dzielą jeden układ).
/// </summary>
/// <remarks>
/// Okno skanowania (<c>scanWindowSeconds</c>) liczy wyłącznie czas nasłuchu, łącznie ze wszystkich rund – czas połączeń się
/// nie wlicza. Czujnik, którego nie było w reklamach przez całe okno, dostaje wynik <see cref="SensorReadFailure.NotFound"/>.
/// Nasłuch przerwany wyjątkiem z radia jest wznawiany po krótkiej przerwie (w tym samym oknie), najwyżej
/// <see cref="MaxListenFailures"/> razy z rzędu; potem czekające czujniki dostają <see cref="SensorReadFailure.Error"/> z
/// przyczyną. Wyjątek nie wychodzi więc poza odczyt i nie przerywa cyklu monitora (alerty, raporty i poczta idą dalej).
/// Powód (2026-11-14, Windows): Shiny przy skanie pyta system o każde słyszane urządzenie, a jedna nieudana próba
/// (COMException 0x80004005) kończy cały skan.
/// </remarks>
public sealed class ListenThenReadSensorReader<TFound>(
    ISensorRadio<TFound> radio, TimeProvider clock, ILogger? logger = null, TimeSpan? listenRetryPause = null) : ISensorReader
{
    /// <summary>Tyle nieudanych nasłuchów z rzędu kończy próby w tym odczycie (udany nasłuch zeruje licznik).</summary>
    public const int MaxListenFailures = 3;

    /// <summary>Przerwa przed wznowieniem nieudanego nasłuchu – adapter po błędzie potrzebuje chwili (nie wlicza się do okna).</summary>
    public static readonly TimeSpan DefaultListenRetryPause = TimeSpan.FromSeconds(1);

    private readonly TimeSpan retryPause = listenRetryPause ?? DefaultListenRetryPause;

    public async IAsyncEnumerable<SensorReadOutcome> ReadAsync(
        IReadOnlyList<SensorConfig> sensors, TimeSpan scanWindow, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var order = sensors.Select((sensor, index) => (sensor, index)).ToDictionary(p => p.sensor, p => p.index);
        var waiting = sensors.ToList();
        var listening = scanWindow;
        var failures = 0;
        string? listenError = null;                                 // błąd ostatniego nasłuchu; null = ostatni się udał
        while (waiting.Count > 0 && listening > TimeSpan.Zero)
        {
            var started = clock.GetUtcNow();
            IReadOnlyList<(SensorConfig Sensor, TFound Found)> found;
            try
            {
                found = await radio.ListenAsync(waiting, listening, cancellationToken);
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested)
            {
                listening -= clock.GetUtcNow() - started;
                listenError = e.Message;
                failures++;
                var names = string.Join(", ", waiting.Select(s => s.Name));
                if (failures >= MaxListenFailures)
                {
                    logger?.LogWarning("Nasłuch BLE nie działa ({Failures} nieudane próby z rzędu, ostatnio: {Error}); czujniki {Names} – kolejna próba w następnym cyklu.",
                        failures, e.Message, names);
                    break;
                }
                // Ślad stosu tylko przy pierwszej porażce z serii – do diagnozy wystarczy, a dziennik nie puchnie.
                if (failures == 1)
                    logger?.LogWarning(e, "Nasłuch BLE przerwany błędem ({Names}); wznawiam.", names);
                else
                    logger?.LogWarning("Nasłuch BLE znów przerwany błędem: {Error}; wznawiam (próba {Attempt} z {Max}).", e.Message, failures + 1, MaxListenFailures);
                await Task.Delay(retryPause, clock, cancellationToken);
                continue;
            }
            failures = 0;
            listenError = null;
            listening -= clock.GetUtcNow() - started;
            if (found.Count == 0) break;

            // Kilka znalezionych naraz – w kolejności z konfiguracji.
            foreach (var (sensor, seen) in found.OrderBy(f => order.GetValueOrDefault(f.Sensor, int.MaxValue)))
            {
                if (!waiting.Remove(sensor)) continue;
                yield return await radio.ReadAsync(sensor, seen, cancellationToken);
            }
        }
        foreach (var sensor in waiting)
            yield return listenError is null
                ? new(sensor, null, SensorReadFailure.NotFound)
                : new(sensor, null, SensorReadFailure.Error, $"nasłuch BLE przerwany błędem: {listenError}");
    }
}
