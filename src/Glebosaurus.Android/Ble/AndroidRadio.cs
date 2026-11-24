using Android.Bluetooth;
using Android.Bluetooth.LE;
using Android.Content;
using Android.OS;
using Glebosaurus.Core.Config;
using Glebosaurus.Core.Domain;
using Glebosaurus.Core.Monitoring;
using Glebosaurus.FlowerCare;
using Glebosaurus.Tuya;
using Microsoft.Extensions.Logging;

namespace Glebosaurus.Android.Ble;

/// <summary>Czujnik znaleziony w reklamach: adres do połączenia i średnia moc sygnału przed połączeniem.</summary>
internal sealed record AndroidFound(string Address, int? Rssi);

/// <summary>
/// Radio BLE agenta Android wprost na API Androida (ARCHITEKTURA.md, zasada 12). Shiny.BluetoothLE na Androidzie filtruje skan
/// tylko po UUID usług i prosi o uprawnienia przez aktywność, a agent pracuje w usłudze bez ekranu – dlatego tu:
/// <list type="bullet">
/// <item>skan zawsze z filtrem po MAC czujników z konfiguracji – skan bez filtra system wstrzymuje przy zgaszonym ekranie;</item>
/// <item>najwyżej 4 starty skanu na 32 s (<see cref="ScanStartLimiter"/>) – przy 6. w 30 s Android po cichu nic nie zwraca;</item>
/// <item>połączenia przez <see cref="GattConnection"/>: tylko LE, ponowienia błędu 133, zawsze <c>close()</c>.</item>
/// </list>
/// Radio robi jedno naraz: nasłuch zbieracza, odczyt albo podgląd zasięgu z ekranu czekają na siebie.
/// </summary>
internal sealed class AndroidRadio(Context context, ILogger logger) : ISensorRadio<AndroidFound>, ISensorListener
{
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan QuietTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SessionDeadline = TimeSpan.FromMinutes(3);

    /// <summary>Po rozłączeniu stos Bluetooth Androida potrzebuje chwili, zanim następny skan albo połączenie zadziała pewnie.</summary>
    private static readonly TimeSpan SettleAfterConnection = TimeSpan.FromMilliseconds(600);

    private readonly ScanStartLimiter limiter = new(TimeProvider.System);
    private readonly SemaphoreSlim busy = new(1, 1);

    private BluetoothAdapter? Adapter => (context.GetSystemService(Context.BluetoothService) as BluetoothManager)?.Adapter;

    /// <summary>Stan radia do sygnału życia i ekranu.</summary>
    public string State => Adapter switch
    {
        null => "brak Bluetooth",
        { IsEnabled: false } => "wyłączony",
        _ => "ok"
    };

    public async Task<IReadOnlyList<HeardSensor>> HearAsync(IReadOnlyList<SensorConfig> sensors, TimeSpan window, CancellationToken cancellationToken)
    {
        if (sensors.Count == 0) return [];
        await busy.WaitAsync(cancellationToken);
        try
        {
            var seen = await ScanAsync(Filters(sensors), window, null, null, cancellationToken);
            var heard = new List<HeardSensor>();
            foreach (var sensor in sensors)
                if (seen.FirstOrDefault(device => device.Matches(sensor)) is { } device)
                    heard.Add(new(sensor.Id, device.Signal.Average, device.Packets));
            return heard;
        }
        finally { busy.Release(); }
    }

    public async Task<IReadOnlyList<(SensorConfig Sensor, AndroidFound Found)>> ListenAsync(
        IReadOnlyList<SensorConfig> wanted, TimeSpan limit, CancellationToken cancellationToken)
    {
        if (wanted.Count == 0) return [];
        await busy.WaitAsync(cancellationToken);
        try
        {
            // Android podaje moc w każdym pakiecie, więc wystarczy pierwszy pakiet czekającego czujnika.
            var seen = await ScanAsync(Filters(wanted), limit, device => device.Signal.Count > 0 && wanted.Any(device.Matches),
                null, cancellationToken);
            var found = new List<(SensorConfig, AndroidFound)>();
            foreach (var sensor in wanted)
                if (seen.FirstOrDefault(device => device.Matches(sensor)) is { } device)
                    found.Add((sensor, new AndroidFound(device.Address, device.Signal.Average)));
            return found;
        }
        finally { busy.Release(); }
    }

