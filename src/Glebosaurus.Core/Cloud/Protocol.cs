using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Glebosaurus.Core.Domain;
using Glebosaurus.Tuya;

namespace Glebosaurus.Core.Cloud;

// Protokół agent ↔ chmura w wersji 1: typy odpowiadają 1:1 funkcjom SQL z supabase/migrations (ARCHITEKTURA.md,
// zasada 13). Nazwy pól JSON w camelCase, jak w odpowiedziach funkcji.

public static class CloudProtocol
{
    public const int Version = 1;

    public const string NotifierResource = "notifier";

    public static string SensorResource(string sensorId) => "sensor:" + sensorId;

    public static bool TryParseSensorResource(string resource, out string sensorId)
    {
        sensorId = resource.StartsWith("sensor:", StringComparison.Ordinal) ? resource["sensor:".Length..] : "";
        return sensorId.Length > 0;
    }
}

/// <param name="Version">Wersja programu agenta.</param>
/// <param name="Ble">Stan radia, np. „ok”, „błąd skanu”.</param>
/// <remarks>Inni agenci dostają go w liście agentów z <c>agent_sync</c> – baza oddaje tylko znane pola o właściwym typie
/// (<c>glebosaurus.agent_status_view</c>), więc nowe pole wymaga też migracji.</remarks>
public sealed record AgentStatus(string Version, string Platform, string? Ble = null)
{
    // Telefon (agent Android): stan zasilania, temperatura i sieć w sygnale życia.
    public int? BatteryPercent { get; init; }
    public double? BatteryTemperatureCelsius { get; init; }

    /// <summary>Podłączony do ładowarki (ochrona akumulatora potrafi wstrzymać ładowanie – to nie błąd).</summary>
    public bool? Charging { get; init; }

    /// <summary>Rodzaj aktywnej sieci – <see cref="AgentDevice.Networks"/>.</summary>
    public string? Network { get; init; }

    /// <summary>Siła Wi-Fi (tylko gdy aktywna sieć to Wi-Fi).</summary>
    public int? WifiDbm { get; init; }

    /// <summary>Stan cieplny systemu (Android <c>PowerManager.getCurrentThermalStatus</c>) – <see cref="AgentDevice.ThermalStatuses"/>.</summary>
    public string? ThermalStatus { get; init; }

    /// <summary>Temperatura procesora (agent na Linuksie, np. Raspberry Pi – <see cref="LinuxThermal"/>; od migracji
    /// 20261120120000).</summary>
    public double? CpuTemperatureCelsius { get; init; }
}

/// <summary>Słyszenie czujnika przez agenta. Czasy względne („tyle sekund temu”), bo zegar agenta może się rozjechać z bazą.</summary>
public sealed record SightingReport(string Sensor, int? Rssi, double? HeardSecondsAgo, double? LastAttemptSecondsAgo,
    double? LastSuccessSecondsAgo, int Failures);

/// <summary>Odczyt w postaci chmury: temperatura w dziesiątych częściach stopnia i czas w pełnych milisekundach UTC – jak w
/// lokalnym SQLite, więc klucz <c>(sensor_id, at)</c> jest ten sam po obu stronach (baza trzyma mikrosekundy, .NET 100 ns).</summary>
public sealed record ReadingReport(string Sensor, DateTimeOffset At, int? Moisture, int? TempTenths, int? Battery, int? BatteryState)
{
    public static ReadingReport From(StoredReading reading) => new(reading.SensorId,
        DateTimeOffset.FromUnixTimeMilliseconds(reading.At.ToUnixTimeMilliseconds()), reading.MoisturePercent,
        reading.SoilTemperatureCelsius is { } t ? (int)Math.Round(t * 10m, MidpointRounding.AwayFromZero) : null,
        reading.BatteryPercent, reading.BatteryState);

    public StoredReading ToStored() => new(Sensor, At, Moisture, TempTenths is { } t ? t / 10m : null, Battery, BatteryState);
}

