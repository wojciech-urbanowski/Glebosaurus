using System.Net;
using System.Net.Sockets;
using Glebosaurus.Tuya;

namespace Glebosaurus.Core.Config;

/// <summary>
/// Konfiguracja agenta. Z pliku na urządzeniu (<see cref="AgentConfigLoader"/>) pochodzą tylko dostęp do chmury i ustawienia
/// urządzenia (baza, retencja, dziennik, panel); czujniki, odstęp odczytów, raporty i alerty uzupełnia konfiguracja z chmury
/// (<see cref="Cloud.CloudConfigMapper"/>).
/// </summary>
public sealed class AgentConfig
{
    /// <summary>Nazwa w dzienniku i certyfikacie panelu, zanim przyjdzie konfiguracja z chmury (wtedy: nazwa agenta w chmurze).</summary>
    public string MonitorName { get; set; } = Environment.MachineName;

    /// <summary>
    /// Domyślny odstęp między odczytami czujnika, w minutach (ułamki dozwolone, minimum 0,5). 2 h 5 min: wilgotność gleby
    /// zmienia się wolno, a czujnik sam zapisuje rekord co godzinę i oddaje go przy połączeniu, więc rzadsze połączenia nie
    /// gubią przebiegu, a oszczędzają baterię.
    /// </summary>
    public double ReadIntervalMinutes { get; set; } = DefaultReadIntervalMinutes;

    public const double DefaultReadIntervalMinutes = 125;

    /// <summary>
    /// Jak długo łącznie nasłuchiwać reklam w poszukiwaniu czujników, które czekają na odczyt (czas połączeń się nie wlicza).
    /// Znaleziony czujnik jest czytany od razu, więc dłuższe okno kosztuje tylko przy czujniku poza zasięgiem. 60 s, bo przy
    /// słabym sygnale reklamy przychodzą seriami z przerwami do ok. 27 s (pomiar 2026-11-13).
    /// </summary>
    public int ScanWindowSeconds { get; set; } = 60;

    /// <summary>Plik bazy SQLite; ścieżka względna jest liczona od katalogu pliku konfiguracji.</summary>
    public string Database { get; set; } = "glebosaurus.db";

    /// <summary>Jak długo przechowywać odczyty w lokalnej bazie.</summary>
    public int RetentionDays { get; set; } = 400;

    /// <summary>Czujniki z chmury (w pliku agenta ich nie ma).</summary>
    public List<SensorConfig> Sensors { get; set; } = [];

    public ReportsConfig Reports { get; set; } = new();
    public AlertsConfig Alerts { get; set; } = new();
    public LogsConfig Logs { get; set; } = new();

    /// <summary>Panel diagnostyczny WWW (HTTPS, tylko sieć lokalna, hasło). Brak sekcji = panel wyłączony.</summary>
    public WebConfig? Web { get; set; }

    /// <summary>
    /// Czy agent używa Bluetooth. <c>false</c> – agent bez radia (np. Raspberry Pi daleko od czujników jako sam nadawca
    /// maili, ROADMAP 2.3a): <c>run</c> w ogóle nie otwiera BLE, nie słucha, nie ubiega się o czujniki i ich nie czyta, ale
    /// synchronizuje się z chmurą, wysyła do niej swój dziennik, a z rolą nadawcy wysyła maile. Ustawienie urządzenia, nie
    /// chmury – zależy od tego, gdzie stoi i co potrafi sprzęt.
    /// </summary>
    public bool Bluetooth { get; set; } = true;

    /// <summary>Opis Bluetooth agenta bez radia – w sygnale życia (panel innych agentów) i we własnym panelu.</summary>
    public const string BluetoothOffText = "wyłączony w konfiguracji (agent nie czyta czujników)";

    /// <summary>
    /// Dostęp do chmury – w pliku agenta wymagany. Czujniki, harmonogramy, odbiorcy i sekrety przychodzą z chmury.
    /// </summary>
    public CloudAccessConfig? Cloud { get; set; }
}

/// <summary>Dostęp agenta do chmury Supabase. Token agenta leży w osobnym, chronionym pliku (tworzy go <c>enroll</c>).</summary>
public sealed class CloudAccessConfig
{
    /// <summary>Adres projektu, np. https://abcdefghijklmnopqrst.supabase.co.</summary>
    public string Url { get; set; } = "";

