using Glebosaurus.Core.Config;
using Glebosaurus.Tuya;

namespace Glebosaurus.Core.Cloud;

/// <summary>
/// Konfiguracja agenta: <see cref="AgentConfig"/> dla silników (czujniki, harmonogramy, progi z chmury,
/// a baza, retencja i panel – lokalnie), strefa czasowa raportów, rola agenta i odbiorcy poczty (bez hasła – to agent
/// dostaje tylko z blokadą nadawcy, <c>mail_credentials</c>).
/// </summary>
/// <param name="CachedAt">Gdy konfiguracja pochodzi z lokalnej kopii (chmura niedostępna) – kiedy ją zapisano; null = świeża z chmury.</param>
public sealed record CloudAgentConfig(AgentConfig Config, TimeZoneInfo TimeZone, long Version, AgentIdentity Agent,
    CloudMailSettings? Mail, IReadOnlyList<string> Warnings, DateTimeOffset? CachedAt = null)
{
    public bool FromCache => CachedAt is not null;

    /// <summary>
    /// Kody czujników do zestawienia w raporcie (<see cref="Glebosaurus.Core.Config.SensorCodes"/>), liczone ze wszystkich czujników spoza archiwum –
    /// także wyłączonych i pominiętych przy mapowaniu – żeby były te same co na pulpicie strony w chmurze.
    /// </summary>
    public IReadOnlyDictionary<string, string> SensorCodes { get; init; } = new Dictionary<string, string>();
}

public static class CloudConfigMapper
{
    /// <summary>
    /// Składa konfigurację z chmury i lokalnych ustawień urządzenia (<paramref name="local"/>: <c>database</c>,
    /// <c>retentionDays</c>, <c>web</c>). Czujnik Flower Care potrzebuje tylko adresu MAC. Czujnik wyłączony, Tuya bez danych
    /// Tuya, z błędnymi danymi albo nieznanego rodzaju (nowszy niż agent) jest
    /// pomijany z ostrzeżeniem – jeden zły wpis w chmurze nie zatrzymuje agenta. Nieznana strefa czasowa → strefa systemu
    /// z ostrzeżeniem. Inna wersja protokołu → <see cref="InvalidDataException"/>.
    /// </summary>
    public static CloudAgentConfig Map(CloudConfig cloud, AgentConfig local, DateTimeOffset? cachedAt = null)
    {
        if (cloud.Protocol != CloudProtocol.Version)
            throw new InvalidDataException($"Konfiguracja z chmury ma protokół {cloud.Protocol}, a agent obsługuje {CloudProtocol.Version} – zaktualizuj agenta.");

        var warnings = new List<string>();
        var sensors = new List<SensorConfig>();
        var uuids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sensor in cloud.Sensors.Where(s => s.Enabled))
        {
            if (Sensor(sensor, uuids, out var problem) is { } mapped) sensors.Add(mapped);
            else warnings.Add($"Czujnik {sensor.Id} ({sensor.Name}) pominięty: {problem}.");
        }

        var settings = cloud.Settings;
        var zone = TimeZones.Resolve(settings.TimeZone, out var exact);
        if (!exact) warnings.Add($"Strefa czasowa „{settings.TimeZone}” nieznana w tym systemie – raporty według strefy systemu ({zone.Id}).");
        var reportTimes = settings.ReportTimes.Where(t => Reports.ReportSchedule.TryParseTime(t, out _)).ToList();
        if (reportTimes.Count != settings.ReportTimes.Count) warnings.Add("Pominięto pory raportów w złym formacie (wymagane HH:mm).");

