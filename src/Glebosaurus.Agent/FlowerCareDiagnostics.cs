using System.Globalization;
using Glebosaurus.Agent.Ble;
using Glebosaurus.Core.Hosting;
using Glebosaurus.FlowerCare;
using Glebosaurus.Tuya;
using Shiny.BluetoothLE;

namespace Glebosaurus.Agent;

/// <summary>
/// <c>read --mac ADRES</c> dla czujnika Xiaomi Flower Care: jednorazowy odczyt z wypisaniem surowych danych – tabela GATT
/// (z --verbose), bateria i firmware, pomiar bieżący, zegar czujnika i rekordy historii z początku i końca pamięci.
/// Historii nie kasuje i niczego w czujniku nie zmienia (poza trybem odczytu, który czujnik przełącza sam przy każdym połączeniu).
/// </summary>
internal static class FlowerCareDiagnostics
{
    /// <summary>Ile rekordów historii wypisać z początku i z końca pamięci.</summary>
    private const int HistoryEdge = 4;

    public static async Task<int> ReadAsync(CliOptions options, CancellationToken cancellationToken)
    {
        if (!BleAddress.TryParse(options.Mac, out var wanted)) throw new ArgumentException("Podaj adres czujnika: --mac AA:BB:CC:DD:EE:FF (pokaże go glebosaurus scan).");
        var seconds = options.Seconds ?? 60;
        using var ble = new BleHost();
        await ble.EnsureAccessAsync();

        Console.WriteLine($"Szukam czujnika {BleAddress.Format(wanted)} (do {seconds} s, {BleHost.Backend})...");
        SeenSensor? target = null;
        await BleScanner.ScanAsync(ble, TimeSpan.FromSeconds(seconds), sensor =>
        {
            if (!BleAddress.TryExtract(sensor.Peripheral.Uuid, out var address) || address != wanted) return false;
            target = sensor;
            return true;
        }, cancellationToken);
        if (target is null)
        {
            Console.Error.WriteLine("Nie znaleziono czujnika w reklamach. Zbliż komputer, sprawdź baterię i czy nic innego (telefon z aplikacją Flower Care) nie trzyma połączenia.");
            return ExitCodes.NotFound;
        }
        Console.WriteLine("Znaleziono: " + target.DescribeFlowerCare(options.Verbose));

        var peripheral = target.Peripheral;
        var started = DateTimeOffset.Now;
        try
        {
            await peripheral.ConnectAsync(new ConnectionConfig(false), cancellationToken, TimeSpan.FromSeconds(20));
            Console.WriteLine($"Połączono po {(DateTimeOffset.Now - started).TotalSeconds:0.0} s (MTU danych: {peripheral.Mtu}).");

            // Mapa charakterystyka → usługa z tabeli GATT: źródła nie są zgodne, w której usłudze jest historia.
            var characteristics = await peripheral.GetAllCharacteristicsAsync(cancellationToken, TimeSpan.FromSeconds(20));
            var services = characteristics.GroupBy(c => c.Uuid.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First().Service.Uuid.ToLowerInvariant());
            if (options.Verbose)
            {
                Console.WriteLine("Tabela GATT:");
                foreach (var service in characteristics.GroupBy(c => c.Service.Uuid))
                {
                    Console.WriteLine($"  usługa {service.Key}");
                    foreach (var characteristic in service) Console.WriteLine($"    {characteristic.Uuid}  {characteristic.Properties}");
                }
            }
            var gatt = new ShinyGattClient(peripheral);
            string ServiceOf(string characteristic) =>
                services.TryGetValue(characteristic, out var service) ? service : throw new InvalidOperationException($"Czujnik nie ma charakterystyki {characteristic}.");
            Task<byte[]> Read(string characteristic) => gatt.ReadAsync(ServiceOf(characteristic), characteristic, cancellationToken);
            Task Write(string characteristic, byte[] value) => gatt.WriteAsync(ServiceOf(characteristic), characteristic, value, cancellationToken);

            var firmwareRaw = await Read(FlowerCareProtocol.FirmwareCharacteristic);
            var (battery, firmware) = FlowerCareProtocol.ParseFirmware(firmwareRaw);
            Console.WriteLine($"Bateria {battery}%, firmware {firmware}  (surowo {Convert.ToHexString(firmwareRaw)})");

            var before = await Read(FlowerCareProtocol.RealtimeCharacteristic);
            Console.WriteLine($"Pomiar przed przełączeniem trybu: {Convert.ToHexString(before)}");
            await Write(FlowerCareProtocol.CommandCharacteristic, FlowerCareProtocol.RealtimeModeCommand);
            for (var i = 1; i <= 2; i++)
            {
                var raw = await Read(FlowerCareProtocol.RealtimeCharacteristic);
                Console.WriteLine($"Pomiar bieżący #{i}: {Describe(FlowerCareProtocol.ParseRealtime(raw))}  (surowo {Convert.ToHexString(raw)})");
                if (i == 1) await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }

            await Write(FlowerCareProtocol.HistoryControlCharacteristic, FlowerCareProtocol.HistoryModeCommand);
            var countRaw = await Read(FlowerCareProtocol.HistoryDataCharacteristic);
            var timeRaw = await Read(FlowerCareProtocol.DeviceTimeCharacteristic);
            var clockAt = DateTimeOffset.Now;
            var deviceTime = FlowerCareProtocol.ParseDeviceTime(timeRaw);
            var count = FlowerCareProtocol.ParseHistoryCount(countRaw);
            Console.WriteLine($"Zegar czujnika: {deviceTime} s od startu ({TimeSpan.FromSeconds(deviceTime):d\\.hh\\:mm\\:ss}), start ok. {clockAt.AddSeconds(-deviceTime):yyyy-MM-dd HH:mm}  (surowo {Convert.ToHexString(timeRaw)})");
            Console.WriteLine($"Rekordów historii: {count}  (surowo {Convert.ToHexString(countRaw)})");

            var indexes = Enumerable.Range(0, Math.Min(count, HistoryEdge))
                .Concat(Enumerable.Range(Math.Max(HistoryEdge, count - HistoryEdge), Math.Max(0, Math.Min(HistoryEdge, count - HistoryEdge)))).ToList();
            var historyStarted = DateTimeOffset.Now;
            foreach (var index in indexes)
            {
                await Write(FlowerCareProtocol.HistoryControlCharacteristic, FlowerCareProtocol.HistoryEntryCommand(index));
                var raw = await Read(FlowerCareProtocol.HistoryDataCharacteristic);
                var record = FlowerCareProtocol.ParseHistoryRecord(index, raw);
                var at = record.SecondsSinceBoot <= deviceTime ? clockAt.AddSeconds(-(double)(deviceTime - record.SecondsSinceBoot)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "z przyszłości";
                Console.WriteLine($"  #{index,-4} {record.SecondsSinceBoot,9} s  {at}  {Describe(record.Values)}  (surowo {Convert.ToHexString(raw)})");
            }
            if (indexes.Count > 0)
                Console.WriteLine($"Rekordy: {indexes.Count} w {(DateTimeOffset.Now - historyStarted).TotalSeconds:0.0} s.");
            Console.WriteLine($"Sesja: {(DateTimeOffset.Now - started).TotalSeconds:0.0} s od początku łączenia. Historii nie skasowano.");
            return ExitCodes.Success;
        }
        finally
        {
            await ShinySensorRadio.DisconnectQuietlyAsync(peripheral);
        }
    }

    private static string Describe(FlowerCareValues values) =>
        string.Create(CultureInfo.InvariantCulture,
            $"temperatura {values.TemperatureTenths / 10m} °C, światło {values.LightLux} lx, wilgotność {values.MoisturePercent}%, przewodność {values.ConductivityMicroSiemens} µS/cm")
        + (values.Problem() is { } problem ? $" – UWAGA: {problem}" : "");
}