    /// <summary>Klucz publikowalny projektu (sb_publishable_…) – ten sam dla wszystkich agentów; uprawnienia daje token.</summary>
    public string PublishableKey { get; set; } = "";

    /// <summary>Plik z tokenem agenta (DPAPI / prawa 600); ścieżka względna od katalogu konfiguracji.</summary>
    public string TokenFile { get; set; } = "glebosaurus-cloud.token";

    /// <summary>Ostatnia dobra konfiguracja z chmury (z kluczami Tuya, chroniona jak token); względna od katalogu konfiguracji.</summary>
    public string ConfigCacheFile { get; set; } = "glebosaurus-cloud-config.bin";

    /// <summary>Codzienna kopia zapasowa chmury robiona przez tego agenta (tylko jeden agent – ten z hasłem bazy).</summary>
    public CloudBackupConfig? Backup { get; set; }

    public Uri ProjectUrl => new(Url.TrimEnd('/'));
}

/// <summary>
/// Kopia zapasowa chmury w ramach <c>run</c> (darmowy plan Supabase kopii nie robi): raz dziennie zrzut bazy i sekretów do
/// lokalnego katalogu. Wymaga pliku administratora z hasłem bazy – to jedyny agent, który je ma.
/// </summary>
public sealed class CloudBackupConfig
{
    /// <summary>Plik administratora (jak cloud-admin.json, wystarczy supabase.url i databasePassword – bez klucza
    /// tajnego); ścieżka względna od katalogu konfiguracji.</summary>
    public string AdminFile { get; set; } = "";

    /// <summary>Katalog kopii (względny od katalogu konfiguracji). Kopie zawierają odszyfrowane sekrety.</summary>
    public string Directory { get; set; } = "backups";

    /// <summary>Ile najnowszych kopii zostawić.</summary>
    public int Keep { get; set; } = 14;
}

/// <summary>
/// Panel diagnostyczny w przeglądarce, wbudowany w działającego agenta (<c>run</c>): podgląd stanu, odczyt czujników i
/// raport na żądanie; konfiguracji nie zmienia. Hasło (jego skrót) i certyfikat HTTPS leżą w osobnych plikach obok
/// konfiguracji – tworzy je polecenie <c>web-password</c>.
/// </summary>
public sealed class WebConfig
{
    public const int DefaultPort = 8470;

    /// <summary>Sieci, z których panel zawsze przyjmuje połączenia: loopback i adresy prywatne (sieć domowa).</summary>
    public static readonly IReadOnlyList<string> PrivateNetworks =
        ["127.0.0.0/8", "::1/128", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16", "fe80::/10", "fc00::/7"];

    /// <summary>Pozwala chwilowo wyłączyć panel bez usuwania sekcji.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Port HTTPS (1024–65535, żeby na Linuksie nie trzeba było uprawnień roota).</summary>
    public int Port { get; set; } = DefaultPort;

    /// <summary>
    /// Sieci (CIDR, np. „192.168.1.0/24”, albo pojedyncze adresy), z których wolno się łączyć. Pusta lista = wszystkie
    /// prywatne (<see cref="PrivateNetworks"/>). Loopback jest dozwolony zawsze.
    /// </summary>
    public List<string> AllowedNetworks { get; set; } = [];

    /// <summary>Jak długo ważne jest zalogowanie (godziny). Restart agenta i tak wylogowuje.</summary>
    public double SessionHours { get; set; } = 12;

    /// <summary>Dozwolone sieci jako <see cref="IPNetwork"/>; rzuca <see cref="InvalidDataException"/> przy błędnym wpisie.</summary>
    public IReadOnlyList<IPNetwork> ParseAllowedNetworks()
    {
        var entries = AllowedNetworks.Count > 0 ? AllowedNetworks.Concat(["127.0.0.0/8", "::1/128"]) : PrivateNetworks;
        var result = new List<IPNetwork>();
        foreach (var raw in entries)
        {
            var text = raw.Trim();
            if (!text.Contains('/') && IPAddress.TryParse(text, out var single))
                text += single.AddressFamily == AddressFamily.InterNetworkV6 ? "/128" : "/32";
            if (!IPNetwork.TryParse(text, out var network))
                throw new InvalidDataException($"web.allowedNetworks: „{raw}” nie jest siecią w zapisie CIDR (np. 192.168.1.0/24) ani adresem IP.");
            result.Add(network);
        }
        return result;
    }
}

public sealed class SensorConfig
{
    /// <summary>Stabilny identyfikator czujnika (w chmurze: małe litery, cyfry, „_” i „-”).</summary>
    public string Id { get; set; } = "";

