using System.Net.Mail;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Glebosaurus.Core.Cloud;
using Glebosaurus.Core.Reports;
using Glebosaurus.Tuya;

namespace Glebosaurus.Core.Config;

/// <summary>
/// Plik konfiguracji chmury, który trzyma właściciel (np. <c>cloud-config.json</c> obok pliku administratora): ustawienia
/// wspólne dla wszystkich agentów, czujniki z kluczami Tuya (zwykle w plikach z <c>pair</c>) i poczta z hasłem aplikacji Gmail.
/// Polecenie <c>cloud config</c> zapisuje go w chmurze; agenci przejmują zmiany sami. Plik zawiera sekrety.
/// </summary>
public sealed class CloudSetup
{
    /// <summary>Strefa czasowa raportów (nazwa IANA, np. Europe/Warsaw); null = strefa komputera, z którego idzie <c>cloud config</c>.</summary>
    public string? TimeZone { get; set; }

    /// <summary>Domyślny odstęp odczytów czujnika w minutach (min. 0,5) – patrz <see cref="AgentConfig.ReadIntervalMinutes"/>.</summary>
    public double ReadIntervalMinutes { get; set; } = AgentConfig.DefaultReadIntervalMinutes;

    /// <summary>Łączny czas nasłuchu reklam w poszukiwaniu czujników czekających na odczyt (sekundy, 5–120).</summary>
    public int ScanWindowSeconds { get; set; } = 60;

    public List<SensorConfig> Sensors { get; set; } = [];

    /// <summary>Konto nadawcy i odbiorcy; null = poczty nie zmieniamy (bez niej w chmurze maile nie wychodzą).</summary>
    public MailConfig? Mail { get; set; }

    public ReportsConfig Reports { get; set; } = new();
    public AlertsConfig Alerts { get; set; } = new();

    /// <summary>Ile dni chmura trzyma dzienniki agentów (1–30).</summary>
    public LogsConfig Logs { get; set; } = new();
}

public static partial class CloudSetupLoader
{
    public const string DefaultFile = "cloud-config.json";

    /// <summary>W chmurze identyfikator czujnika jest kluczem w tabeli i w poleceniach – stąd węższy zapis.</summary>
    public static bool IsValidSensorId(string id) => SensorIdPattern().IsMatch(id);

    public const string SensorIdRule = "małe litery, cyfry, „_” i „-” (do 40 znaków, na początku litera albo cyfra)";

