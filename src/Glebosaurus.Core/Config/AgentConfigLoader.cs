using System.Net.Mail;
using System.Text.Json;
using System.Text.RegularExpressions;
using Glebosaurus.Core.Reports;
using Glebosaurus.Tuya;

namespace Glebosaurus.Core.Config;

public static partial class AgentConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true
    };

    public static AgentConfig Load(string path)
    {
        AgentConfig config;
        try
        {
            config = JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException("Pusty plik konfiguracji.");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"Nieprawidłowy JSON w {path}: {e.Message}", e);
        }
        ResolveTuyaFiles(config, Path.GetDirectoryName(Path.GetFullPath(path))!);
        Validate(config);
        return config;
    }

    /// <summary>Wczytuje dane przypisania z plików wskazanych przez <c>tuyaFile</c> (ścieżki względne od katalogu konfiguracji).</summary>
    private static void ResolveTuyaFiles(AgentConfig config, string configDirectory)
    {
        foreach (var sensor in config.Sensors.Where(s => !string.IsNullOrWhiteSpace(s.TuyaFile)))
        {
            var label = string.IsNullOrWhiteSpace(sensor.Id) ? "(bez id)" : sensor.Id;
            if (sensor.Tuya.Uuid.Length > 0 || sensor.Tuya.LocalKeyHex is not null || sensor.Tuya.LocalKey is not null)
                throw new InvalidDataException($"Czujnik {label}: podaj albo tuya, albo tuyaFile – nie oba naraz.");
            var file = Path.GetFullPath(Path.Combine(configDirectory, sensor.TuyaFile!));
            if (!File.Exists(file)) throw new InvalidDataException($"Czujnik {label}: nie znaleziono pliku tuyaFile {file}.");
            try { sensor.Tuya = TuyaCredentials.Load(file); }
            catch (Exception e) when (e is InvalidDataException or System.Text.Json.JsonException)
            {
                throw new InvalidDataException($"Czujnik {label}: plik {file}: {e.Message}", e);
            }
        }
    }

    public static string ResolveDatabasePath(AgentConfig config, string configPath) =>
        Path.GetFullPath(Path.IsPathRooted(config.Database)
            ? config.Database
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath))!, config.Database));

    public static void Validate(AgentConfig config)
    {
        if (config.ReadIntervalMinutes < 0.5) throw new InvalidDataException("readIntervalMinutes: minimum 0,5.");
        if (config.ScanWindowSeconds is < 5 or > 120) throw new InvalidDataException("scanWindowSeconds: od 5 do 120.");
        if (config.RetentionDays < 1) throw new InvalidDataException("retentionDays: minimum 1.");
        if (config.Cloud is { } cloud) ValidateCloud(config, cloud);
        else if (config.Sensors.Count == 0) throw new InvalidDataException("Konfiguracja nie zawiera żadnego czujnika (sensors).");
        if (config.Alerts.ReminderHours <= 0) throw new InvalidDataException("alerts.reminderHours: musi być dodatnie.");
        if (config.Logs.RetentionDays is < 1 or > 365) throw new InvalidDataException("logs.retentionDays: od 1 do 365.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var uuids = new HashSet<string>(StringComparer.Ordinal);
        var macs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sensor in config.Sensors)
        {
            var label = string.IsNullOrWhiteSpace(sensor.Id) ? "(bez id)" : sensor.Id;
            if (!SensorIdPattern().IsMatch(sensor.Id))
                throw new InvalidDataException($"Czujnik {label}: id może zawierać tylko litery, cyfry, '.', '_' i '-'.");
            if (!ids.Add(sensor.Id)) throw new InvalidDataException($"Czujnik {label}: id występuje więcej niż raz.");
            if (string.IsNullOrWhiteSpace(sensor.Name)) sensor.Name = sensor.Id;
            if (sensor.Tuya.Uuid.Length == 0)
                throw new InvalidDataException($"Czujnik {label}: brak danych przypisania – podaj tuya albo tuyaFile (zob. polecenie pair).");
            try { sensor.Tuya.Validate(); }
            catch (InvalidDataException e) { throw new InvalidDataException($"Czujnik {label}: tuya: {e.Message}", e); }
            if (!uuids.Add(sensor.Tuya.Uuid)) throw new InvalidDataException($"Czujnik {label}: ten sam UUID Tuya co inny czujnik.");
            if (sensor.Tuya.Mac.Length > 0 && !macs.Add(BleAddress.Format(BleAddress.Parse(sensor.Tuya.Mac))))
                throw new InvalidDataException($"Czujnik {label}: ten sam adres MAC co inny czujnik.");
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

        foreach (var time in config.Reports.Times)
            if (!ReportSchedule.TryParseTime(time, out _)) throw new InvalidDataException($"reports.times: „{time}” nie jest godziną HH:mm.");
        if (config.Reports.CatchUpHours is < 0 or > 24) throw new InvalidDataException("reports.catchUpHours: od 0 do 24.");

        if (config.Mail is { } mail)
        {
            mail.GmailAddress = mail.GmailAddress.Trim();
            if (!IsEmailAddress(mail.GmailAddress)) throw new InvalidDataException("mail.gmailAddress: podaj adres konta Gmail.");
            if (string.IsNullOrWhiteSpace(mail.AppPassword)) throw new InvalidDataException("mail.appPassword: podaj hasło aplikacji Gmail.");
            mail.AlertRecipients = NormalizeRecipients(mail.AlertRecipients, "mail.alertRecipients");
            mail.ReportRecipients = NormalizeRecipients(mail.ReportRecipients, "mail.reportRecipients");
            mail.AdminRecipients = NormalizeRecipients(mail.AdminRecipients, "mail.adminRecipients");
            if (mail.AlertRecipients.Count == 0) throw new InvalidDataException("mail.alertRecipients: podaj co najmniej jeden adres.");
        }

        if (config.Web is { } web)
        {
            if (web.Port is < 1024 or > 65535) throw new InvalidDataException("web.port: od 1024 do 65535.");
            if (web.SessionHours is < 0.25 or > 720) throw new InvalidDataException("web.sessionHours: od 0,25 do 720.");
            web.ParseAllowedNetworks();
        }
    }

    /// <summary>
    /// Tryb chmurowy: w pliku tylko dostęp do chmury i ustawienia urządzenia. Poczta i czujniki z pliku byłyby po cichu
    /// pomijane – lepiej zatrzymać start z wyjaśnieniem (do chmury przenosi je <c>cloud import</c>).
    /// </summary>
    private static void ValidateCloud(AgentConfig config, CloudAccessConfig cloud)
    {
        if (config.Mail is not null)
            throw new InvalidDataException("Sekcja „cloud” i „mail” naraz: w trybie chmurowym poczta (konto, hasło, odbiorcy) jest w chmurze – usuń sekcję „mail” z pliku.");
        if (config.Sensors.Count > 0)
            throw new InvalidDataException("Sekcja „cloud” i lista „sensors” naraz: w trybie chmurowym czujniki są w chmurze – usuń „sensors” z pliku (przenosi je cloud import).");
        if (!Uri.TryCreate(cloud.Url.Trim().TrimEnd('/'), UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps
            || !SupabaseHost().IsMatch(url.Host) || url.AbsolutePath != "/")
            throw new InvalidDataException("cloud.url: podaj adres projektu Supabase, np. https://abcdefghijklmnopqrst.supabase.co.");
        if (!cloud.PublishableKey.StartsWith("sb_publishable_", StringComparison.Ordinal))
            throw new InvalidDataException("cloud.publishableKey: podaj klucz publikowalny projektu (sb_publishable_…), nie klucz tajny.");
        if (string.IsNullOrWhiteSpace(cloud.TokenFile) || string.IsNullOrWhiteSpace(cloud.ConfigCacheFile))
            throw new InvalidDataException("cloud.tokenFile i cloud.configCacheFile nie mogą być puste.");
    }

    public static string ResolveRelative(string configPath, string file) =>
        Path.GetFullPath(Path.IsPathRooted(file) ? file : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath))!, file));

    [GeneratedRegex("^[a-z0-9]{20}\\.supabase\\.co$")]
    private static partial Regex SupabaseHost();

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

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex SensorIdPattern();
}