    /// <summary>Nazwa widoczna w mailach, np. „Monstera w salonie”.</summary>
    public string Name { get; set; } = "";

    /// <summary>Dane lokalnego przypisania Tuya wpisane wprost: mac, uuid, deviceId, localKeyHex, productId.</summary>
    public TuyaCredentials Tuya { get; set; } = new();

    /// <summary>
    /// Plik konfiguracji chmury: plik z danymi przypisania (tworzony przez polecenie pair); ścieżka względna liczy się od
    /// katalogu tego pliku. Zamiast <see cref="Tuya"/> – klucze nie muszą wtedy leżeć w głównym pliku.
    /// </summary>
    public string? TuyaFile { get; set; }

    /// <summary>Własny odstęp odczytów; null = domyślny z <see cref="AgentConfig.ReadIntervalMinutes"/>.</summary>
    public double? ReadIntervalMinutes { get; set; }

    /// <summary>Alert „czas podlać”, gdy wilgotność jest niższa niż ta wartość przez <see cref="ConsecutiveReadings"/> odczytów.</summary>
    public int WateringBelowPercent { get; set; } = 25;

    /// <summary>Alert „czas podlać” wygasa dopiero po przekroczeniu tej wartości (histereza).</summary>
    public int WateringClearAbovePercent { get; set; } = 35;

    public int ConsecutiveReadings { get; set; } = 2;

    /// <summary>Alert „słaba bateria”, gdy wskazanie baterii wynosi tyle procent lub mniej.</summary>
    public int LowBatteryPercent { get; set; } = 18;

    public bool IsBatteryLow(int batteryPercent) => batteryPercent <= LowBatteryPercent;

    /// <summary>Alert „brak odczytów”, gdy od ostatniego udanego odczytu minie tyle godzin.</summary>
    public double SilentAfterHours { get; set; } = 6;
}

/// <summary>Poczta w pliku konfiguracji chmury (<see cref="CloudSetup"/>); hasło trafia do Vault, reszta do ustawień w chmurze.</summary>
public sealed class MailConfig
{
    /// <summary>Konto Gmail nadawcy.</summary>
    public string GmailAddress { get; set; } = "";

    /// <summary>Hasło aplikacji Gmail (https://myaccount.google.com/apppasswords), nie hasło do konta.</summary>
    public string AppPassword { get; set; } = "";

    public List<string> AlertRecipients { get; set; } = [];

    /// <summary>Odbiorcy raportów; pusta lista = ci sami, co alertów.</summary>
    public List<string> ReportRecipients { get; set; } = [];

    public IReadOnlyList<string> EffectiveReportRecipients => ReportRecipients.Count > 0 ? ReportRecipients : AlertRecipients;

    /// <summary>
    /// Administrator: dostaje techniczny dziennik każdego agenta z całego poprzedniego dnia (osobny mail od każdego).
    /// Osobna lista od alertów i raportów; pusta = dzienniki nie są wysyłane.
    /// </summary>
    public List<string> AdminRecipients { get; set; } = [];
}

public sealed class LogsConfig
{
    /// <summary>Ile dni wstecz agent trzyma pliki dziennika (starsze są usuwane); dziennik z wczoraj jest potrzebny do wysyłki, więc minimum 1.</summary>
    public int RetentionDays { get; set; } = 7;
}

public sealed class ReportsConfig
{
    /// <summary>Pory wysyłki raportów w strefie z konfiguracji chmury, format HH:mm. Pusta lista wyłącza raporty.</summary>
    public List<string> Times { get; set; } = ["07:00", "19:00"];

    /// <summary>Jeśli o zaplanowanej porze nie było nadawcy, raport jest nadrabiany przez tyle godzin; potem pomijany.</summary>
    public double CatchUpHours { get; set; } = 3;
}

public sealed class AlertsConfig
{
    /// <summary>Co ile godzin przypominać o wciąż otwartym alercie.</summary>
    public double ReminderHours { get; set; } = 24;
}
