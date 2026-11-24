using System.Globalization;
using Glebosaurus.Agent.Ble;
using Glebosaurus.Core.Config;
using Glebosaurus.Core.Domain;
using Glebosaurus.Core.Hosting;
using Glebosaurus.Core.Monitoring;
using Glebosaurus.FlowerCare;
using Glebosaurus.Tuya;
using Shiny.BluetoothLE;

namespace Glebosaurus.Agent;

/// <summary>
/// <c>read --mac ADRES</c> dla czujnika Xiaomi Flower Care: jednorazowy odczyt tą samą drogą co agent (radio Shiny, sesja
/// Flower Care, wynik zbieracza) – stan bieżący, bateria i cała historia z pamięci czujnika (do limitu sesji). Z --verbose
/// najpierw osobne połączenie z surowymi danymi: tabela GATT, bajty baterii i pomiaru, zegar czujnika, rekordy z początku
/// i końca pamięci. Historii nie kasuje i niczego w czujniku nie zmienia (tryb odczytu czujnik przełącza przy każdym połączeniu).
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

        async Task<SeenSensor?> FindAsync()
        {
            Console.WriteLine($"Szukam czujnika {BleAddress.Format(wanted)} (do {seconds} s, {BleHost.Backend})...");
            SeenSensor? found = null;
            await BleScanner.ScanAsync(ble, TimeSpan.FromSeconds(seconds), sensor =>
            {
                if (!BleAddress.TryExtract(sensor.Peripheral.Uuid, out var address) || address != wanted) return false;
                found = sensor;
                return true;
            }, cancellationToken);
            if (found is null)
                Console.Error.WriteLine("Nie znaleziono czujnika w reklamach. Zbliż komputer, sprawdź baterię i czy nic innego (telefon z aplikacją Flower Care) nie trzyma połączenia.");
            else Console.WriteLine("Znaleziono: " + found.DescribeFlowerCare(options.Verbose));
            return found;
        }

        if (await FindAsync() is not { } target) return ExitCodes.NotFound;
        if (options.Verbose)
        {
            await RawDumpAsync(target, cancellationToken);
            Console.WriteLine();
            // Po rozłączeniu Shiny zwalnia urządzenie – drugie połączenie wymaga świeżego nasłuchu (agent i tak czyta zawsze
            // urządzenie z bieżącego nasłuchu), a tuż po zrzucie (pełna tabela GATT) połączenie potrafi się urwać po 2 s
            // (2026-11-24) – stąd przerwa jak między odczytami --repeat.
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            if (await FindAsync() is not { } again) return ExitCodes.NotFound;
            target = again;
        }

        var sensor = new SensorConfig { Id = "read", Name = "czujnik", Model = SensorModel.FlowerCare, Mac = BleAddress.Format(wanted) };
        var repeat = options.Repeat ?? 1;
        SensorReadOutcome outcome = null!;
        for (var attempt = 1; attempt <= repeat; attempt++)
        {
            if (attempt > 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                if (await FindAsync() is not { } next) return ExitCodes.NotFound;
                target = next;
            }
            Console.WriteLine($"Odczyt drogą agenta (radio Shiny, sesja Flower Care){(repeat > 1 ? $", {attempt} z {repeat}" : "")}...");
            outcome = await ReadOnceAsync(sensor, target, cancellationToken);
        }
        return outcome.Failure == SensorReadFailure.None ? ExitCodes.Success : ExitCodes.NoData;
    }

    private static async Task<SensorReadOutcome> ReadOnceAsync(SensorConfig sensor, SeenSensor target, CancellationToken cancellationToken)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var outcome = await ShinySensorRadio.ReadOneAsync(sensor, target, null, cancellationToken);
        Console.WriteLine($"Wynik po {timer.Elapsed.TotalSeconds:0.0} s: {outcome.Failure}{(outcome.Error is null ? "" : " – " + outcome.Error)}, sygnał {SignalStats.Format(outcome.SignalDbm)}.");
        if (outcome.Earlier.Count > 0)
        {
            Console.WriteLine($"Historia z pamięci czujnika ({outcome.Earlier.Count}), od najstarszego:");
            foreach (var earlier in outcome.Earlier) Console.WriteLine("  " + Describe(earlier));
        }
        else Console.WriteLine("Historia: brak rekordów (czujnik zapisuje rekord co godzinę od włożenia baterii).");
        if (outcome.Reading is { } reading) Console.WriteLine("Stan bieżący: " + Describe(reading));
        foreach (var note in outcome.Notes) Console.WriteLine($"  Uwaga: {note}");
        return outcome;
    }

    private static string Describe(SensorReading reading) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{reading.At.ToLocalTime():yyyy-MM-dd HH:mm:ss}  wilgotność {reading.MoisturePercent}%, temperatura {reading.TemperatureCelsius} °C, " +
            $"światło {reading.LightLux} lx, żyzność {reading.ConductivityMicroSiemens} µS/cm{(reading.BatteryPercent is { } battery ? $", bateria {battery}%" : "")}");

    /// <summary>Surowe dane z osobnego połączenia (--verbose) – do sprawdzenia protokołu na nowym firmware.</summary>
    private static async Task RawDumpAsync(SeenSensor target, CancellationToken cancellationToken)
    {
        var verbose = true;
        var peripheral = target.Peripheral;
        var started = DateTimeOffset.Now;
        try
        {
            await peripheral.ConnectAsync(new ConnectionConfig(false), cancellationToken, TimeSpan.FromSeconds(20));
            Console.WriteLine($"Połączono po {(DateTimeOffset.Now - started).TotalSeconds:0.0} s (MTU danych: {peripheral.Mtu}).");

            // Mapa charakterystyka → usługa z tabeli GATT: źródła nie są zgodne, w której usłudze jest historia.
            var characteristics = await peripheral.GetAllCharacteristicsAsync(cancellationToken, TimeSpan.FromSeconds(20));
            var services = characteristics.GroupBy(c => c.Uuid.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First().Service.Uuid.ToLowerInvariant());
            if (verbose)
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
