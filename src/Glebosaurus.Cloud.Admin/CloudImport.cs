using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Glebosaurus.Core.Cloud;
using Glebosaurus.Core.Config;
using Glebosaurus.Tuya;
using Npgsql;
using NpgsqlTypes;

namespace Glebosaurus.Cloud.Admin;

/// <summary>Czujnik do przeniesienia do chmury. Dane Tuya (z kluczem) są poza <see cref="ToString"/>.</summary>
public sealed record SensorImport(string Id, string Name, string Mac, JsonObject Options)
{
    internal JsonObject Credentials { get; init; } = [];

    public override string ToString() => $"SensorImport {{ {Id}, {Name}, {Mac} }}";
}

/// <summary>
/// Co <c>cloud import</c> zapisze w chmurze: ustawienia (z odbiorcami), czujniki z danymi Tuya, hasło Gmaila i adres sygnału
/// healthchecks.io. Sekrety są tylko w polach wewnętrznych – <see cref="ToString"/> i wydruk planu ich nie pokazują.
/// </summary>
/// <param name="Problems">Coś uniemożliwia import (np. identyfikator czujnika, którego chmura nie przyjmie) – nic nie jest zapisywane.</param>
/// <param name="Notes">Uwagi bez blokowania (np. przycięta retencja logów).</param>
public sealed record ImportPlan(JsonObject Settings, IReadOnlyList<SensorImport> Sensors, IReadOnlyList<string> Problems, IReadOnlyList<string> Notes)
{
    internal string? GmailPassword { get; init; }
    internal string? HealthchecksPingUrl { get; init; }

    public bool SetsGmailPassword => GmailPassword is not null;
    public bool SetsHealthchecksPingUrl => HealthchecksPingUrl is not null;

    public override string ToString() =>
        $"ImportPlan {{ czujników {Sensors.Count}, hasło Gmaila {(SetsGmailPassword ? "tak" : "nie")}, strażnik {(SetsHealthchecksPingUrl ? "tak" : "nie")}, problemów {Problems.Count} }}";
}

