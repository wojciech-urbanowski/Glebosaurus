using System.Globalization;
using Glebosaurus.Core.Monitoring;

namespace Glebosaurus.Core.Cloud;

/// <param name="Warning">Sekcja wymaga uwagi (np. agent odcięty, telefon nie ładuje się) – ekran ją wyróżnia.</param>
public sealed record ScreenSection(string Title, IReadOnlyList<string> Lines, bool Warning = false);

/// <summary>
/// Stan agenta jako zwykły tekst – ekran aplikacji Android (ARCHITEKTURA.md, zasada 12). Źródłem jest ten
/// sam <see cref="MonitorStatus"/> co w lokalnym panelu WWW (<see cref="CloudPanel"/>, składany pole po polu, bez sekretów),
/// plus stan telefonu z sygnału życia. Krótkie wiersze pod wąski ekran.
/// </summary>
public static class AgentScreenText
{
    /// <summary>Od tej temperatury akumulatora ekran ostrzega (telefon stale na ładowarce).</summary>
    public const double HotBatteryCelsius = 45;

    public static IReadOnlyList<ScreenSection> Build(MonitorStatus status, AgentStatus? phone, TimeZoneInfo zone)
    {
        var now = status.Monitor.Now;
        var sections = new List<ScreenSection> { Agent(status, now, zone, out var warning) };
        if (warning is not null) sections.Insert(0, warning);
        if (phone is not null && PhoneSection(phone) is { } phoneSection) sections.Add(phoneSection);
        sections.Add(Sensors(status, now, zone));
        if (status.Alerts.Count > 0)
            sections.Add(new("Otwarte alerty", [.. status.Alerts.Select(a => $"{a.SensorName}: {a.KindText} (od {Clock(a.OpenedAt, now, zone)})")]));
        if (status.Cloud is { Agents.Count: > 0 } cloud)
            sections.Add(new("Agenci", [.. cloud.Agents.Select(a => AgentLine(a, now))]));
        return sections;
    }

    private static ScreenSection Agent(MonitorStatus status, DateTimeOffset now, TimeZoneInfo zone, out ScreenSection? warning)
    {
        warning = null;
        var lines = new List<string>();
        var cloud = status.Cloud;
        var monitor = status.Monitor;
        if (cloud is null)
        {
            lines.Add($"„{monitor.Name}” – bez stanu chmury");
            return new("Agent", lines);
        }
        var role = cloud.CanSendMail ? $"zbiera i może wysyłać maile (priorytet {cloud.SenderPriority})" : "tylko zbiera odczyty";
        lines.Add($"„{cloud.Agent ?? monitor.Name}” w {cloud.Project} – {role}");
        lines.Add($"Wersja {monitor.Version}" + (monitor.StartedAt is { } started ? $", działa od {Clock(started, now, zone)}" : ""));

        if (cloud.Revoked)
            warning = new("Agent odcięty", ["Chmura odrzuciła token agenta – agent nie pracuje.", "Zarejestruj go ponownie nowym kodem od właściciela."], true);
        else if (cloud.SyncFailures > 0)
            warning = new("Brak łączności z chmurą",
                [$"Synchronizacja nieudana {cloud.SyncFailures}× z rzędu: {cloud.LastSyncError ?? "nieznany błąd"}",
                 cloud.LastSyncAt is { } ok ? $"Ostatnia udana: {Clock(ok, now, zone)} ({Ago(ok, now)})" : "Jeszcze żadna się nie udała.",
                 "Odczyty czekają w telefonie i pójdą po powrocie sieci."], true);

        lines.Add(cloud.LastSyncAt is { } last ? $"Synchronizacja: {Clock(last, now, zone)} ({Ago(last, now)})" : "Synchronizacja: jeszcze nie było");
        if (cloud.UnsentReadings > 0) lines.Add($"Czeka na wysłanie: {cloud.UnsentReadings} odczytów");
        if (cloud.ConfigVersion is { } version)
            lines.Add(cloud.ConfigCachedAt is { } cached
                ? $"Konfiguracja: wersja {version} z kopii z {Clock(cached, now, zone)} (chmura niedostępna przy starcie)"
                : $"Konfiguracja: wersja {version}");
        foreach (var note in cloud.ConfigWarnings) lines.Add($"Uwaga: {note}");
        lines.Add(cloud.Notifier is { } notifier ? $"Maile wysyła: {notifier}" : "Maile: żaden nadawca teraz nie pracuje");
        if (monitor.ReadingNow.Count > 0)
            lines.Add($"Teraz czyta: {string.Join(", ", monitor.ReadingNow)}" + (monitor.ReadingSince is { } since ? $" (od {Clock(since, now, zone, seconds: true)})" : ""));
        if (monitor.LastCycleError is { } error && monitor.LastCycleErrorAt is { } errorAt)
            lines.Add($"Ostatni błąd cyklu ({Clock(errorAt, now, zone)}): {error}");
        return new("Agent", lines);
    }

