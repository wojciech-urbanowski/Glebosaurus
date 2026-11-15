using Glebosaurus.Core.Config;
using Glebosaurus.Core.Monitoring;
using Glebosaurus.Tuya;
using Shiny.BluetoothLE;

namespace Glebosaurus.Agent.Ble;

/// <summary>
/// Radio Shiny dla silnika monitora: nasłuch reklam BLE do pierwszego gotowego czujnika i odczyt jednego czujnika.
/// Kolejność „nasłuch → odczyt → nasłuch” układa <see cref="ListenThenReadSensorReader{TFound}"/> (Core, z testami).
/// </summary>
internal sealed class ShinySensorRadio(BleHost ble) : ISensorRadio<SeenSensor>
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(12);

    /// <summary>
    /// Czujnik po połączeniu oddaje zaległe pomiary, jeden rekord na ok. 1,1 s, od najstarszego; ostatni jest stanem z chwili
    /// połączenia. Dlatego nie kończymy na pierwszym kompletnym zestawie (to byłby najstarszy rekord), tylko po ciszy dłuższej
    /// niż odstęp między ramkami.
    /// </summary>
    private static readonly TimeSpan QuietTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Limit całej sesji; długa przerwa to dużo rekordów (3 minuty to ok. 150 godzinowych pomiarów).</summary>
    private static readonly TimeSpan SessionDeadline = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Nasłuchuje reklam, aż któryś z czekających czujników będzie miał choć jedną próbkę z ważnym RSSI, albo do końca limitu.
    /// Ważny RSSI, bo Shiny podaje −127 dla ok. 1/3 pakietów (np. odpowiedzi na skan), a bez tego dziennik często miałby „sygnał
    /// nieznany”; kolejny pakiet zwykle przychodzi w ułamku sekundy, a gdy ważnej próbki nie ma wcale, czujnik i tak zostaje
    /// zwrócony z końcem limitu i jest czytany bez poziomu sygnału.
    /// </summary>
    public async Task<IReadOnlyList<(SensorConfig Sensor, SeenSensor Found)>> ListenAsync(
        IReadOnlyList<SensorConfig> wanted, TimeSpan limit, CancellationToken cancellationToken)
    {
        var seen = await BleScanner.ScanAsync(ble, limit,
            device => device.Signal.Count > 0 && wanted.Any(s => device.Matches(s.Tuya)), cancellationToken);
        var found = new List<(SensorConfig, SeenSensor)>();
        foreach (var sensor in wanted)
            if (seen.FirstOrDefault(device => device.Matches(sensor.Tuya)) is { } device) found.Add((sensor, device));
        return found;
    }

    public Task<SensorReadOutcome> ReadAsync(SensorConfig sensor, SeenSensor found, CancellationToken cancellationToken) =>
        ReadOneAsync(sensor, found, cancellationToken);

    /// <summary>Łączy się z już znalezionym czujnikiem, odczytuje komplet punktów danych i rozłącza.</summary>
    internal static async Task<SensorReadOutcome> ReadOneAsync(SensorConfig sensor, SeenSensor seen, CancellationToken cancellationToken)
    {
        var peripheral = seen.Peripheral;
        var signal = seen.Signal.Average;
        try
        {
            var profile = SoilSensorProfiles.ForProduct(sensor.Tuya.ProductId)
                ?? throw new InvalidOperationException($"Brak profilu odczytu dla produktu „{sensor.Tuya.ProductId ?? "?"}”.");
            await peripheral.ConnectAsync(new ConnectionConfig(false), cancellationToken, ConnectTimeout);
            // Chwila połączenia, nie końca sesji: ostatni rekord czujnik datuje na początek połączenia, a przy długiej historii
            // sesja trwa minuty.
            var connectedAt = DateTimeOffset.UtcNow;
            var session = new TuyaSession(new ShinyGattLink(peripheral), sensor.Tuya, ResponseTimeout,
                sessionDeadline: SessionDeadline, quietTimeout: QuietTimeout);
            var result = await session.ReadAsync(cancellationToken);
            var readings = SoilSensorProfiles.MapAll(profile, result.DataPoints, connectedAt);
            return new(sensor, readings.LastOrDefault(), SensorReadFailure.None)
                { Earlier = readings.SkipLast(1).ToArray(), SignalDbm = signal, Notes = result.Notes };
        }
        catch (TuyaUnboundException)
        {
            return new(sensor, null, SensorReadFailure.Unbound) { SignalDbm = signal };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            return new(sensor, null, SensorReadFailure.Error, e.Message) { SignalDbm = signal };
        }
        finally
        {
            await DisconnectQuietlyAsync(peripheral);
        }
    }

    internal static async Task DisconnectQuietlyAsync(IPeripheral peripheral)
    {
        try { await peripheral.DisconnectAsync(CancellationToken.None, TimeSpan.FromSeconds(5)); }
        catch (Exception) { /* rozłączenie nie może przesłonić wyniku */ }
    }
}