/// <summary>
/// Przeniesienie konfiguracji trybu samodzielnego do chmury (ARCHITEKTURA.md, zasada 15): jednorazowo, ale bezpiecznie do
/// powtórzenia (czujniki i ustawienia są nadpisywane, nic nie jest usuwane). Historia odczytów nie idzie tędy – wysyła ją
/// sam agent w trybie chmurowym ze swojej bazy (stan wysłania w SQLite), już po imporcie czujników.
/// </summary>
public static partial class CloudImporter
{
    private static readonly JsonSerializerOptions TuyaJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <param name="timeZone">Strefa raportów (IANA); null – strefa tego komputera.</param>
    public static ImportPlan Plan(AgentConfig standalone, string? healthchecksPingUrl, string? timeZone)
    {
        var problems = new List<string>();
        var notes = new List<string>();
        if (standalone.Cloud is not null)
            problems.Add("To jest konfiguracja trybu chmurowego (sekcja „cloud”) – import bierze konfigurację trybu samodzielnego.");

        var zone = timeZone ?? TimeZones.ToIana(TimeZoneInfo.Local);
        if (zone is null)
            problems.Add($"Nie da się ustalić nazwy IANA strefy czasowej tego komputera ({TimeZoneInfo.Local.Id}) – podaj --time-zone, np. Europe/Warsaw.");
        else
        {
            TimeZones.Resolve(zone, out var known);
            if (!known) problems.Add($"Strefa czasowa „{zone}” jest nieznana – podaj nazwę IANA, np. Europe/Warsaw.");
        }

        var logDays = standalone.Logs.RetentionDays;
        if (logDays > 30)
        {
            notes.Add($"Retencja logów {logDays} dni przycięta do 30 (w chmurze logi agentów są w bazie).");
            logDays = 30;
        }
        var settings = new JsonObject
        {
            ["timeZone"] = zone,
            ["readIntervalMinutes"] = standalone.ReadIntervalMinutes,
            ["scanWindowSeconds"] = standalone.ScanWindowSeconds,
            ["reportTimes"] = new JsonArray([.. standalone.Reports.Times.Select(t => (JsonNode)JsonValue.Create(t)!)]),
            ["reportCatchUpHours"] = standalone.Reports.CatchUpHours,
            ["alertReminderHours"] = standalone.Alerts.ReminderHours,
            ["logRetentionDays"] = logDays
        };
        string? password = null;
        if (standalone.Mail is { } mail)
        {
            settings["gmailAddress"] = mail.GmailAddress;
            settings["alertRecipients"] = Addresses(mail.AlertRecipients);
            settings["reportRecipients"] = Addresses(mail.ReportRecipients);
            settings["adminRecipients"] = Addresses(mail.AdminRecipients);
            password = mail.AppPassword;
        }
        else notes.Add("Brak sekcji „mail” – adresu nadawcy, hasła i odbiorców nie przenoszę (ustaw je osobno, inaczej maile nie wyjdą).");

        var sensors = new List<SensorImport>();
        foreach (var sensor in standalone.Sensors)
        {
            var label = $"Czujnik {sensor.Id}";
            if (!CloudId().IsMatch(sensor.Id))
            {
                problems.Add($"{label}: w chmurze identyfikator może mieć tylko małe litery, cyfry, „_” i „-” (do 40 znaków, na początku litera albo cyfra).");
                continue;
            }
            if (!BleAddress.TryParse(sensor.Tuya.Mac, out var address))
            {
                problems.Add($"{label}: brak adresu MAC w danych Tuya – w chmurze czujnik jest rozpoznawany po MAC (dopisz „mac” albo sparuj ponownie).");
                continue;
            }
            if (sensor.Name.Length > 80) problems.Add($"{label}: nazwa dłuższa niż 80 znaków.");
            var mac = BleAddress.Format(address);
            var credentials = JsonSerializer.SerializeToNode(sensor.Tuya, TuyaJson)!.AsObject();
            credentials["mac"] = mac;
            sensors.Add(new(sensor.Id, sensor.Name, mac, new JsonObject
            {
                ["enabled"] = true,
                ["readIntervalMinutes"] = sensor.ReadIntervalMinutes,
                ["wateringBelowPercent"] = sensor.WateringBelowPercent,
                ["wateringClearAbovePercent"] = sensor.WateringClearAbovePercent,
                ["consecutiveReadings"] = sensor.ConsecutiveReadings,
                ["lowBatteryPercent"] = sensor.LowBatteryPercent,
                ["silentAfterHours"] = sensor.SilentAfterHours
            }) { Credentials = credentials });
        }

        string? ping = null;
        if (healthchecksPingUrl is { Length: > 0 } url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps) ping = url;
            else problems.Add("healthchecks.pingUrl w pliku administratora nie jest adresem https.");
        }
        else notes.Add("Brak adresu sygnału healthchecks.io w pliku administratora – strażnik nie będzie dostawał sygnałów.");