        var config = new AgentConfig
        {
            MonitorName = cloud.Agent.Name,
            ReadIntervalMinutes = Math.Max(0.5, settings.ReadIntervalMinutes),
            ScanWindowSeconds = Math.Clamp(settings.ScanWindowSeconds, 5, 600),
            Database = local.Database,
            RetentionDays = local.RetentionDays,
            Sensors = sensors,
            Reports = new() { Times = reportTimes, CatchUpHours = Math.Clamp(settings.ReportCatchUpHours, 0, 24) },
            Alerts = new() { ReminderHours = settings.AlertReminderHours > 0 ? settings.AlertReminderHours : 24, MutedUntil = settings.AlertsMutedUntil },
            Logs = new() { RetentionDays = Math.Clamp(settings.LogRetentionDays, 1, 365) },
            Web = local.Web,
            Bluetooth = local.Bluetooth
        };
        return new(config, zone, cloud.ConfigVersion, cloud.Agent, cloud.Agent.CanSendMail ? cloud.Mail : null, warnings, cachedAt)
        {
            SensorCodes = Glebosaurus.Core.Config.SensorCodes.For(cloud.Sensors.Select(s => (s.Id, s.Name)))
        };
    }

    private static SensorConfig? Sensor(CloudSensor sensor, HashSet<string> uuids, out string problem)
    {
        problem = "";
        var model = SensorModels.Parse(sensor.Model);
        if (model is null)
        {
            problem = $"nieznany rodzaj czujnika „{sensor.Model}” – zaktualizuj agenta";
            return null;
        }
        if (model == SensorModel.FlowerCare)
        {
            if (BleAddress.TryParse(sensor.Mac, out var address))
                return Settings(sensor, new() { Model = SensorModel.FlowerCare, Mac = BleAddress.Format(address) });
            problem = "zły adres MAC";
            return null;
        }
        if (sensor.Tuya is not { } tuya)
        {
            problem = "brak danych Tuya w chmurze (sparuj czujnik albo zaimportuj klucze)";
            return null;
        }
        var credentials = new TuyaCredentials
        {
            Mac = tuya.Mac.Length > 0 ? tuya.Mac : sensor.Mac, Uuid = tuya.Uuid, DeviceId = tuya.DeviceId, ProductId = tuya.ProductId,
            LocalKey = tuya.LocalKey, LocalKeyHex = tuya.LocalKeyHex, BindingAttempted = tuya.BindingAttempted, Verified = tuya.Verified
        };
        try { credentials.Validate(); }
        catch (InvalidDataException e)
        {
            problem = "dane Tuya: " + e.Message;
            return null;
        }
        if (!BleAddress.TryParse(credentials.Mac, out var mac) || !BleAddress.TryParse(sensor.Mac, out var expected) || mac != expected)
        {
            problem = "MAC w danych Tuya nie zgadza się z czujnikiem";
            return null;
        }
        if (SoilSensorProfiles.ForProduct(credentials.ProductId) is null)
        {
            problem = $"nieznany productId „{credentials.ProductId}”";
            return null;
        }
        if (!uuids.Add(credentials.Uuid))
        {
            problem = "ten sam UUID Tuya co inny czujnik";
            return null;
        }
        return Settings(sensor, new() { Tuya = credentials });
    }

    /// <summary>Wspólne ustawienia czujnika z chmury (nazwa, odstęp, progi, wyciszenie).</summary>
    private static SensorConfig Settings(CloudSensor sensor, SensorConfig config)
    {
        config.Id = sensor.Id;
        config.Name = sensor.Name;
        config.ReadIntervalMinutes = sensor.ReadIntervalMinutes;
        config.WateringBelowPercent = sensor.WateringBelowPercent;
        config.WateringClearAbovePercent = sensor.WateringClearAbovePercent;
        config.ConsecutiveReadings = sensor.ConsecutiveReadings;
        config.LowBatteryPercent = sensor.LowBatteryPercent;
        config.SilentAfterHours = sensor.SilentAfterHours;
        config.AlertsMutedUntil = sensor.AlertsMutedUntil;
        return config;
    }
}

/// <summary>
/// Strefa czasowa po nazwie IANA (jak w chmurze, np. Europe/Warsaw). Agent działa z <c>InvariantGlobalization</c>, a wtedy
/// Windows nie zamienia nazw IANA (potrzebuje ICU) – stąd zapasowa mapa najczęstszych stref na nazwy Windows.
/// </summary>
public static class TimeZones
{
    private static readonly Dictionary<string, string> WindowsIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Europe/Warsaw"] = "Central European Standard Time",
        ["Europe/Prague"] = "Central Europe Standard Time",
        ["Europe/Berlin"] = "W. Europe Standard Time",
        ["Europe/Paris"] = "Romance Standard Time",
        ["Europe/London"] = "GMT Standard Time",
        ["Europe/Kyiv"] = "FLE Standard Time",
        ["UTC"] = "UTC",
        ["Etc/UTC"] = "UTC"
    };

    /// <param name="exact">false = nazwy nie udało się rozpoznać i zwrócono strefę systemu.</param>
    public static TimeZoneInfo Resolve(string id, out bool exact)
    {
        exact = true;
        if (TryFind(id) is { } zone) return zone;
        if (WindowsIds.TryGetValue(id, out var windowsId) && TryFind(windowsId) is { } mapped) return mapped;
        exact = false;
        return TimeZoneInfo.Local;
    }

    /// <summary>Nazwa IANA strefy (do chmury): na Linuksie i Androidzie to już jej id; na Windows zamiana przez ICU albo
    /// zapasowa mapa; null – nie da się ustalić.</summary>
    public static string? ToIana(TimeZoneInfo zone)
    {
        if (zone.HasIanaId) return zone.Id;
        try
        {
            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana)) return iana;
        }
        catch (Exception e) when (e is InvalidTimeZoneException or ArgumentException or PlatformNotSupportedException) { }
        return WindowsIds.FirstOrDefault(p => p.Value == zone.Id && p.Key.Contains('/')).Key;
    }

    private static TimeZoneInfo? TryFind(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException) { return null; }
    }
}