public sealed record LeaseRelease(string Resource, long Epoch);

/// <summary>Ładunek <c>agent_sync</c>: sygnał życia, słyszenia, paczka odczytów, oddania i prośby o dzierżawy.</summary>
public sealed record SyncRequest(AgentStatus Status)
{
    public int Protocol { get; init; } = CloudProtocol.Version;
    public IReadOnlyList<SightingReport> Sightings { get; init; } = [];
    public IReadOnlyList<ReadingReport> Readings { get; init; } = [];
    public IReadOnlyList<LeaseRelease> Releases { get; init; } = [];
    public IReadOnlyList<string> Claims { get; init; } = [];
}

public sealed record AgentIdentity(string Name, bool CanSendMail, int? SenderPriority);

/// <summary>Wynik prośby o dzierżawę: przyznana (epoka, ważność od chwili wysłania prośby) albo odmowa (kto trzyma, kiedy
/// spróbować) albo błąd (<c>invalid_resource</c>, <c>not_sender</c>, <c>unknown_sensor</c>).</summary>
public sealed record LeaseResult(string Resource, bool Granted)
{
    public long? Epoch { get; init; }
    public double? ValidSeconds { get; init; }
    public string? Holder { get; init; }
    public double? RetryInSeconds { get; init; }
    public string? Error { get; init; }
}

public sealed record RejectedReading(string? Sensor, DateTimeOffset? At, string Reason);

public sealed record ReadingsResult(int Received, int Inserted, IReadOnlyList<RejectedReading> Rejected);

public sealed record ReleaseResult(string? Resource, bool Released);

/// <summary>Polecenie skierowane do agenta (read_now, report_now, release, pair, send_log).</summary>
public sealed record CloudCommand(long Id, string Kind, string? Sensor, JsonObject? Params, DateTimeOffset CreatedAt, double ExpiresInSeconds);

public sealed record OtherHearing(string Agent, int Rssi, double HeardSecondsAgo);

public sealed record SensorLeaseView(string Sensor, string? Holder, double? HolderExpiresInSeconds, OtherHearing? BestOther);

public sealed record NotifierView(string Holder, long Epoch, double ExpiresInSeconds);

public sealed record AgentView(string Name, bool CanSendMail, int? SenderPriority, double? LastSeenSecondsAgo)
{
    /// <summary>Sygnał życia z ostatniej synchronizacji agenta; null – jeszcze go nie wysłał (albo baza sprzed migracji
    /// 20261119120000).</summary>
    public AgentStatus? Status { get; init; }
}

public sealed record SyncResponse(int Protocol, DateTimeOffset ServerTime, long ConfigVersion, AgentIdentity Agent)
{
    public ReadingsResult Readings { get; init; } = new(0, 0, []);
    public IReadOnlyList<ReleaseResult> Released { get; init; } = [];
    public IReadOnlyList<LeaseResult> Leases { get; init; } = [];
    public IReadOnlyList<CloudCommand> Commands { get; init; } = [];
    public IReadOnlyList<SensorLeaseView> Sensors { get; init; } = [];
    public NotifierView? Notifier { get; init; }
    public IReadOnlyList<AgentView> Agents { get; init; } = [];
}

public sealed record EnrollResult(int Protocol, string Agent, string Token, bool CanSendMail, int? SenderPriority)
{
    // Token to sekret – nie trafia do dziennika przez ToString.
    public override string ToString() => $"EnrollResult {{ Agent = {Agent}, CanSendMail = {CanSendMail} }}";
}

public sealed record CloudSettings(string TimeZone, double ReadIntervalMinutes, int ScanWindowSeconds, IReadOnlyList<string> ReportTimes,
    double ReportCatchUpHours, double AlertReminderHours, int LogRetentionDays);

public sealed record CloudMailSettings(string? GmailAddress, IReadOnlyList<string> AlertRecipients, IReadOnlyList<string> ReportRecipients,
    IReadOnlyList<string> AdminRecipients);