        return new(settings, sensors, problems, notes) { GmailPassword = password, HealthchecksPingUrl = ping };
    }

    /// <summary>Zapisuje plan w jednej transakcji (wszystko albo nic) funkcjami administracyjnymi schematu.</summary>
    public static async Task ApplyAsync(NpgsqlDataSource dataSource, ImportPlan plan, CancellationToken cancellationToken)
    {
        if (plan.Problems.Count > 0) throw new InvalidOperationException("Plan importu ma problemy – nic nie zapisano.");
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        async Task Run(string sql, params (string Name, object? Value, NpgsqlDbType Type)[] args)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            foreach (var (name, value, type) in args) command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await Run("select glebosaurus.admin_settings_update(@p)", ("p", plan.Settings.ToJsonString(), NpgsqlDbType.Jsonb));
        foreach (var sensor in plan.Sensors)
        {
            await Run("select glebosaurus.admin_sensor_upsert(@id, @name, @mac, @o)", ("id", sensor.Id, NpgsqlDbType.Text),
                ("name", sensor.Name, NpgsqlDbType.Text), ("mac", sensor.Mac, NpgsqlDbType.Text), ("o", sensor.Options.ToJsonString(), NpgsqlDbType.Jsonb));
            await Run("select glebosaurus.admin_sensor_credentials(@id, @c)", ("id", sensor.Id, NpgsqlDbType.Text),
                ("c", sensor.Credentials.ToJsonString(), NpgsqlDbType.Jsonb));
        }
        if (plan.GmailPassword is { } password)
            await Run("select glebosaurus.admin_mail_secret('gmail-app-password', @v)", ("v", password, NpgsqlDbType.Text));
        if (plan.HealthchecksPingUrl is { } ping)
            await Run("select glebosaurus.admin_mail_secret('healthchecks-ping-url', @v)", ("v", ping, NpgsqlDbType.Text));
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Identyfikatory czujników już będących w chmurze – do pokazania, co import doda, a co nadpisze.</summary>
    public static async Task<IReadOnlyList<string>> CloudSensorIdsAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("select id from glebosaurus.sensors order by id");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var ids = new List<string>();
        while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetString(0));
        return ids;
    }

    private static JsonArray Addresses(IEnumerable<string> addresses) => new([.. addresses.Select(a => (JsonNode)JsonValue.Create(a)!)]);

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,39}$")]
    private static partial Regex CloudId();
}

/// <summary>Agent w chmurze z punktu widzenia właściciela.</summary>
public sealed record CloudAgentInfo(string Name, bool CanSendMail, int? SenderPriority, bool Enabled, bool Enrolled, DateTimeOffset? LastSeenAt);

/// <summary>Agenci: lista i nowy agent z jednorazowym kodem rejestracji (ARCHITEKTURA.md, zasada 4).</summary>
public static partial class CloudAgents
{
    public static async Task<IReadOnlyList<CloudAgentInfo>> ListAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            select name, can_send_mail, sender_priority, enabled, token_hash is not null, last_seen_at from glebosaurus.agents order by name
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var agents = new List<CloudAgentInfo>();
        while (await reader.ReadAsync(cancellationToken))
            agents.Add(new(reader.GetString(0), reader.GetBoolean(1), reader.IsDBNull(2) ? null : reader.GetInt32(2), reader.GetBoolean(3),
                reader.GetBoolean(4), reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5)));
        return agents;
    }

    /// <summary>Tworzy (albo zmienia rolę) agenta i daje kod rejestracji ważny 24 h. Kod pokazuje się tylko raz.</summary>
    /// <param name="senderPriority">null – agent tylko zbiera; 1–9 – kandydat na nadawcę (1 najważniejszy).</param>
    public static async Task<string> CreateWithCodeAsync(NpgsqlDataSource dataSource, string name, int? senderPriority, CancellationToken cancellationToken)
    {
        if (!AgentName().IsMatch(name))
            throw new ArgumentException("Nazwa agenta: małe litery, cyfry, „_” i „-” (do 40 znaków, na początku litera albo cyfra).");
        if (senderPriority is < 1 or > 9) throw new ArgumentException("Priorytet nadawcy: od 1 (najważniejszy) do 9.");
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var upsert = new NpgsqlCommand("select glebosaurus.admin_agent_upsert(@n, @m, @p)", connection, transaction))
        {
            upsert.Parameters.AddWithValue("n", name);
            upsert.Parameters.AddWithValue("m", senderPriority is not null);
            upsert.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Integer) { Value = (object?)senderPriority ?? DBNull.Value });
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }
        string code;
        await using (var create = new NpgsqlCommand("select glebosaurus.admin_enrollment_code(@n)", connection, transaction))
        {
            create.Parameters.AddWithValue("n", name);
            code = (string)(await create.ExecuteScalarAsync(cancellationToken))!;
        }
        await transaction.CommitAsync(cancellationToken);
        return code;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,39}$")]
    private static partial Regex AgentName();
}
