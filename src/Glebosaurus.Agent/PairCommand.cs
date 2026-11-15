using System.Text.RegularExpressions;
using Glebosaurus.Agent.Ble;
using Glebosaurus.Core.Config;
using Glebosaurus.Core.Monitoring;
using Glebosaurus.Tuya;
using Shiny.BluetoothLE;

namespace Glebosaurus.Agent;

/// <summary>
/// Lokalne parowanie nowego czujnika bez konta i aplikacji Tuya: własny deviceId i 6-bajtowy klucz zapisywane przez BLE,
/// potem weryfikacja na nowym połączeniu. Klucz trafia do pliku PRZED wysłaniem do czujnika, więc przerwane parowanie
/// wznawia się tym samym kluczem (ponowne uruchomienie tego samego polecenia).
/// </summary>
internal static partial class PairCommand
{
    /// <summary>Po zobaczeniu pierwszego kandydata czekamy chwilę, czy nie ma drugiego – nie chcemy sparować niewłaściwego czujnika.</summary>
    private static readonly TimeSpan CompetitorWindow = TimeSpan.FromSeconds(1.5);

    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// Jak długo czekać na reklamę czujnika po przypisaniu. Sparowany czujnik reklamuje się rzadko (zmierzone: ok. 1 pakiet
    /// na 12 s), a tuż po przypisaniu przez pierwszą chwilę bywa cichy – 30 s okazało się za krótkie.
    /// </summary>
    private const int VerifyScanSeconds = 90;

    public static async Task<int> RunAsync(CliOptions options, CancellationToken cancellationToken)
    {
        if (!options.ConfirmBind)
            throw new ArgumentException("pair zmienia przypisanie czujnika. Dodaj --confirm-bind i uruchom polecenie, gdy czujnik jest w trybie parowania (po włożeniu baterii nadaje tylko ok. 20 s).");
        var id = options.Id ?? throw new ArgumentException("Podaj identyfikator czujnika: --id monstera.");
        if (!IdPattern().IsMatch(id)) throw new ArgumentException("--id: tylko litery, cyfry, '.', '_' i '-'.");
        ulong? wantedMac = null;
        if (options.Mac is { } macText)
            wantedMac = BleAddress.TryParse(macText, out var parsed) ? parsed : throw new ArgumentException("--mac: nieprawidłowy adres, np. DC:23:51:CB:F7:7D.");

        var configDirectory = options.Config is { } config ? Path.GetDirectoryName(Path.GetFullPath(config))! : Directory.GetCurrentDirectory();
        var path = Path.GetFullPath(options.Credentials ?? Path.Combine(configDirectory, $"{id}.credentials.json"));
        // Istniejący plik klucza = ten sam czujnik: nigdy nie wymyślamy drugiego klucza. Dotyczy to i przerwanego parowania,
        // i czujnika, który stracił przypisanie (kasuje je reset przyciskiem, wymiana baterii nie) – wtedy przypisujemy ten sam klucz jeszcze raz.
        var credentials = File.Exists(path) ? TuyaCredentials.Load(path) : null;

        using var ble = new BleHost();
        await ble.EnsureAccessAsync();

        Console.WriteLine(credentials switch
        {
            null => $"Czekam do {options.Seconds ?? 120} s na czujnik w trybie parowania – włóż do niego baterie (skan już działa).",
            { Verified: true } => $"Klucz z {path} jest już zweryfikowany – odnawiam przypisanie tym samym kluczem (np. po resecie czujnika). Zresetuj czujnik i włóż baterie (skan już działa).",
            _ => "Wznawiam przerwane parowanie tym samym kluczem..."
        });
        var candidates = await FindCandidatesAsync(ble, credentials, wantedMac, TimeSpan.FromSeconds(options.Seconds ?? 120), cancellationToken);
        if (candidates.Count == 0)
        {
            Console.Error.WriteLine(credentials is null
                ? "Nie znaleziono niesparowanego czujnika znanego typu. Włóż baterie ponownie i uruchom polecenie jeszcze raz."
                : "Nie znaleziono czujnika z pliku klucza.");
            return 2;
        }
        if (candidates.Count > 1)
        {
            Console.Error.WriteLine("Znaleziono kilka niesparowanych czujników – nie wybieram sam, żeby nie sparować niewłaściwego. Wskaż ten, o który chodzi, przez --mac:");
            foreach (var candidate in candidates) Console.Error.WriteLine("  " + candidate.Describe(verbose: false));
            return 1;
        }

        var target = candidates[0];
        var identity = target.Identity!;
        Console.WriteLine($"Znaleziono czujnik {target.Peripheral.Uuid}, produkt {identity.ProductId ?? "ukryty"}, {(identity.IsBound ? "sparowany" : "niesparowany")}.");

        if (credentials is null)
        {
            var mac = BleAddress.TryExtract(target.Peripheral.Uuid, out var address) ? BleAddress.Format(address) : options.Mac ?? "";
            credentials = TuyaCredentials.Create(mac, identity);
            credentials.Save(path, createNew: true);
            Console.WriteLine($"Klucz lokalny zapisano w {path}. To jedyna kopia – zrób jej kopię zapasową (bez niej czujnik trzeba resetować).");
        }

        if (identity.IsBound)
        {
            Console.WriteLine("Czujnik jest już związany – przechodzę do weryfikacji klucza.");
        }
        else
        {
            try { await BindAsync(target.Peripheral, credentials, path, options.Timeout, cancellationToken); }
            catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                Console.Error.WriteLine($"Przypisanie nie powiodło się: {e.Message}");
                Console.Error.WriteLine($"Klucz jest zapisany w {path}. Włóż baterie ponownie i uruchom to samo polecenie – użyje tego samego klucza.");
                return 3;
            }
            await Task.Delay(SettleDelay, cancellationToken);
        }