    public async Task<SensorReadOutcome> ReadAsync(SensorConfig sensor, AndroidFound found, DateTimeOffset? historySince,
        CancellationToken cancellationToken)
    {
        await busy.WaitAsync(cancellationToken);
        try
        {
            if (sensor.Model == SensorModel.FlowerCare) return await ReadFlowerCareAsync(sensor, found, historySince, cancellationToken);
            var profile = SoilSensorProfiles.ForProduct(sensor.Tuya.ProductId);
            if (profile is null)
                return new(sensor, null, SensorReadFailure.Error, $"Brak profilu odczytu dla produktu „{sensor.Tuya.ProductId ?? "?"}”.") { SignalDbm = found.Rssi };
            var adapter = Adapter;
            if (adapter is not { IsEnabled: true })
                return new(sensor, null, SensorReadFailure.Error, "Bluetooth jest wyłączony.") { SignalDbm = found.Rssi };
            try
            {
                var device = adapter.GetRemoteDevice(found.Address)!;
                await using var connection = await GattConnection.ConnectAsync(context, device, logger, cancellationToken);
                // Chwila połączenia, nie końca sesji: ostatni rekord czujnik datuje na początek połączenia.
                var connectedAt = DateTimeOffset.UtcNow;
                var session = new TuyaSession(connection, sensor.Tuya, ResponseTimeout, sessionDeadline: SessionDeadline, quietTimeout: QuietTimeout);
                var result = await session.ReadAsync(cancellationToken);
                var mapped = SoilSensorProfiles.MapSession(profile, result.DataPoints, connectedAt, result.Truncated);
                var earlier = mapped.Earlier.Select(SensorReading.From).ToArray();
                return mapped is { Current: null, Earlier.Count: > 0 }
                    ? new(sensor, null, SensorReadFailure.Incomplete, "sesja urwana w trakcie historii czujnika")
                        { Earlier = earlier, SignalDbm = found.Rssi, Notes = result.Notes }
                    : new(sensor, mapped.Current is { } current ? SensorReading.From(current) : null, SensorReadFailure.None)
                        { Earlier = earlier, SignalDbm = found.Rssi, Notes = result.Notes };
            }
            catch (TuyaUnboundException)
            {
                return new(sensor, null, SensorReadFailure.Unbound) { SignalDbm = found.Rssi };
            }
            catch (System.OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                return new(sensor, null, SensorReadFailure.Error, e.Message) { SignalDbm = found.Rssi };
            }
            finally
            {
                await Task.Delay(SettleAfterConnection, CancellationToken.None);
            }
        }
        finally { busy.Release(); }
    }

    /// <summary>Odczyt czujnika Xiaomi Flower Care (bez klucza): pomiar bieżący, bateria i historia nowsza niż ostatni znany odczyt.</summary>
    private async Task<SensorReadOutcome> ReadFlowerCareAsync(SensorConfig sensor, AndroidFound found, DateTimeOffset? historySince,
        CancellationToken cancellationToken)
    {
        if (Adapter is not { IsEnabled: true } adapter)
            return new(sensor, null, SensorReadFailure.Error, "Bluetooth jest wyłączony.") { SignalDbm = found.Rssi };
        try
        {
            var device = adapter.GetRemoteDevice(found.Address)!;
            await using var connection = await GattConnection.ConnectAsync(context, device, logger, cancellationToken);
            var connectedAt = DateTimeOffset.UtcNow;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(SessionDeadline);
            var result = await new FlowerCareSession(connection, TimeProvider.System).ReadAsync(historySince, FlowerCareOutcome.HistoryOptions, deadline.Token);
            return FlowerCareOutcome.From(sensor, result, connectedAt, found.Rssi);
        }
        catch (System.OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            return new(sensor, null, SensorReadFailure.Error, e.Message) { SignalDbm = found.Rssi };
        }
        finally
        {
            await Task.Delay(SettleAfterConnection, CancellationToken.None);
        }
    }

    /// <summary>
    /// Podgląd zasięgu z ekranu (wybór miejsca na telefon): skan bez filtra – ekran jest wtedy włączony – z wynikiem po każdym
    /// pakiecie urządzenia Tuya. Czeka, aż zbieracz skończy bieżący nasłuch albo odczyt.
    /// </summary>
    public async Task SurveyAsync(TimeSpan duration, Action<AdvertisedDevice> onPacket, CancellationToken cancellationToken)
    {
        await busy.WaitAsync(cancellationToken);
        try
        {
            await ScanAsync(null, duration, null, device => { if (device.IsTuyaLike || device.IsFlowerCare) onPacket(device); }, cancellationToken);
        }
        finally { busy.Release(); }
    }