    /// <summary>Stan telefonu z sygnału życia; null, gdy nic o nim nie wiadomo. Ostrzega przy gorącym akumulatorze i bez ładowarki.</summary>
    public static ScreenSection? PhoneSection(AgentStatus phone)
    {
        var parts = new List<string>();
        if (phone.BatteryPercent is { } battery) parts.Add($"bateria {battery} %");
        if (phone.Charging is { } charging) parts.Add(charging ? "ładuje się" : "NIE ładuje się");
        if (phone.BatteryTemperatureCelsius is { } temperature) parts.Add(Celsius(temperature));
        if (phone.WifiDbm is { } wifi) parts.Add($"Wi-Fi {SignalStats.Format(wifi)}");
        if (phone.Ble is { } ble) parts.Add($"Bluetooth: {ble}");
        if (parts.Count == 0) return null;
        var lines = new List<string> { Capitalize(string.Join(" · ", parts)) };
        var warning = false;
        if (phone.BatteryTemperatureCelsius is >= HotBatteryCelsius)
        {
            lines.Add($"Akumulator gorący ({Celsius(phone.BatteryTemperatureCelsius.Value)}) – przenieś telefon w chłodniejsze miejsce.");
            warning = true;
        }
        if (phone.Charging == false)
        {
            lines.Add("Telefon ma pracować stale na ładowarce.");
            warning = true;
        }
        return new("Telefon", lines, warning);
    }

    private static ScreenSection Sensors(MonitorStatus status, DateTimeOffset now, TimeZoneInfo zone)
    {
        var lines = new List<string>();
        if (status.Sensors.Count == 0) lines.Add("Brak czujników w konfiguracji.");
        foreach (var sensor in status.Sensors)
        {
            var who = sensor.HolderIsMe ? "czyta ten telefon" + (sensor.HolderUntil is { } until ? $" (dzierżawa do {Clock(until, now, zone)})" : "")
                : sensor.Holder is { } holder ? $"czyta: {holder}"
                : status.Cloud is null ? "" : "nikt teraz nie czyta";
            lines.Add(who.Length > 0 ? $"{sensor.Name} – {who}" : sensor.Name);
            lines.Add("   " + (sensor.Latest is { } latest ? Reading(latest, now) : "brak odczytów"));

            var details = new List<string>();
            if (sensor.ReadingNow) details.Add("czytam teraz");
            if (sensor.LastSignalDbm is { } signal)
                details.Add($"słyszę {SignalStats.Format(signal)}" + (sensor.LastSignalAt is { } heard ? $" ({Ago(heard, now)})" : ""));
            if (sensor.BestOtherAgent is { } other) details.Add($"{other}: {SignalStats.Format(sensor.BestOtherRssi)}");
            if (sensor.LastResultText is { } result && sensor.LastAttemptAt is { } attempt)
                details.Add($"ostatnia próba: {result} ({Clock(attempt, now, zone)})" + (sensor.Failures > 1 ? $", {sensor.Failures}× z rzędu" : ""));
            if (details.Count > 0) lines.Add("   " + string.Join("; ", details));
            foreach (var alert in sensor.OpenAlerts) lines.Add($"   alert: {alert}");
        }
        return new($"Czujniki ({status.Sensors.Count})", lines);
    }

    private static string AgentLine(CloudAgentStatus agent, DateTimeOffset now)
    {
        var role = agent.CanSendMail ? $"nadawca ({agent.SenderPriority})" : "zbieracz";
        var seen = agent.LastSeenAt is { } at ? Ago(at, now) : "jeszcze nie pracował";
        return $"{agent.Name}{(agent.IsMe ? " (ten telefon)" : "")} – {role}, czujników: {agent.HeldSensors}, kontakt {seen}";
    }

    private static string Reading(ReadingStatus reading, DateTimeOffset now)
    {
        var parts = new List<string>();
        if (reading.MoisturePercent is { } moisture) parts.Add($"{moisture} %");
        if (reading.SoilTemperatureCelsius is { } temperature) parts.Add(Celsius((double)temperature));
        if (reading.BatteryPercent is { } battery) parts.Add($"bateria {battery} %");
        return $"{string.Join(" · ", parts)} ({Ago(reading.At, now)})";
    }

    private static string Celsius(double value) => value.ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',') + " °C";

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>Godzina dzisiaj, dzień i godzina wcześniej – w strefie telefonu.</summary>
    internal static string Clock(DateTimeOffset at, DateTimeOffset now, TimeZoneInfo zone, bool seconds = false)
    {
        var local = TimeZoneInfo.ConvertTime(at, zone);
        var today = TimeZoneInfo.ConvertTime(now, zone).Date == local.Date;
        var time = local.ToString(seconds ? "HH:mm:ss" : "HH:mm", CultureInfo.InvariantCulture);
        return today ? time : local.ToString("dd.MM ", CultureInfo.InvariantCulture) + time;
    }

    internal static string Ago(DateTimeOffset at, DateTimeOffset now)
    {
        var span = now - at;
        if (span < TimeSpan.FromMinutes(1)) return "przed chwilą";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} min temu";
        if (span < TimeSpan.FromDays(1)) return span.Minutes == 0 ? $"{(int)span.TotalHours} h temu" : $"{(int)span.TotalHours} h {span.Minutes} min temu";
        return $"{(int)span.TotalDays} d temu";
    }
}
