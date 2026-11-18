using System.Globalization;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;

namespace Glebosaurus.Cloud.Admin;

/// <summary>Czujnik z punktu widzenia odstępu odczytów: własny odstęp (null = wspólny) i próg alertu „brak odczytów”.</summary>
public sealed record CloudSensorTiming(string Id, string Name, double? ReadIntervalMinutes, double SilentAfterHours);

/// <summary>Wspólne ustawienia chmury (bez odbiorców i sekretów) i włączone czujniki – do pokazania przed zmianą.</summary>
public sealed record CloudSettingsInfo(string TimeZone, double ReadIntervalMinutes, int ScanWindowSeconds, IReadOnlyList<string> ReportTimes,
    double AlertReminderHours, int LogRetentionDays, long ConfigVersion, IReadOnlyList<CloudSensorTiming> Sensors);

/// <summary>
/// Ustawienia chmury bez pełnego <c>cloud import</c>: <c>cloud settings</c> czyta je i zmienia samo pole funkcją
/// <c>glebosaurus.admin_settings_update</c> (pozostałe ustawienia, czujniki i sekrety zostają). Zmiana podnosi wersję
/// konfiguracji, więc agenci pobierają ją przy najbliższej synchronizacji (ok. minuty).
/// </summary>
public static class CloudSettingsAdmin
{
    /// <summary>Najkrótszy odstęp, jaki przyjmuje chmura (ograniczenie kolumny).</summary>
    public const double MinReadIntervalMinutes = 0.5;

    /// <summary>Poniżej tego odstępu odczyty wyraźnie kosztują baterię (połączenie trwa ok. 11–14 s).</summary>
    public const double BatteryFriendlyMinutes = 15;

    public static async Task<CloudSettingsInfo> GetAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        // Jedna transakcja = ustawienia i czujniki z tej samej chwili.
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        var sensors = new List<CloudSensorTiming>();
        await using (var command = new NpgsqlCommand("""
            select id, name, read_interval_minutes::float8, silent_after_hours::float8 from glebosaurus.sensors where enabled order by id
            """, connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                sensors.Add(new(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetDouble(2), reader.GetDouble(3)));
        }
        await using (var command = new NpgsqlCommand("""
            select time_zone, read_interval_minutes::float8, scan_window_seconds, report_times, alert_reminder_hours::float8,
                   log_retention_days, config_version
              from glebosaurus.settings
            """, connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("W chmurze brakuje wiersza ustawień (glebosaurus.settings).");
            var info = new CloudSettingsInfo(reader.GetString(0), reader.GetDouble(1), reader.GetInt32(2), reader.GetFieldValue<string[]>(3),
                reader.GetDouble(4), reader.GetInt32(5), reader.GetInt64(6), sensors);
            await reader.CloseAsync();
            await transaction.CommitAsync(cancellationToken);
            return info;
        }
    }

    /// <summary>Ustawia wspólny odstęp odczytów; czujniki z własnym odstępem go zachowują.</summary>
    public static async Task SetReadIntervalAsync(NpgsqlDataSource dataSource, double minutes, CancellationToken cancellationToken)
    {
        if (!double.IsFinite(minutes) || minutes < MinReadIntervalMinutes)
            throw new ArgumentOutOfRangeException(nameof(minutes), minutes, $"Odstęp odczytów: co najmniej {MinReadIntervalMinutes.ToString(CultureInfo.InvariantCulture)} min.");
        await using var command = dataSource.CreateCommand("select glebosaurus.admin_settings_update(@p)");
        command.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Jsonb) { Value = new JsonObject { ["readIntervalMinutes"] = minutes }.ToJsonString() });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Co warto wiedzieć przed zmianą wspólnego odstępu: czujniki z własnym odstępem (zmiana ich nie dotyczy) i czujniki,
    /// którym alert „brak odczytów” przyjdzie wcześniej niż kolejny odczyt (próg nie dłuższy niż odstęp).
    /// </summary>
    public static IReadOnlyList<string> ReadIntervalNotes(CloudSettingsInfo current, double minutes)
    {
        var notes = new List<string>();
        if (minutes < BatteryFriendlyMinutes)
            notes.Add($"odstęp krótszy niż {BatteryFriendlyMinutes:0} min wyraźnie skraca życie baterii (każdy odczyt to ok. 11–14 s połączenia).");
        foreach (var sensor in current.Sensors)
        {
            if (sensor.ReadIntervalMinutes is { } own)
            {
                notes.Add($"czujnik {sensor.Id} „{sensor.Name}” ma własny odstęp {Minutes(own)} – zostaje bez zmian.");
                continue;
            }
            if (sensor.SilentAfterHours * 60 <= minutes)
                notes.Add($"czujnik {sensor.Id} „{sensor.Name}”: alert „brak odczytów” po {sensor.SilentAfterHours.ToString(CultureInfo.InvariantCulture)} h " +
                          $"przyjdzie przed kolejnym odczytem – próg (silentAfterHours) powinien być dłuższy niż odstęp.");
        }
        return notes;
    }

    /// <summary>Odstęp czytelnie: „15 min”, „2 h 5 min”, „6 h”.</summary>
    public static string Minutes(double minutes)
    {
        if (minutes < 60 || minutes % 1 != 0) return $"{minutes.ToString("0.##", CultureInfo.InvariantCulture)} min";
        var hours = (int)(minutes / 60);
        var rest = (int)(minutes % 60);
        return rest == 0 ? $"{hours} h" : $"{hours} h {rest} min";
    }
}