public sealed record CloudSensor(string Id, string Name, string Mac, bool Enabled, double? ReadIntervalMinutes, int WateringBelowPercent,
    int WateringClearAbovePercent, int ConsecutiveReadings, int LowBatteryPercent, double SilentAfterHours)
{
    /// <summary>Dane przypisania Tuya z Vault – sekret (klucz lokalny czujnika), więc poza <see cref="ToString"/>.</summary>
    public TuyaCredentials? Tuya { get; init; }

    public override string ToString() => $"CloudSensor {{ Id = {Id}, Name = {Name}, Mac = {Mac}, Enabled = {Enabled} }}";
}

/// <summary>Konfiguracja z <c>get_config</c>. Sekcja poczty tylko dla kandydatów na nadawcę.</summary>
public sealed record CloudConfig(int Protocol, long ConfigVersion, AgentIdentity Agent, CloudSettings Settings, CloudMailSettings? Mail,
    IReadOnlyList<CloudSensor> Sensors);

public sealed record MailClaimResult(bool Claimed, string Reason, DateTimeOffset? SentAt = null)
{
    public bool AlreadyClaimed => Reason == "already_claimed";
}

/// <summary>Hasło aplikacji Gmail i adres sygnału healthchecks.io dla obecnego nadawcy – tylko w pamięci, nigdy w dzienniku.</summary>
public sealed record MailCredentials(string? GmailAddress, string? AppPassword, string? HealthchecksPingUrl)
{
    public override string ToString() => $"MailCredentials {{ GmailAddress = {GmailAddress}, AppPassword = {(AppPassword is null ? "∅" : "***")} }}";
}

public sealed record CloudAlert(long Id, string Sensor, AlertKind Kind, string Detail, DateTimeOffset OpenedAt, DateTimeOffset? LastNotifiedAt,
    DateTimeOffset? ResolvedAt)
{
    public AlertRecord ToRecord() => new(Id, Sensor, Kind, Detail, OpenedAt, LastNotifiedAt, ResolvedAt);
}

public sealed record AlertOpenResult(bool Created, CloudAlert Alert);

/// <summary>
/// Paczka z <c>readings_feed</c>: odczyty wszystkich agentów przyjęte po numerze kolejnym <c>after</c> (od najstarszego
/// numeru); <paramref name="Last"/> – od tego numeru pytać następnym razem, <paramref name="More"/> – czeka więcej.
/// Baza oddaje tylko odczyty „ustalone” (przyjęte co najmniej 30 s temu), więc żaden nie przepada między paczkami.
/// </summary>
public sealed record ReadingsFeed(IReadOnlyList<ReadingReport> Readings, long Last, bool More);

public sealed record CommandCreateResult(long Id, bool Created);

public sealed record LogPutResult(DateOnly Day, int SizeBytes);

public sealed record AgentLog(string Agent, DateOnly Day, int SizeBytes, DateTimeOffset UploadedAt, string ContentGzipBase64);

/// <summary>Rodzaj błędu chmury – agent reaguje na rodzaj, nie na treść komunikatu.</summary>
public enum CloudErrorKind
{
    /// <summary>Token nieznany albo unieważniony (SQLSTATE 28000, HTTP 403) – agent przestaje pracować z chmurą.</summary>
    Unauthorized,

    /// <summary>Czynność tylko dla obecnego nadawcy, a blokady z tą epoką już nie ma (SQLSTATE 42501).</summary>
    NotNotifier,

    /// <summary>Błędne dane (SQLSTATE 22023 i inne błędy 4xx) – błąd programu, ponowienie nic nie da.</summary>
    Invalid,

    /// <summary>Brak sieci, przekroczony czas, błąd serwera, projekt uśpiony – można ponowić później.</summary>
    Unavailable,

    /// <summary>Odpowiedź niezgodna z protokołem (np. nowsza wersja bazy).</summary>
    Protocol
}

