using System.Globalization;
using Glebosaurus.Core.Monitoring;

namespace Glebosaurus.Core.Cloud;

/// <param name="Label">Np. „Bateria”.</param>
public sealed record DeviceDetail(string Label, string Value);

/// <summary>Urządzenie agenta z jego ostatniego sygnału życia, gotowe do pokazania.</summary>
/// <param name="Program">Np. „Glebosaurus 0.2.2 · Android”.</param>
/// <param name="Details">Bateria, temperatura, sieć, Bluetooth – tylko to, co agent zgłosił.</param>
/// <param name="Warnings">Co wymaga uwagi: gorący albo zmarznięty akumulator, przegrzanie, słaba bateria bez ładowarki, brak
/// Wi-Fi.</param>
public sealed record AgentDeviceStatus(string Program, IReadOnlyList<DeviceDetail> Details, IReadOnlyList<string> Warnings);

/// <summary>
/// Stan urządzenia agenta z sygnału życia (ARCHITEKTURA.md, zasada 12) – opis i ostrzeżenia wspólne dla ekranu telefonu
/// (<see cref="AgentScreenText"/>) i lokalnego panelu WWW innego agenta (<see cref="CloudPanel"/>). Telefon ma być na Wi-Fi
/// i może stać na zewnątrz; praca na baterii jest normalna – ostrzeżenie dopiero przy słabej baterii bez ładowarki, przy
/// upale, mrozie i bez Wi-Fi.
/// </summary>
public static class AgentDevice
{
    /// <summary>Od tej temperatury akumulatora – ostrzeżenie.</summary>
    public const double HotBatteryCelsius = 45;

    /// <summary>Poniżej tej temperatury akumulatora – ostrzeżenie: akumulator litowy w mrozie się nie ładuje (Android
    /// wstrzymuje ładowanie), szybko traci pojemność, a telefon potrafi się wyłączyć mimo zapasu baterii.</summary>
    public const double ColdBatteryCelsius = 0;

    /// <summary>Poniżej tego poziomu baterii telefonu bez ładowarki – ostrzeżenie.</summary>
    public const int LowBatteryPercent = 30;

    /// <summary>Wartości <see cref="AgentStatus.Network"/> (te same przepuszcza baza – <c>glebosaurus.agent_status_view</c>).</summary>
    public static readonly IReadOnlyList<string> Networks = ["wifi", "cellular", "ethernet", "other"];

    /// <summary>Wartości <see cref="AgentStatus.ThermalStatus"/> w kolejności kodów Androida (<c>THERMAL_STATUS_NONE</c> = 0 …
    /// <c>THERMAL_STATUS_SHUTDOWN</c> = 6; te same przepuszcza baza).</summary>
    public static IReadOnlyList<string> ThermalStatuses => ThermalNames;

    private static readonly string[] ThermalNames = ["none", "light", "moderate", "severe", "critical", "emergency", "shutdown"];

    /// <summary>Od tego stanu cieplnego (system ogranicza wydajność) – ostrzeżenie.</summary>
    private const int HotThermalLevel = 2;

    private static readonly string[] ThermalTexts =
        ["w normie", "ciepły", "ogranicza wydajność", "mocno ogranicza wydajność", "krytycznie gorący", "wyłącza podzespoły", "zaraz się wyłączy"];

    /// <summary>Nazwa stanu cieplnego z kodu Androida; null dla nieznanego kodu.</summary>
    public static string? ThermalStatusName(int code) => code >= 0 && code < ThermalNames.Length ? ThermalNames[code] : null;

    public static AgentDeviceStatus Describe(AgentStatus status) => new(Program(status), Details(status), Warnings(status));

    public static string Program(AgentStatus status) => $"Glebosaurus {status.Version} · {PlatformText(status.Platform)}";

    public static IReadOnlyList<DeviceDetail> Details(AgentStatus status)
    {
        var details = new List<DeviceDetail>();
        var battery = new List<string>();
        if (status.BatteryPercent is { } percent) battery.Add(string.Create(CultureInfo.InvariantCulture, $"{percent} %"));
        if (status.Charging is { } charging) battery.Add(charging ? "na ładowarce" : "bez ładowarki");
        if (battery.Count > 0) details.Add(new("Bateria", string.Join(" · ", battery)));

        var temperature = new List<string>();
        if (status.BatteryTemperatureCelsius is { } celsius) temperature.Add($"{Celsius(celsius)} (akumulator)");
        if (ThermalText(status.ThermalStatus) is { } thermal) temperature.Add($"system: {thermal}");
        if (temperature.Count > 0) details.Add(new("Temperatura", string.Join(" · ", temperature)));

        if (NetworkText(status) is { } network) details.Add(new("Sieć", network));
        if (status.Ble is { Length: > 0 } ble) details.Add(new("Bluetooth", ble));
        return details;
    }

    public static IReadOnlyList<string> Warnings(AgentStatus status)
    {
        var warnings = new List<string>();
        if (status.BatteryTemperatureCelsius is { } celsius and >= HotBatteryCelsius)
            warnings.Add($"Akumulator gorący ({Celsius(celsius)}) – przenieś telefon w chłodniejsze miejsce.");
        else if (status.BatteryTemperatureCelsius is { } cold and < ColdBatteryCelsius)
            warnings.Add($"Akumulator zmarznięty ({Celsius(cold)}) – w mrozie się nie ładuje, telefon może się wyłączyć.");
        if (ThermalLevel(status.ThermalStatus) is >= HotThermalLevel and var level)
            warnings.Add($"Telefon się przegrzewa (system: {ThermalTexts[level]}).");
        // Na ładowarce słaba bateria się ładuje; bez informacji o ładowarce (null) ostrzegamy jak bez niej.
        if (status.BatteryPercent is { } percent and < LowBatteryPercent && status.Charging != true)
            warnings.Add(string.Create(CultureInfo.InvariantCulture, $"Słaba bateria ({percent} %) – podłącz telefon do ładowarki."));
        if (status.Network == "cellular") warnings.Add("Bez Wi-Fi – telefon wysyła dane przez sieć komórkową.");
        return warnings;
    }

    /// <summary>Ocena siły Wi-Fi: od −60 dBm dobry, od −70 dBm średni, słabszy – słaby.</summary>
    public static string WifiQuality(int dbm) => dbm >= -60 ? "dobry" : dbm >= -70 ? "średni" : "słaby";

    private static string? NetworkText(AgentStatus status) => status.Network switch
    {
        // Telefon 0.2.2 wysyła siłę Wi-Fi bez rodzaju sieci – siła jest tylko dla Wi-Fi.
        "wifi" or null when status.WifiDbm is { } dbm => $"Wi-Fi {SignalStats.Format(dbm)} ({WifiQuality(dbm)})",
        "wifi" => "Wi-Fi",
        "cellular" => "sieć komórkowa (bez Wi-Fi)",
        "ethernet" => "Ethernet",
        "other" => "inna niż Wi-Fi",
        _ => null
    };

    private static int ThermalLevel(string? status) => status is null ? -1 : Array.IndexOf(ThermalNames, status);

    private static string? ThermalText(string? status) => ThermalLevel(status) is >= 0 and var level ? ThermalTexts[level] : null;

    private static string PlatformText(string platform) => platform switch
    {
        "android" => "Android",
        "windows" => "Windows",
        "linux" => "Linux",
        _ => platform
    };

    internal static string Celsius(double value) => value.ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',') + " °C";
}
