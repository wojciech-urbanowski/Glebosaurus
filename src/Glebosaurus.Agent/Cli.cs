using System.Diagnostics;
using System.Globalization;
using System.Text;
using Glebosaurus.Agent.Ble;
using Glebosaurus.Core.Config;
using Glebosaurus.Core.Mail;
using Glebosaurus.Core.Monitoring;
using Glebosaurus.Data;
using Glebosaurus.Mail;
using Glebosaurus.Tuya;
using Glebosaurus.Web;
using Microsoft.Extensions.Logging;
using Shiny.BluetoothLE;

namespace Glebosaurus.Agent;

internal sealed record CliOptions(
    string Command, int? Seconds, int Timeout, string? Credentials, string? Config, string? Output, bool Verbose, bool Quiet,
    int? Minutes, bool ConfirmBind, string? Id, string? Name, string? Mac, int? Linger, bool Samples, string? Date);

internal static class Cli
{
    private static readonly string Help = $"""
        Glebosaurus – monitor czujników gleby Tuya BLE (BLE: {BleHost.Backend})

        Praca ciągła:
          glebosaurus run [--config PLIK] [--quiet] [--minutes N]
              Czyta czujniki co ustalony odstęp, zapisuje odczyty w SQLite, pilnuje alertów i wysyła maile/raporty.
              Dziennik trafia do katalogu logs/ obok pliku konfiguracji; --quiet wyłącza wyjście na konsolę;
              --minutes N kończy pracę (czysto, z rozłączeniem czujnika) po N minutach – do prób i diagnostyki.
          glebosaurus status [--config PLIK]
              Ostatnie odczyty i otwarte alerty z lokalnej bazy (bez użycia radia).
          glebosaurus report [--config PLIK] [--out raport.html]
              Pokazuje raport (albo zapisuje jako HTML) bez wysyłania.
          glebosaurus mail-test [--config PLIK] [--samples]
              Wysyła PRAWDZIWĄ wiadomość próbną do odbiorców alertów (sprawdza konto Gmail i hasło aplikacji).
              Z --samples wysyła też przykładowy alert i raport ze zmyślonymi danymi (oznaczone „[PRZYKŁAD]”) –
              do obejrzenia wyglądu maili na telefonie – oraz, gdy jest mail.adminRecipients, przykładowy log administratora.
          glebosaurus mail-preview [--out KATALOG]
              Zapisuje przykładowe maile (wiadomość próbna, alert, raport, log administratora; HTML i tekst) w katalogu
              (domyślnie mail-preview/). Niczego nie wysyła i nie wymaga konfiguracji.
          glebosaurus send-log [--date RRRR-MM-DD] [--config PLIK]
              Wysyła PRAWDZIWY dziennik agenta z danego dnia (domyślnie wczorajszego) do administratora (mail.adminRecipients),
              z pełnym dziennikiem w załączniku. Zwykle robi to sam monitor w pierwszym przebiegu każdego dnia; to polecenie
              jest na żądanie i do sprawdzenia poczty (nie zmienia znacznika automatycznej wysyłki). Kod 2: brak dziennika z tego dnia.

        Panel WWW (diagnostyka w przeglądarce: stan, historia, dziennik, odczyt czujników na żądanie):
          glebosaurus web-password [--config PLIK]
              Ustawia hasło do panelu (co najmniej {PanelPassword.MinLength} znaków; zapisuje tylko jego skrót w pliku
              glebosaurus-web.password obok konfiguracji) i przygotowuje certyfikat HTTPS (glebosaurus-web.pfx). Wypisuje
              adresy panelu i odcisk certyfikatu do porównania z tym, który pokaże przeglądarka. Panel działa w ramach „run”,
              gdy konfiguracja ma sekcję "web" (port domyślnie {WebConfig.DefaultPort}); odpowiada tylko na połączenia z sieci lokalnej.

        Nowy czujnik:
          glebosaurus pair --id ID [--name "Nazwa"] [--config PLIK] [--credentials PLIK] [--mac ADRES] [--seconds N] --confirm-bind
              Paruje czujnik lokalnie, bez konta i aplikacji Tuya: skan rusza od razu, potem wkładasz baterie do czujnika
              (po włożeniu nadaje tylko ok. 20 s). Zapisuje klucz w ID.credentials.json (obok konfiguracji), sprawdza go na
              nowym połączeniu i podaje wpis do listy "sensors". Przerwane parowanie wznawia się tym samym poleceniem.
              Przy kilku niesparowanych czujnikach w zasięgu wymaga --mac (żeby nie sparować niewłaściwego).

        Diagnostyka:
          glebosaurus scan [--seconds N] [--verbose]
              Pokazuje urządzenia Tuya BLE w zasięgu (tylko nasłuch reklam, bez łączenia).
          glebosaurus read --credentials PLIK [--seconds N] [--timeout N]
              Jednorazowy odczyt jednego czujnika z pliku klucza; wypisuje też zaległe pomiary z czujnika (a on je potem kasuje).
          glebosaurus listen --credentials PLIK [--seconds N] [--timeout N] [--linger N] [--out PLIK]
              Jak read, ale nie kończy po zebraniu wilgotności, temperatury i baterii: zostaje połączony, aż czujnik
              zamilknie na --linger sekund (domyślnie 20, najdłużej 5 min), i wypisuje każdą ramkę (komenda, DP, czas
              czujnika, surowe bajty). Sprawdza, czy czujnik oddaje zaległe pomiary. Nic do czujnika nie wysyła poza
              zwykłym uwierzytelnieniem i prośbą o stan; --out dopisuje wynik do pliku. Kosztuje trochę baterii.

        --config PLIK    plik konfiguracji JSON (domyślnie {AgentRuntime.DefaultConfigFile} w bieżącym katalogu)
        --seconds N      czas skanowania w sekundach (scan: 20, read i listen: 60, pair: 120)
        --timeout N      limit odpowiedzi czujnika w sekundach (domyślnie 12)
        --linger N       listen: ile sekund ciszy po ostatniej ramce kończy nasłuch (domyślnie 20)
        --credentials    plik klucza lokalnego czujnika (JSON, tworzony przez pair)
        --verbose        surowe dane reklam

        Kody wyjścia: 0 sukces, 1 błąd, 2 nie znaleziono czujnika, 3 czujnik niesparowany, brak danych lub nieudane parowanie, 130 przerwano.
        """;

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var options = Parse(args);
        switch (options.Command)
        {
            case "run": return await RunMonitorAsync(options, cancellationToken);
            case "status": return await StatusAsync(options, cancellationToken);
            case "report": return await ReportAsync(options, cancellationToken);
            case "mail-test": return await MailTestAsync(options, cancellationToken);
            case "mail-preview": return await MailPreviewAsync(options, cancellationToken);
            case "send-log": return await SendLogAsync(options, cancellationToken);
            case "web-password": return await WebPasswordAsync(options, cancellationToken);
            case "pair": return await PairCommand.RunAsync(options, cancellationToken);
            case "scan": return await ScanAsync(options, cancellationToken);
            case "read": return await ReadAsync(options, cancellationToken);
            case "listen": return await ListenAsync(options, cancellationToken);
            default:
                Console.WriteLine(Help);
                return options.Command == "help" ? 0 : 1;
        }
    }

    private static CliOptions Parse(string[] args)
    {
        var hasCommand = args.Length > 0 && !args[0].StartsWith('-');
        var command = hasCommand ? args[0].ToLowerInvariant() : "help";
        int? seconds = null, minutes = null, linger = null;
        var timeout = 12;
        string? credentials = null, config = null, output = null, id = null, name = null, mac = null, date = null;
        bool verbose = false, quiet = false, confirmBind = false, samples = false;
        for (var i = hasCommand ? 1 : 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--verbose": verbose = true; break;
                case "--quiet": quiet = true; break;
                case "--confirm-bind": confirmBind = true; break;
                case "--samples": samples = true; break;
                case "--help" or "-h": command = "help"; break;
                case "--seconds": seconds = Number(args, ++i, "--seconds", 300); break;
                case "--minutes": minutes = Number(args, ++i, "--minutes", 1440); break;
                case "--timeout": timeout = Number(args, ++i, "--timeout", 120); break;
                case "--linger": linger = Number(args, ++i, "--linger", 120); break;
                case "--credentials": credentials = Value(args, ++i, "--credentials"); break;
                case "--config": config = Value(args, ++i, "--config"); break;
                case "--out": output = Value(args, ++i, "--out"); break;
                case "--id": id = Value(args, ++i, "--id"); break;
                case "--name": name = Value(args, ++i, "--name"); break;
                case "--mac": mac = Value(args, ++i, "--mac"); break;
                case "--date": date = Value(args, ++i, "--date"); break;
                default: throw new ArgumentException($"Nieznany parametr {args[i]}. Użyj --help.");
            }
        }
        return new(command, seconds, timeout, credentials, config, output, verbose, quiet, minutes, confirmBind, id, name, mac, linger, samples, date);
    }

    private static string Value(string[] args, int index, string flag) =>
        index < args.Length ? args[index] : throw new ArgumentException($"Brak wartości dla {flag}.");

    private static int Number(string[] args, int index, string flag, int maximum) =>
        index < args.Length && int.TryParse(args[index], out var value) && value >= 1 && value <= maximum
            ? value : throw new ArgumentException($"{flag}: podaj liczbę od 1 do {maximum}.");

    // ---- panel WWW ---------------------------------------------------------------------------------------------------

    private static async Task<int> WebPasswordAsync(CliOptions options, CancellationToken cancellationToken)
    {
        var loaded = AgentRuntime.LoadConfig(options.Config);
        Console.WriteLine($"Hasło do panelu WWW monitora „{loaded.Config.MonitorName}” (co najmniej {PanelPassword.MinLength} znaków; nie będzie widać wpisywanych znaków).");
        var password = await ReadSecretAsync("Nowe hasło: ", cancellationToken);
        if (PanelPassword.Problem(password) is { } problem)
        {
            Console.Error.WriteLine(problem);
            return 1;
        }
        if (await ReadSecretAsync("Powtórz hasło: ", cancellationToken) != password)
        {
            Console.Error.WriteLine("Hasła się różnią – nic nie zmieniono.");
            return 1;
        }

        PanelPassword.Write(loaded.WebPasswordPath, PanelPassword.Hash(password));
        Console.WriteLine($"Zapisano skrót hasła: {loaded.WebPasswordPath}");

        var (certificate, created) = PanelCertificate.LoadOrCreate(loaded.WebCertificatePath, loaded.Config.MonitorName, DateTimeOffset.UtcNow);
        using (certificate)
        {
            Console.WriteLine($"Certyfikat HTTPS ({(created ? "nowy" : "istniejący")}): {loaded.WebCertificatePath}, ważny do {certificate.NotAfter:yyyy-MM-dd}");
            Console.WriteLine($"Odcisk SHA-256: {PanelCertificate.Fingerprint(certificate)}");
        }
        Console.WriteLine("Przeglądarka ostrzeże, że certyfikat nie pochodzi z zaufanego urzędu – to oczekiwane; porównaj odcisk w szczegółach certyfikatu.");

        if (loaded.Config.Web is { Enabled: true } web)
        {
            Console.WriteLine($"Panel (po ponownym uruchomieniu monitora): {string.Join("  ", AgentWebPanel.Urls(web.Port))}");
            if (OperatingSystem.IsWindows())
                Console.WriteLine($"Dostęp z innych urządzeń wymaga reguły Zapory Windows (PowerShell jako administrator):{Environment.NewLine}" +
                    $"  New-NetFirewallRule -DisplayName \"Glebosaurus panel\" -Direction Inbound -Protocol TCP -LocalPort {web.Port} -Profile Private -RemoteAddress LocalSubnet -Action Allow");
        }
        else
        {
            Console.WriteLine($"Panel jest wyłączony – dodaj do konfiguracji sekcję  \"web\": {{ \"port\": {WebConfig.DefaultPort} }}  i uruchom monitor ponownie.");
        }
        return 0;
    }

    /// <summary>Czyta hasło bez wyświetlania znaków; ze strumienia (np. w skrypcie) – zwykły wiersz.</summary>
    private static async Task<string> ReadSecretAsync(string prompt, CancellationToken cancellationToken)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
        var text = new StringBuilder();
        while (true)
        {
            while (!Console.KeyAvailable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(30, cancellationToken);
            }
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (text.Length > 0) text.Length--;
            }
            else if (!char.IsControl(key.KeyChar))
            {
                text.Append(key.KeyChar);
            }
        }
        Console.WriteLine();
        return text.ToString();
    }

    // ---- praca ciągła --------------------------------------------------------------------------------------------

    private static async Task<int> RunMonitorAsync(CliOptions options, CancellationToken cancellationToken)
    {
        var loaded = AgentRuntime.LoadConfig(options.Config);
        using var loggers = AgentRuntime.CreateLogging(loaded.LogFiles, loaded.Config.Logs.RetentionDays, console: !options.Quiet);
        var logger = loggers.CreateLogger("Glebosaurus");
        logger.LogInformation("Konfiguracja: {Config}; baza: {Database}; dziennik: {Logs}; BLE: {Backend}.",
            loaded.ConfigPath, loaded.DatabasePath, loaded.LogDirectory, BleHost.Backend);
        logger.LogInformation("Dziennik: pliki dzienne, trzymane {Days} dni; log z poprzedniego dnia dla administratora: {Admin}.",
            loaded.Config.Logs.RetentionDays, loaded.Config.Mail is { AdminRecipients.Count: > 0 } ? "włączony" : "wyłączony");

        using var ble = new BleHost();
        await ble.EnsureAccessAsync();
        IMailGateway? mail = loaded.Config.Mail is { } m ? new GmailSender(m.GmailAddress, m.AppPassword) : null;
        if (mail is null) logger.LogWarning("Sekcja „mail” nie jest skonfigurowana – alerty i raporty będą tylko w dzienniku.");

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (options.Minutes is { } minutes) limit.CancelAfter(TimeSpan.FromMinutes(minutes));

        var store = new SqliteMonitorStore(loaded.DatabasePath);
        await store.InitializeAsync(limit.Token);
        var engine = new MonitorEngine(loaded.Config, store, new ListenThenReadSensorReader<SeenSensor>(new ShinySensorRadio(ble), TimeProvider.System, logger), mail, TimeProvider.System, logger, logs: loaded.LogFiles);
        // Panel zamyka się dopiero po czystym zatrzymaniu silnika (koniec bloku).
        await using var panel = await AgentWebPanel.TryStartAsync(loaded, engine, store, loggers, logger, limit.Token);
        await engine.RunAsync(limit.Token);
        return 0;
    }

    private static async Task<int> StatusAsync(CliOptions options, CancellationToken cancellationToken)
    {
        var message = await BuildReportAsync(options, cancellationToken);
        Console.WriteLine(message.TextBody);
        return 0;
    }

    private static async Task<int> ReportAsync(CliOptions options, CancellationToken cancellationToken)
    {
        var message = await BuildReportAsync(options, cancellationToken);
        if (options.Output is { } path)
        {
            await File.WriteAllTextAsync(Path.GetFullPath(path), message.HtmlBody, cancellationToken);
            Console.WriteLine($"Zapisano raport: {Path.GetFullPath(path)}");
        }
        else
        {
            Console.WriteLine(message.Subject);
            Console.WriteLine();
            Console.WriteLine(message.TextBody);
        }
        return 0;
    }

    private static async Task<MailMessage> BuildReportAsync(CliOptions options, CancellationToken cancellationToken)
    {
        var loaded = AgentRuntime.LoadConfig(options.Config);
        var store = new SqliteMonitorStore(loaded.DatabasePath);
        await store.InitializeAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var snapshots = await SnapshotBuilder.BuildAsync(loaded.Config, store, now, cancellationToken);
        var recipients = loaded.Config.Mail?.EffectiveReportRecipients ?? [];
        return MailTemplates.Report(snapshots, recipients, loaded.Config.MonitorName, now, TimeZoneInfo.Local);
    }

    private static async Task<int> MailTestAsync(CliOptions options, CancellationToken cancellationToken)
    {
        var loaded = AgentRuntime.LoadConfig(options.Config);
        if (loaded.Config.Mail is not { } mail)
        {
            Console.Error.WriteLine("W konfiguracji brakuje sekcji „mail”.");
            return 1;
        }
        var now = DateTimeOffset.UtcNow;
        var messages = new List<MailMessage>();
        if (options.Samples)
        {
            messages.AddRange(MailSamples.Build(mail.AlertRecipients, loaded.Config.MonitorName, now, TimeZoneInfo.Local));
            if (mail.AdminRecipients.Count > 0)
                messages.Add(MailSamples.AdminLog(mail.AdminRecipients, loaded.Config.MonitorName, now, TimeZoneInfo.Local));
        }
        else
        {
            messages.Add(MailTemplates.Test(mail.AlertRecipients, loaded.Config.MonitorName, now, TimeZoneInfo.Local));
        }
        var sender = new GmailSender(mail.GmailAddress, mail.AppPassword);
        foreach (var message in messages)
        {
            Console.WriteLine($"Wysyłam „{message.Subject}” z {mail.GmailAddress} do: {string.Join(", ", message.To)}...");
            try
            {
                await sender.SendAsync(message, cancellationToken);
            }
            catch (MailTransportException e)
            {
                Console.Error.WriteLine(e.Message);
                return 1;
            }
            Console.WriteLine("Wysłano.");
        }
        return 0;
    }

    /// <summary>Zapisuje przykładowe maile do plików, żeby obejrzeć je w przeglądarce (np. w widoku telefonu) bez wysyłki.</summary>
    private static async Task<int> MailPreviewAsync(CliOptions options, CancellationToken cancellationToken)
    {
        var directory = Path.GetFullPath(options.Output ?? "mail-preview");
        Directory.CreateDirectory(directory);
        string[] names = ["probna", "alert", "raport", "log", "raport-20"];
        var now = DateTimeOffset.UtcNow;
        var messages = MailSamples.Build(["ktos@example.com"], "Podgląd", now, TimeZoneInfo.Local)
            .Append(MailSamples.AdminLog(["admin@example.com"], "Podgląd", now, TimeZoneInfo.Local))
            .Append(MailSamples.LargeReport(["ktos@example.com"], "Podgląd", now, TimeZoneInfo.Local)).ToList();
        for (var i = 0; i < messages.Count; i++)
        {
            foreach (var attachment in messages[i].Attachments ?? [])
                await File.WriteAllBytesAsync(Path.Combine(directory, attachment.FileName), attachment.Content, cancellationToken);
            var html = Path.Combine(directory, names[i] + ".html");
            await File.WriteAllTextAsync(html, messages[i].HtmlBody, new UTF8Encoding(false), cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(directory, names[i] + ".txt"), messages[i].TextBody, new UTF8Encoding(false), cancellationToken);
            Console.WriteLine($"{html}  –  tytuł: {messages[i].Subject}");
        }
        return 0;
    }

    /// <summary>Wysyła administratorowi prawdziwy dziennik z danego dnia (na żądanie); nie dotyka znacznika automatycznej wysyłki.</summary>
    private static async Task<int> SendLogAsync(CliOptions options, CancellationToken cancellationToken)
    {
        var loaded = AgentRuntime.LoadConfig(options.Config);
        if (loaded.Config.Mail is not { AdminRecipients.Count: > 0 } mail)
        {
            Console.Error.WriteLine("W konfiguracji brakuje mail.adminRecipients (adres administratora).");
            return 1;
        }

        var now = DateTimeOffset.UtcNow;
        DateOnly day;
        if (options.Date is null) day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local).DateTime).AddDays(-1);
        else if (!DateOnly.TryParseExact(options.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day))
            throw new ArgumentException("--date: podaj dzień w formacie RRRR-MM-DD.");

        var text = await loaded.LogFiles.ReadDayAsync(day, cancellationToken);
        if (text is null)
        {
            Console.Error.WriteLine($"Brak dziennika z {day:yyyy-MM-dd} w {loaded.LogDirectory}.");
            return 2;
        }

        var message = MailTemplates.AdminLog(text, day, mail.AdminRecipients, loaded.Config.MonitorName, now, TimeZoneInfo.Local);
        Console.WriteLine($"Wysyłam „{message.Subject}” z {mail.GmailAddress} do: {string.Join(", ", message.To)} (załącznik {message.Attachments![0].FileName})...");
        try
        {
            await new GmailSender(mail.GmailAddress, mail.AppPassword).SendAsync(message, cancellationToken);
        }
        catch (MailTransportException e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
        Console.WriteLine("Wysłano.");
        return 0;
    }

    // ---- diagnostyka ---------------------------------------------------------------------------------------------

    private static async Task<int> ScanAsync(CliOptions options, CancellationToken cancellationToken)
    {
        var seconds = options.Seconds ?? 20;
        using var ble = new BleHost();
        await ble.EnsureAccessAsync();

        Console.WriteLine($"Skanuję {seconds} s ({BleHost.Backend})...");
        var seen = await BleScanner.ScanAsync(ble, TimeSpan.FromSeconds(seconds), null, cancellationToken);

        var tuya = seen.Where(s => s.IsTuyaLike).OrderByDescending(s => s.Signal.Average ?? int.MinValue).ToArray();
        Console.WriteLine($"Urządzeń BLE: {seen.Count}, w tym Tuya: {tuya.Length}.");
        foreach (var sensor in tuya) Console.WriteLine("  " + sensor.Describe(options.Verbose));
        return 0;
    }

    private static async Task<int> ReadAsync(CliOptions options, CancellationToken cancellationToken)
    {
        if (options.Credentials is null) throw new ArgumentException("Podaj plik klucza: --credentials PLIK.");
        var credentials = TuyaCredentials.Load(options.Credentials);
        var sensorConfig = new SensorConfig { Id = "read", Name = "czujnik", Tuya = credentials };
        var profile = SoilSensorProfiles.ForProduct(credentials.ProductId)
            ?? throw new InvalidOperationException($"Brak profilu odczytu dla produktu „{credentials.ProductId ?? "?"}”.");
        Console.WriteLine($"Czujnik z pliku klucza: {credentials.Mac}, UUID Tuya {credentials.Uuid}, produkt {credentials.ProductId}.");

        using var ble = new BleHost();
        await ble.EnsureAccessAsync();
        if (await FindSensorAsync(ble, credentials, options.Seconds ?? 60, cancellationToken) is not { } target) return 2;

        Console.WriteLine("Znaleziono czujnik. Łączę się...");
        var peripheral = target.Peripheral;
        try
        {
            await peripheral.ConnectAsync(new Shiny.BluetoothLE.ConnectionConfig(false), cancellationToken, TimeSpan.FromSeconds(20));
            Console.WriteLine($"Połączono (MTU danych: {peripheral.Mtu}).");
            // Czytamy do ciszy, nie do pierwszego kompletu: czujnik oddaje najpierw historię (od najstarszego), a stan z chwili
            // połączenia jest w ostatnim rekordzie. Odebrane rekordy czujnik kasuje, więc ten odczyt je wypisuje (nie zapisuje w bazie).
            var connectedAt = DateTimeOffset.Now;
            var session = new TuyaSession(new ShinyGattLink(peripheral), credentials, TimeSpan.FromSeconds(options.Timeout), Console.WriteLine,
                sessionDeadline: TimeSpan.FromMinutes(3), quietTimeout: TimeSpan.FromSeconds(3));
            var result = await session.ReadAsync(cancellationToken);

            var readings = SoilSensorProfiles.MapAll(profile, result.DataPoints, connectedAt);
            Console.WriteLine();
            Console.WriteLine($"Protokół Tuya {result.Protocol}, firmware {result.Firmware}, profil {profile}.");
            if (readings.Count > 1)
            {
                Console.WriteLine($"Zaległe pomiary z czujnika ({readings.Count - 1}), od najstarszego:");
                foreach (var old in readings.SkipLast(1)) Console.WriteLine("  " + Describe(old));
            }
            if (readings.LastOrDefault() is { } reading)
            {
                Console.WriteLine($"Stan najnowszy ({reading.At.ToLocalTime():yyyy-MM-dd HH:mm:ss}):");
                Console.WriteLine($"  Wilgotność gleby : {Show(reading.MoisturePercent, "%")}");
                Console.WriteLine($"  Temperatura gleby: {Show(reading.SoilTemperatureCelsius, "°C")}");
                Console.WriteLine($"  Bateria          : {Show(reading.BatteryPercent, "%")} (stan DP14: {Show(reading.BatteryState, "")})");
            }
            foreach (var note in result.Notes) Console.WriteLine($"  Uwaga: {note}");
            return readings.Count == 0 ? 3 : 0;
        }
        catch (TuyaUnboundException e)
        {
            Console.Error.WriteLine(e.Message);
            return 3;
        }
        finally
        {
            await ShinySensorRadio.DisconnectQuietlyAsync(peripheral);
        }
    }

    /// <summary>Nasłuchuje reklam, aż pojawi się czujnik z pliku klucza; null (z komunikatem), gdy go nie ma.</summary>
    private static async Task<SeenSensor?> FindSensorAsync(BleHost ble, TuyaCredentials credentials, int seconds, CancellationToken cancellationToken)
    {
        Console.WriteLine($"Szukam czujnika (do {seconds} s, {BleHost.Backend})...");
        SeenSensor? target = null;
        await BleScanner.ScanAsync(ble, TimeSpan.FromSeconds(seconds), sensor =>
        {
            if (!sensor.Matches(credentials)) return false;
            target = sensor;
            return true;
        }, cancellationToken);
        if (target is null)
            Console.Error.WriteLine("Nie znaleziono czujnika w reklamach. Zbliż komputer, sprawdź baterie i czy nic innego (telefon) nie trzyma połączenia.");
        return target;
    }

    /// <summary>
    /// Diagnostyka zaległych pomiarów: zostaje połączony po zwykłym raporcie i wypisuje wszystko, co czujnik jeszcze wyśle
    /// (ramki DP z czasem 0x8003, podpisane raporty, nieznane komendy). Niczego poza zwykłym odczytem do czujnika nie wysyła.
    /// </summary>
    private static async Task<int> ListenAsync(CliOptions options, CancellationToken cancellationToken)
    {
        if (options.Credentials is null) throw new ArgumentException("Podaj plik klucza: --credentials PLIK.");
        var credentials = TuyaCredentials.Load(options.Credentials);
        var linger = TimeSpan.FromSeconds(options.Linger ?? 20);
        Console.WriteLine($"Czujnik z pliku klucza: {credentials.Mac}, UUID Tuya {credentials.Uuid}, produkt {credentials.ProductId}.");

        using var ble = new BleHost();
        await ble.EnsureAccessAsync();
        if (await FindSensorAsync(ble, credentials, options.Seconds ?? 60, cancellationToken) is not { } target) return 2;

        using var file = options.Output is { } path ? new StreamWriter(Path.GetFullPath(path), append: true, new UTF8Encoding(false)) { AutoFlush = true } : null;
        var started = Stopwatch.StartNew();
        void Emit(string line)
        {
            var text = $"+{started.Elapsed.TotalSeconds,6:0.0} s  {line}";
            Console.WriteLine(text);
            file?.WriteLine(text);
        }

        var frames = new SortedDictionary<ushort, int>();
        file?.WriteLine($"# listen {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}, czujnik {credentials.Mac}, cisza kończąca nasłuch {linger.TotalSeconds:0} s");
        Console.WriteLine("Znaleziono czujnik. Łączę się...");
        var peripheral = target.Peripheral;
        try
        {
            await peripheral.ConnectAsync(new Shiny.BluetoothLE.ConnectionConfig(false), cancellationToken, TimeSpan.FromSeconds(20));
            started.Restart();
            Emit($"Połączono (MTU danych: {peripheral.Mtu}). Nasłuch kończy cisza {linger.TotalSeconds:0} s albo limit 5 min.");
            var session = new TuyaSession(new ShinyGattLink(peripheral), credentials, TimeSpan.FromSeconds(options.Timeout), Emit,
                sessionDeadline: TimeSpan.FromMinutes(5), quietTimeout: linger, onFrame: frame =>
                {
                    frames[frame.Command] = frames.GetValueOrDefault(frame.Command) + 1;
                    Emit($"ramka 0x{frame.Command:X4}, seq {frame.Sequence}, odp. na {frame.ResponseTo}, {frame.Data.Length} B: {Convert.ToHexString(frame.Data)}");
                });
            var result = await session.ReadAsync(cancellationToken);

            Emit($"Koniec nasłuchu. Protokół Tuya {result.Protocol}, firmware {result.Firmware}.");
            foreach (var note in result.Notes) Emit($"Uwaga: {note}");
        }
        catch (TuyaUnboundException e)
        {
            Console.Error.WriteLine(e.Message);
            return 3;
        }
        catch (TimeoutException e)
        {
            // Limit sesji przy wciąż nadających ramkach nie jest błędem diagnostyki – ramki są już wypisane.
            Emit($"Przerwano: {e.Message}");
        }
        finally
        {
            await ShinySensorRadio.DisconnectQuietlyAsync(peripheral);
        }

        Emit(frames.Count == 0
            ? "Podsumowanie: czujnik nie wysłał żadnej ramki."
            : "Podsumowanie ramek: " + string.Join(", ", frames.Select(f => $"0x{f.Key:X4} ×{f.Value}")) + ".");
        return frames.Count == 0 ? 3 : 0;
    }

    private static string Describe(SoilReading reading) =>
        $"{reading.At.ToLocalTime():yyyy-MM-dd HH:mm:ss}  wilgotność {Show(reading.MoisturePercent, "%")}, temperatura {Show(reading.SoilTemperatureCelsius, "°C")}, bateria {Show(reading.BatteryPercent, "%")}";

    private static string Show<T>(T? value, string unit) where T : struct =>
        value is null ? "brak" : $"{Convert.ToString(value, CultureInfo.InvariantCulture)} {unit}".TrimEnd();
}
