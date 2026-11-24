using Glebosaurus.Core.Config;
using Glebosaurus.Core.Domain;
using Glebosaurus.Core.Monitoring;
using Glebosaurus.FlowerCare;
using Glebosaurus.Tuya;
using Shiny.BluetoothLE;

namespace Glebosaurus.Agent.Ble;

/// <summary>
/// Radio Shiny dla silnika monitora: nasłuch reklam BLE do pierwszego gotowego czujnika i odczyt jednego czujnika.
/// Kolejność „nasłuch → odczyt → nasłuch” układa <see cref="ListenThenReadSensorReader{TFound}"/> (Core, z testami).
/// </summary>
internal sealed class ShinySensorRadio(BleHost ble) : ISensorRadio<SeenSensor>, ISensorListener
{
    /// <summary>
    /// Nasłuch przez całe okno, bez łączenia – kto i jak dobrze słyszy który czujnik (przydział czujników między agentów). Średnia
    /// z ważnych próbek RSSI; czujnik słyszany tylko z −127 („brak wartości”) wraca bez mocy. Niesprawdzone na sprzęcie
    /// (2026-11-16) – ten sam skaner co <c>scan</c>, tylko bez wcześniejszego końca.
    /// </summary>
    public async Task<IReadOnlyList<HeardSensor>> HearAsync(IReadOnlyList<SensorConfig> sensors, TimeSpan window, CancellationToken cancellationToken)
    {
        var seen = await BleScanner.ScanAsync(ble, window, stopWhen: null, cancellationToken);
        var heard = new List<HeardSensor>();
        foreach (var sensor in sensors)
            if (seen.FirstOrDefault(device => device.Matches(sensor)) is { } device)
                heard.Add(new(sensor.Id, device.Signal.Average, device.Packets));
        return heard;
    }

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
            device => device.Signal.Count > 0 && wanted.Any(device.Matches), cancellationToken);
        var found = new List<(SensorConfig, SeenSensor)>();
        foreach (var sensor in wanted)
            if (seen.FirstOrDefault(device => device.Matches(sensor)) is { } device) found.Add((sensor, device));
        return found;
    }

    public Task<SensorReadOutcome> ReadAsync(SensorConfig sensor, SeenSensor found, DateTimeOffset? historySince, CancellationToken cancellationToken) =>
        ReadOneAsync(sensor, found, historySince, cancellationToken);

    /// <summary>Łączy się z już znalezionym czujnikiem, odczytuje komplet punktów danych i rozłącza.</summary>
    internal static async Task<SensorReadOutcome> ReadOneAsync(SensorConfig sensor, SeenSensor seen, DateTimeOffset? historySince,
        CancellationToken cancellationToken)
    {
        var peripheral = seen.Peripheral;
        var signal = seen.Signal.Average;
        try
        {
            if (sensor.Model == SensorModel.FlowerCare)
            {
                await peripheral.ConnectAsync(new ConnectionConfig(false), cancellationToken, ConnectTimeout);
                var connected = DateTimeOffset.UtcNow;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(SessionDeadline);
                var flowerCare = await new FlowerCareSession(new ShinyGattClient(peripheral), TimeProvider.System)
                    .ReadAsync(historySince, FlowerCareOutcome.HistoryOptions, deadline.Token);
                return FlowerCareOutcome.From(sensor, flowerCare, connected, signal);
            }
            var profile = SoilSensorProfiles.ForProduct(sensor.Tuya.ProductId)
                ?? throw new InvalidOperationException($"Brak profilu odczytu dla produktu „{sensor.Tuya.ProductId ?? "?"}”.");
            await peripheral.ConnectAsync(new ConnectionConfig(false), cancellationToken, ConnectTimeout);
            // Chwila połączenia, nie końca sesji: ostatni rekord czujnik datuje na początek połączenia, a przy długiej historii
            // sesja trwa minuty.
            var connectedAt = DateTimeOffset.UtcNow;
            var session = new TuyaSession(new ShinyGattLink(peripheral), sensor.Tuya, ResponseTimeout,
                sessionDeadline: SessionDeadline, quietTimeout: QuietTimeout);
            var result = await session.ReadAsync(cancellationToken);
            var mapped = SoilSensorProfiles.MapSession(profile, result.DataPoints, connectedAt, result.Truncated);
            var earlier = mapped.Earlier.Select(SensorReading.From).ToArray();
            return mapped is { Current: null, Earlier.Count: > 0 }
                ? new(sensor, null, SensorReadFailure.Incomplete, "sesja urwana w trakcie historii czujnika")
                    { Earlier = earlier, SignalDbm = signal, Notes = result.Notes }
                : new(sensor, mapped.Current is { } current ? SensorReading.From(current) : null, SensorReadFailure.None)
                    { Earlier = earlier, SignalDbm = signal, Notes = result.Notes };
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