    /// <summary>Filtry po MAC czujników; null (skan bez filtra), gdy któryś czujnik nie ma MAC – wtedy tylko przy włączonym ekranie.</summary>
    private IReadOnlyList<string>? Filters(IReadOnlyList<SensorConfig> sensors)
    {
        var macs = new List<string>();
        foreach (var sensor in sensors)
        {
            if (!BleAddress.TryParse(sensor.Address, out var address))
            {
                logger.LogWarning("Czujnik {Sensor} nie ma adresu MAC – skan bez filtra (przy zgaszonym ekranie Android go wstrzymuje).", sensor.Name);
                return null;
            }
            macs.Add(BleAddress.Format(address));
        }
        return macs;
    }

    private async Task<IReadOnlyList<AdvertisedDevice>> ScanAsync(IReadOnlyList<string>? macs, TimeSpan limit,
        Func<AdvertisedDevice, bool>? stopWhen, Action<AdvertisedDevice>? onPacket, CancellationToken cancellationToken)
    {
        var scanner = Adapter is { IsEnabled: true } adapter ? adapter.BluetoothLeScanner : null;
        if (scanner is null) throw new InvalidOperationException("Bluetooth jest wyłączony.");
        await limiter.WaitTurnAsync(cancellationToken);

        var devices = new Dictionary<string, AdvertisedDevice>(StringComparer.OrdinalIgnoreCase);
        var stopped = false;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Bez Dispose: stos Bluetooth potrafi jeszcze chwilę po StopScan wołać wywołania zwrotne – zwolniony obiekt wywróciłby proces.
        var callback = new ScanResults(result =>
        {
            if (result.Device?.Address is not { } address) return;
            var record = result.ScanRecord;
            var serviceData = record?.GetServiceData(TuyaService);
            var manufacturer = record?.GetManufacturerSpecificData(TuyaIdentity.CompanyId);
            var miBeacon = record?.GetServiceData(MiBeaconService);
            lock (devices)
            {
                if (stopped) return;
                if (!devices.TryGetValue(address, out var device)) devices[address] = device = new AdvertisedDevice(address);
                device.Add(DateTimeOffset.UtcNow, result.Rssi, record?.DeviceName, result.IsConnectable, serviceData, manufacturer, miBeacon);
                onPacket?.Invoke(device);
                if (stopWhen?.Invoke(device) == true) done.TrySetResult();
            }
        }, error => done.TrySetException(new InvalidOperationException($"Android nie uruchomił skanu BLE (kod {error}).")));

        var filters = macs?.Select(mac => new ScanFilter.Builder().SetDeviceAddress(mac)!.Build()!).ToList() ?? [];
        var settings = new ScanSettings.Builder().SetScanMode(global::Android.Bluetooth.LE.ScanMode.LowLatency)!.Build()!;
        scanner.StartScan(filters, settings, callback);
        try
        {
            await Task.WhenAny(done.Task, Task.Delay(limit, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            if (done.Task.IsFaulted) await done.Task;
        }
        finally
        {
            try { scanner.StopScan(callback); }
            catch (Java.Lang.Exception e) { logger.LogDebug("Zatrzymanie skanu BLE: {Error}", e.Message); }
            lock (devices) stopped = true;
        }
        lock (devices) return [.. devices.Values];
    }

    private static readonly ParcelUuid TuyaService = ParcelUuid.FromString(TuyaIdentity.ServiceUuid)!;
    private static readonly ParcelUuid MiBeaconService = ParcelUuid.FromString(FlowerCareProtocol.MiBeaconService)!;

    private sealed class ScanResults(Action<ScanResult> onResult, Action<ScanFailure> onFailed) : ScanCallback
    {
        public override void OnScanResult(ScanCallbackType callbackType, ScanResult? result)
        {
            if (result is not null) onResult(result);
        }

        public override void OnBatchScanResults(IList<ScanResult>? results)
        {
            foreach (var result in results ?? []) onResult(result);
        }

        public override void OnScanFailed(ScanFailure errorCode) => onFailed(errorCode);
    }
}