public sealed class CloudApiException(CloudErrorKind kind, string message, string? sqlState = null, Exception? inner = null)
    : Exception(message, inner)
{
    public CloudErrorKind Kind { get; } = kind;
    public string? SqlState { get; } = sqlState;
}

/// <summary>
/// Funkcje chmury dostępne agentowi (Data API Supabase z kluczem publikowalnym i tokenem agenta). Implementacja HTTP:
/// <c>Glebosaurus.Cloud.SupabaseCloudApi</c>; testy silników używają atrapy.
/// </summary>
/// <exception cref="CloudApiException">Każda metoda – z rodzajem błędu.</exception>
public interface ICloudApi
{
    Task<CloudConfig> GetConfigAsync(CancellationToken cancellationToken);
    Task<SyncResponse> SyncAsync(SyncRequest request, CancellationToken cancellationToken);
    Task<LeaseResult> ClaimAsync(string resource, CancellationToken cancellationToken);
    Task<bool> ReleaseAsync(string resource, long epoch, CancellationToken cancellationToken);

    Task<MailClaimResult> MailClaimAsync(string key, long epoch, CancellationToken cancellationToken);
    Task<bool> MailSentAsync(string key, string? messageId, CancellationToken cancellationToken);
    Task<bool> MailAbandonAsync(string key, CancellationToken cancellationToken);
    Task<MailCredentials> MailCredentialsAsync(long epoch, CancellationToken cancellationToken);

    Task<IReadOnlyList<StoredReading>> ReadingsRangeAsync(IReadOnlyCollection<string> sensors, DateTimeOffset from, DateTimeOffset to,
        CancellationToken cancellationToken);

    /// <summary>Odczyty wszystkich agentów przyjęte po numerze <paramref name="after"/> (kopia lokalna dla panelu).</summary>
    Task<ReadingsFeed> ReadingsFeedAsync(long after, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<CloudAlert>> AlertListAsync(CancellationToken cancellationToken);
    Task<AlertOpenResult> AlertOpenAsync(long epoch, string sensor, AlertKind kind, string detail, CancellationToken cancellationToken);
    Task<bool> AlertResolveAsync(long epoch, long alertId, CancellationToken cancellationToken);
    Task<int> AlertNotifiedAsync(long epoch, IReadOnlyCollection<long> alertIds, CancellationToken cancellationToken);

    Task<CommandCreateResult> CommandCreateAsync(string kind, string? sensor, CancellationToken cancellationToken);
    Task<bool> CommandDoneAsync(long commandId, JsonObject? result, CancellationToken cancellationToken);

    Task<LogPutResult> LogPutAsync(DateOnly day, byte[] gzipContent, CancellationToken cancellationToken);
    Task<IReadOnlyList<AgentLog>> LogGetAsync(long epoch, DateOnly day, CancellationToken cancellationToken);
}

/// <summary>Serializacja protokołu bez refleksji (agent Android jest przycinany).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
[JsonSerializable(typeof(SyncRequest))]
[JsonSerializable(typeof(SyncResponse))]
[JsonSerializable(typeof(LeaseResult))]
[JsonSerializable(typeof(EnrollResult))]
[JsonSerializable(typeof(CloudConfig))]
[JsonSerializable(typeof(CachedCloudConfig))]
[JsonSerializable(typeof(MailClaimResult))]
[JsonSerializable(typeof(MailCredentials))]
[JsonSerializable(typeof(List<ReadingReport>))]
[JsonSerializable(typeof(ReadingsFeed))]
[JsonSerializable(typeof(List<CloudAlert>))]
[JsonSerializable(typeof(AlertOpenResult))]
[JsonSerializable(typeof(CommandCreateResult))]
[JsonSerializable(typeof(LogPutResult))]
[JsonSerializable(typeof(List<AgentLog>))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
public sealed partial class CloudJsonContext : JsonSerializerContext;