        return await VerifyAsync(ble, credentials, path, id, options, configDirectory, cancellationToken);
    }

    /// <summary>
    /// Kandydat do sparowania: niesparowany, łączliwy, protokół 3 i znany typ produktu (albo – przy wznawianiu – czujnik
    /// o UUID z pliku klucza). Przy --mac tylko wskazany adres.
    /// </summary>
    private static async Task<IReadOnlyList<SeenSensor>> FindCandidatesAsync(
        BleHost ble, TuyaCredentials? existing, ulong? wantedMac, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var candidates = new Dictionary<string, SeenSensor>();
        DateTimeOffset? firstSeen = null;
        await BleScanner.ScanAsync(ble, timeout, sensor =>
        {
            if (IsCandidate(sensor, existing, wantedMac) && candidates.TryAdd(sensor.Peripheral.Uuid, sensor))
                firstSeen ??= DateTimeOffset.UtcNow;
            return firstSeen is { } first && DateTimeOffset.UtcNow - first >= CompetitorWindow;
        }, cancellationToken);
        return [.. candidates.Values];
    }

    private static bool IsCandidate(SeenSensor sensor, TuyaCredentials? existing, ulong? wantedMac)
    {
        ulong? address = BleAddress.TryExtract(sensor.Peripheral.Uuid, out var extracted) ? extracted : null;
        return PairingRules.IsCandidate(sensor.Identity, sensor.IsConnectable, address, existing, wantedMac);
    }

    private static async Task BindAsync(IPeripheral peripheral, TuyaCredentials credentials, string path, int timeoutSeconds, CancellationToken cancellationToken)
    {
        try
        {
            await peripheral.ConnectAsync(new ConnectionConfig(false), cancellationToken, TimeSpan.FromSeconds(20));
            var session = new TuyaSession(new ShinyGattLink(peripheral), credentials, TimeSpan.FromSeconds(timeoutSeconds), Console.WriteLine);
            await session.BindAsync(_ =>
            {
                // Klucz musi być na dysku, zanim opuści komputer.
                credentials.BindingAttempted = true;
                credentials.Save(path);
                return Task.CompletedTask;
            }, cancellationToken);
        }
        finally
        {
            await ShinySensorRadio.DisconnectQuietlyAsync(peripheral);
        }
    }

    private static async Task<int> VerifyAsync(
        BleHost ble, TuyaCredentials credentials, string path, string id, CliOptions options, string configDirectory, CancellationToken cancellationToken)
    {
        Console.WriteLine($"Weryfikuję klucz na nowym połączeniu (czekam do {VerifyScanSeconds} s – tuż po przypisaniu czujnik reklamuje się rzadko)...");
        SeenSensor? target = null;
        await BleScanner.ScanAsync(ble, TimeSpan.FromSeconds(VerifyScanSeconds), sensor =>
        {
            if (!sensor.Matches(credentials)) return false;
            target = sensor;
            return true;
        }, cancellationToken);

        if (target is null)
        {
            Console.Error.WriteLine($"Czujnik nie pojawił się w reklamach po przypisaniu. Klucz jest zapisany w {path}. Uruchom to samo polecenie jeszcze raz.");
            return 3;
        }

        var sensorConfig = new SensorConfig { Id = id, Name = options.Name ?? id, Tuya = credentials };
        var outcome = await ShinySensorRadio.ReadOneAsync(sensorConfig, target, cancellationToken);
        if (outcome.Failure != SensorReadFailure.None || outcome.Reading is not { } reading)
        {
            Console.Error.WriteLine($"Weryfikacja nie powiodła się ({outcome.Failure}{(outcome.Error is null ? "" : ": " + outcome.Error)}). Klucz jest zapisany w {path}.");
            Console.Error.WriteLine("Uruchom to samo polecenie jeszcze raz; jeśli czujnik nadal jest niesparowany, włóż w nim baterie ponownie.");
            return 3;
        }

        credentials.Verified = true;
        credentials.Save(path);

        var relative = Path.GetRelativePath(configDirectory, path);
        var reference = relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? path : relative;
        Console.WriteLine();
        Console.WriteLine("Sparowano i zweryfikowano.");
        Console.WriteLine($"  Wilgotność gleby : {reading.MoisturePercent?.ToString() ?? "brak"} %");
        Console.WriteLine($"  Temperatura gleby: {reading.SoilTemperatureCelsius?.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) ?? "brak"} °C");
        Console.WriteLine($"  Bateria          : {reading.BatteryPercent?.ToString() ?? "brak"} %");
        Console.WriteLine();
        Console.WriteLine("Dodaj czujnik do listy \"sensors\" w pliku konfiguracji:");
        Console.WriteLine($"  {{ \"id\": \"{id}\", \"name\": \"{(options.Name ?? id).Replace("\"", "\\\"")}\", \"tuyaFile\": \"{reference.Replace("\\", "/")}\" }}");
        return 0;
    }

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex IdPattern();
}
