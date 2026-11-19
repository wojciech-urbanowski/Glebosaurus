using System.Text.Json;
using System.Text.Json.Nodes;
using Glebosaurus.Core.Cloud;
using Glebosaurus.Core.Config;
using Glebosaurus.Tuya;
using Npgsql;
using NpgsqlTypes;

namespace Glebosaurus.Cloud.Admin;

/// <summary>Czujnik do zapisania w chmurze. Dane Tuya (z kluczem) są poza <see cref="ToString"/>.</summary>
public sealed record SensorSetup(string Id, string Name, string Mac, JsonObject Options)
{
    internal JsonObject Credentials { get; init; } = [];

    public override string ToString() => $"SensorSetup {{ {Id}, {Name}, {Mac} }}";
}

/// <summary>
/// Co <c>cloud config</c> zapisze w chmurze: ustawienia (z odbiorcami), czujniki z danymi Tuya, hasło Gmaila i adres sygnału
/// healthchecks.io. Sekrety są tylko w polach wewnętrznych – <see cref="ToString"/> i wydruk planu ich nie pokazują.
/// </summary>
/// <param name="Problems">Coś uniemożliwia zapis (np. nieznana strefa czasowa) – nic nie jest zapisywane.</param>
/// <param name="Notes">Uwagi bez blokowania (np. brak poczty w pliku).</param>
public sealed record SetupPlan(JsonObject Settings, IReadOnlyList<SensorSetup> Sensors, IReadOnlyList<string> Problems, IReadOnlyList<string> Notes)
{
    internal string? GmailPassword { get; init; }
    internal string? HealthchecksPingUrl { get; init; }

    public bool SetsGmailPassword => GmailPassword is not null;
    public bool SetsHealthchecksPingUrl => HealthchecksPingUrl is not null;

    public override string ToString() =>
        $"SetupPlan {{ czujników {Sensors.Count}, hasło Gmaila {(SetsGmailPassword ? "tak" : "nie")}, strażnik {(SetsHealthchecksPingUrl ? "tak" : "nie")}, problemów {Problems.Count} }}";
}

/// <summary>
/// Zapis pliku konfiguracji chmury (<see cref="CloudSetup"/>) w chmurze (ARCHITEKTURA.md, zasada 15): ustawienia i czujniki
/// są nadpisywane, nic nie jest usuwane – czujnik, którego nie ma w pliku, zostaje w chmurze bez zmian. Bezpieczne do
/// powtarzania; agenci przejmują nową konfigurację przy najbliższej synchronizacji.
/// </summary>
public static class CloudSetupWriter
{
    private static readonly JsonSerializerOptions TuyaJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <param name="setup">Plik już sprawdzony przez <see cref="CloudSetupLoader"/>.</param>
    /// <param name="healthchecksPingUrl">Adres sygnału strażnika z pliku administratora; null – bez zmian.</param>
    public static SetupPlan Plan(CloudSetup setup, string? healthchecksPingUrl)
    {
        var problems = new List<string>();
        var notes = new List<string>();

        var zone = setup.TimeZone ?? TimeZones.ToIana(TimeZoneInfo.Local);
        if (zone is null)
            problems.Add($"Nie da się ustalić nazwy IANA strefy czasowej tego komputera ({TimeZoneInfo.Local.Id}) – wpisz „timeZone” do pliku, np. Europe/Warsaw.");
        else
        {
            TimeZones.Resolve(zone, out var known);
            if (!known) problems.Add($"Strefa czasowa „{zone}” jest nieznana – podaj nazwę IANA, np. Europe/Warsaw.");
        }

        var settings = new JsonObject
        {
            ["timeZone"] = zone,
            ["readIntervalMinutes"] = setup.ReadIntervalMinutes,
            ["scanWindowSeconds"] = setup.ScanWindowSeconds,
            ["reportTimes"] = new JsonArray([.. setup.Reports.Times.Select(t => (JsonNode)JsonValue.Create(t)!)]),
            ["reportCatchUpHours"] = setup.Reports.CatchUpHours,
            ["alertReminderHours"] = setup.Alerts.ReminderHours,
            ["logRetentionDays"] = setup.Logs.RetentionDays
        };
        string? password = null;
        if (setup.Mail is { } mail)
        {
            settings["gmailAddress"] = mail.GmailAddress;
            settings["alertRecipients"] = Addresses(mail.AlertRecipients);
            settings["reportRecipients"] = Addresses(mail.ReportRecipients);
            settings["adminRecipients"] = Addresses(mail.AdminRecipients);
            password = mail.AppPassword;
        }
        else notes.Add("Brak sekcji „mail” – poczty w chmurze nie zmieniam (bez niej maile nie wychodzą).");

        var sensors = new List<SensorSetup>();
        foreach (var sensor in setup.Sensors)
        {
            var mac = BleAddress.TryParse(sensor.Tuya.Mac, out var address) ? BleAddress.Format(address) : sensor.Tuya.Mac;
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
    public static async Task ApplyAsync(NpgsqlDataSource dataSource, SetupPlan plan, CancellationToken cancellationToken)
    {
        if (plan.Problems.Count > 0) throw new InvalidOperationException("Plan ma problemy – nic nie zapisano.");
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

    /// <summary>Identyfikatory czujników już będących w chmurze – do pokazania, co zapis doda, a co nadpisze.</summary>
    public static async Task<IReadOnlyList<string>> CloudSensorIdsAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("select id from glebosaurus.sensors order by id");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var ids = new List<string>();
        while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetString(0));
        return ids;
    }

    private static JsonArray Addresses(IEnumerable<string> addresses) => new([.. addresses.Select(a => (JsonNode)JsonValue.Create(a)!)]);
}