    public static CloudSetup Load(string path)
    {
        CloudSetup setup;
        try
        {
            var text = File.ReadAllText(path);
            if (JsonNode.Parse(text, documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) is JsonObject root
                && root.Any(p => string.Equals(p.Key, "cloud", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("To plik agenta (ma sekcję „cloud”), a nie plik konfiguracji chmury – wzór: examples/cloud-config.json.");
            setup = JsonSerializer.Deserialize<CloudSetup>(text, AgentConfigLoader.JsonOptions)
                ?? throw new InvalidDataException("Pusty plik konfiguracji chmury.");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"Nieprawidłowy JSON w {path}: {e.Message}", e);
        }
        ResolveTuyaFiles(setup, Path.GetDirectoryName(Path.GetFullPath(path))!);
        Validate(setup);
        return setup;
    }

    /// <summary>Wczytuje dane przypisania z plików wskazanych przez <c>tuyaFile</c> (ścieżki względne od katalogu pliku).</summary>
    private static void ResolveTuyaFiles(CloudSetup setup, string directory)
    {
        foreach (var sensor in setup.Sensors.Where(s => !string.IsNullOrWhiteSpace(s.TuyaFile)))
        {
            var label = string.IsNullOrWhiteSpace(sensor.Id) ? "(bez id)" : sensor.Id;
            if (sensor.Tuya.Uuid.Length > 0 || sensor.Tuya.LocalKeyHex is not null || sensor.Tuya.LocalKey is not null)
                throw new InvalidDataException($"Czujnik {label}: podaj albo tuya, albo tuyaFile – nie oba naraz.");
            var file = Path.GetFullPath(Path.Combine(directory, sensor.TuyaFile!));
            if (!File.Exists(file)) throw new InvalidDataException($"Czujnik {label}: nie znaleziono pliku tuyaFile {file}.");
            try { sensor.Tuya = TuyaCredentials.Load(file); }
            catch (Exception e) when (e is InvalidDataException or JsonException)
            {
                throw new InvalidDataException($"Czujnik {label}: plik {file}: {e.Message}", e);
            }
        }
    }

    public static void Validate(CloudSetup setup)
    {
        if (setup.TimeZone is { } zone)
        {
            TimeZones.Resolve(zone, out var known);
            if (!known) throw new InvalidDataException($"timeZone: strefa „{zone}” jest nieznana – podaj nazwę IANA, np. Europe/Warsaw.");
        }
        if (setup.ReadIntervalMinutes < 0.5) throw new InvalidDataException("readIntervalMinutes: minimum 0,5.");
        if (setup.ScanWindowSeconds is < 5 or > 120) throw new InvalidDataException("scanWindowSeconds: od 5 do 120.");
        if (setup.Alerts.ReminderHours <= 0) throw new InvalidDataException("alerts.reminderHours: musi być dodatnie.");
        if (setup.Logs.RetentionDays is < 1 or > 30) throw new InvalidDataException("logs.retentionDays: od 1 do 30 (dzienniki agentów leżą w bazie w chmurze).");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var uuids = new HashSet<string>(StringComparer.Ordinal);
        var macs = new HashSet<ulong>();
        foreach (var sensor in setup.Sensors)
        {
            var label = string.IsNullOrWhiteSpace(sensor.Id) ? "(bez id)" : sensor.Id;
            if (!IsValidSensorId(sensor.Id)) throw new InvalidDataException($"Czujnik {label}: id – {SensorIdRule}.");
            if (!ids.Add(sensor.Id)) throw new InvalidDataException($"Czujnik {label}: id występuje więcej niż raz.");
            if (string.IsNullOrWhiteSpace(sensor.Name)) sensor.Name = sensor.Id;
            if (sensor.Name.Length > 80) throw new InvalidDataException($"Czujnik {label}: nazwa dłuższa niż 80 znaków.");
            if (sensor.Tuya.Uuid.Length == 0)
                throw new InvalidDataException($"Czujnik {label}: brak danych przypisania – podaj tuya albo tuyaFile (zob. polecenie pair).");
            try { sensor.Tuya.Validate(); }
            catch (InvalidDataException e) { throw new InvalidDataException($"Czujnik {label}: tuya: {e.Message}", e); }
            if (!BleAddress.TryParse(sensor.Tuya.Mac, out var mac))
                throw new InvalidDataException($"Czujnik {label}: brak adresu MAC w danych Tuya – chmura rozpoznaje czujnik po MAC (dopisz „mac”, adres podaje glebosaurus scan).");
            sensor.Tuya.Mac = BleAddress.Format(mac);
            if (!uuids.Add(sensor.Tuya.Uuid)) throw new InvalidDataException($"Czujnik {label}: ten sam UUID Tuya co inny czujnik.");
            if (!macs.Add(mac)) throw new InvalidDataException($"Czujnik {label}: ten sam adres MAC co inny czujnik.");
            if (SoilSensorProfiles.ForProduct(sensor.Tuya.ProductId) is null)
                throw new InvalidDataException($"Czujnik {label}: nieznany productId „{sensor.Tuya.ProductId}”.");
            if (sensor.ReadIntervalMinutes is < 0.5) throw new InvalidDataException($"Czujnik {label}: readIntervalMinutes: minimum 0,5.");
            if (sensor.WateringBelowPercent is < 0 or > 100 || sensor.WateringClearAbovePercent is < 0 or > 100)
                throw new InvalidDataException($"Czujnik {label}: progi wilgotności muszą mieścić się w 0–100.");
            if (sensor.WateringClearAbovePercent <= sensor.WateringBelowPercent)
                throw new InvalidDataException($"Czujnik {label}: wateringClearAbovePercent musi być większy niż wateringBelowPercent.");
            if (sensor.ConsecutiveReadings is < 1 or > 10) throw new InvalidDataException($"Czujnik {label}: consecutiveReadings: od 1 do 10.");
            if (sensor.LowBatteryPercent is < 0 or > 100) throw new InvalidDataException($"Czujnik {label}: lowBatteryPercent: od 0 do 100.");
            if (sensor.SilentAfterHours <= 0) throw new InvalidDataException($"Czujnik {label}: silentAfterHours: musi być dodatnie.");
        }

        foreach (var time in setup.Reports.Times)
            if (!ReportSchedule.TryParseTime(time, out _)) throw new InvalidDataException($"reports.times: „{time}” nie jest godziną HH:mm.");
        if (setup.Reports.CatchUpHours is < 0 or > 24) throw new InvalidDataException("reports.catchUpHours: od 0 do 24.");

        if (setup.Mail is { } mail)
        {
            mail.GmailAddress = mail.GmailAddress.Trim();
            if (!IsEmailAddress(mail.GmailAddress)) throw new InvalidDataException("mail.gmailAddress: podaj adres konta Gmail.");
            if (string.IsNullOrWhiteSpace(mail.AppPassword)) throw new InvalidDataException("mail.appPassword: podaj hasło aplikacji Gmail.");
            mail.AlertRecipients = NormalizeRecipients(mail.AlertRecipients, "mail.alertRecipients");
            mail.ReportRecipients = NormalizeRecipients(mail.ReportRecipients, "mail.reportRecipients");
            mail.AdminRecipients = NormalizeRecipients(mail.AdminRecipients, "mail.adminRecipients");
            if (mail.AlertRecipients.Count == 0) throw new InvalidDataException("mail.alertRecipients: podaj co najmniej jeden adres.");
        }
    }

    /// <summary>Obcina spacje, odrzuca coś, co nie jest zwykłym adresem e-mail, i usuwa powtórzenia (wielkość liter nie ma znaczenia).</summary>
    private static List<string> NormalizeRecipients(List<string> recipients, string setting)
    {
        var result = new List<string>();
        foreach (var raw in recipients)
        {
            var address = raw.Trim();
            if (!IsEmailAddress(address)) throw new InvalidDataException($"{setting}: „{raw}” nie wygląda jak adres e-mail (podaj sam adres, np. ktos@example.com).");
            if (!result.Contains(address, StringComparer.OrdinalIgnoreCase)) result.Add(address);
        }
        return result;
    }

    /// <summary>Sam adres (bez „Imię &lt;adres&gt;”), z domeną zawierającą kropkę.</summary>
    private static bool IsEmailAddress(string text) =>
        MailAddress.TryCreate(text, out var parsed)
        && string.Equals(parsed.Address, text, StringComparison.OrdinalIgnoreCase)
        && parsed.Host.Contains('.');

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,39}$")]
    private static partial Regex SensorIdPattern();
}
