using Glebosaurus.Core.Config;
using Glebosaurus.Core.Domain;
using Glebosaurus.Core.Mail;
using Glebosaurus.Core.Reports;

namespace Glebosaurus.Core.Monitoring;

/// <summary>Dane o procesie agenta, których silnik nie zna (warstwa BLE, ścieżki plików).</summary>
public sealed record MonitorHostInfo(string BleBackend, string ConfigPath, string DatabasePath, string LogDirectory);

/// <summary>
/// Stan agenta dla panelu WWW i ekranu telefonu. Składany jawnie, pole po polu – nigdy przez serializację
/// <see cref="AgentConfig"/> – więc sekrety (klucz lokalny i deviceId czujnika, hasło aplikacji Gmail) nie mają którędy wyjść.
/// </summary>
public sealed record MonitorStatus(
    MonitorSummary Monitor, IReadOnlyList<SensorStatus> Sensors, IReadOnlyList<AlertStatus> Alerts, MailStatus Mail, ReportsStatus Reports)
{
    /// <summary>Synchronizacja, agenci, nadawca, kopia odczytów – uzupełnia <see cref="Cloud.CloudPanel"/>.</summary>
    public Glebosaurus.Core.Cloud.CloudStatus? Cloud { get; init; }
}

/// <param name="ReadingNow">Nazwy czujników, których odczyt właśnie trwa.</param>
public sealed record MonitorSummary(
    string Name, string Version, string Machine, string BleBackend, string TimeZone, DateTimeOffset Now,
    DateTimeOffset? StartedAt, DateTimeOffset? LastCycleAt, string? LastCycleError, DateTimeOffset? LastCycleErrorAt,
    IReadOnlyList<string> ReadingNow, DateTimeOffset? ReadingSince,
    double ReadIntervalMinutes, int ScanWindowSeconds, int RetentionDays, int LogRetentionDays, double AlertReminderHours,
    string ConfigPath, string DatabasePath, string LogDirectory);

public sealed record ReadingStatus(DateTimeOffset At, int? MoisturePercent, decimal? SoilTemperatureCelsius, int? BatteryPercent, int? BatteryState)
{
    public static ReadingStatus From(StoredReading r) => new(r.At, r.MoisturePercent, r.SoilTemperatureCelsius, r.BatteryPercent, r.BatteryState);
}

/// <param name="MoistureTone">Kolor wilgotności względem progów, jak w mailach: red (poniżej progu podlania), amber (strefa histerezy), green.</param>
/// <param name="NextReadAt">Najwcześniejszy następny odczyt (w przeszłości = w najbliższym cyklu).</param>
/// <param name="LastResult">Wynik ostatniej próby tego agenta od startu: ok, notFound, unbound, error; null = jeszcze nie próbował.</param>
/// <param name="ReadRequested">Odczyt zlecony na żądanie – panel czeka na nowy odczyt.</param>
/// <param name="ReadOnRequestFrom">Od kiedy można zlecić odczyt na żądanie (ostatnia próba była przed chwilą); null = już można.</param>
public sealed record SensorStatus(
    string Id, string Name, string Mac, string? ProductId,
    double ReadIntervalMinutes, int WateringBelowPercent, int WateringClearAbovePercent, int ConsecutiveReadings,
    int LowBatteryPercent, double SilentAfterHours,
    ReadingStatus? Latest, string? MoistureTone, bool BatteryLow, DateTimeOffset? NextReadAt, bool ReadingNow,
    DateTimeOffset? LastAttemptAt, string? LastResult, string? LastResultText, int Failures, DateTimeOffset? RetryAt,
    int? LastSignalDbm, DateTimeOffset? LastSignalAt, IReadOnlyList<string> OpenAlerts,
    bool ReadRequested, DateTimeOffset? ReadOnRequestFrom)
{
    // Kto ma dzierżawę czujnika i kto poza tym agentem słyszy go najlepiej (uzupełnia CloudPanel).

    /// <summary>Agent z dzierżawą (tylko on czyta czujnik); null – nikt.</summary>
    public string? Holder { get; init; }

    public bool HolderIsMe { get; init; }

    public DateTimeOffset? HolderUntil { get; init; }

    public string? BestOtherAgent { get; init; }

    public int? BestOtherRssi { get; init; }
}

public sealed record AlertStatus(
    long Id, string SensorId, string SensorName, string Kind, string KindText, string Detail, DateTimeOffset OpenedAt, DateTimeOffset? LastNotifiedAt);

/// <param name="Configured">Ten agent jest nadawcą i zna ustawienia poczty z chmury.</param>
/// <param name="ReportRecipients">Faktyczni odbiorcy raportów (pusta lista w konfiguracji = odbiorcy alertów).</param>
public sealed record MailStatus(
    bool Configured, string? Sender, IReadOnlyList<string> AlertRecipients, IReadOnlyList<string> ReportRecipients,
    IReadOnlyList<string> AdminRecipients, DateTimeOffset? LastSentAt, string? LastError, DateTimeOffset? LastErrorAt, DateTimeOffset? RetryAt)
{
    public static readonly MailStatus NotConfigured = new(false, null, [], [], [], null, null, null, null);
}

public sealed record ReportsStatus(IReadOnlyList<string> Times, double CatchUpHours, DateTimeOffset? NextAt, ReportOnRequestStatus OnRequest);

/// <param name="Available">Czy można zlecić raport na żądanie (jest nadawca).</param>
/// <param name="Requested">Raport na żądanie czeka na wysyłkę.</param>
/// <param name="LastAttemptAt">Ostatnie zlecenie raportu na żądanie z tego panelu (od startu agenta).</param>
/// <param name="LastError">Błąd ostatniego zlecenia; null = zlecony.</param>
/// <param name="AvailableFrom">Od kiedy można zlecić kolejny raport (odstęp po poprzednim); null = już można.</param>
public sealed record ReportOnRequestStatus(bool Available, bool Requested, DateTimeOffset? LastAttemptAt, string? LastError, DateTimeOffset? AvailableFrom)
{
    public static readonly ReportOnRequestStatus Unavailable = new(false, false, null, null, null);
}

/// <summary>
/// Wspólna część statusu: czujniki z konfiguracji, ich odczyty z lokalnej bazy, migawka zbieracza i otwarte alerty.
/// Pocztę, raport na żądanie, dzierżawy i stan chmury dokłada <see cref="Cloud.CloudPanel"/>.
/// </summary>
public static class MonitorStatusBuilder
{
    /// <summary>Ten sam czujnik można zlecić do odczytu najwyżej raz na tyle (bateria czujnika).</summary>
    public static readonly TimeSpan ReadOnRequestCooldown = TimeSpan.FromMinutes(1);

    /// <param name="openAlerts">Otwarte alerty ze wspólnego stanu w chmurze.</param>
    public static async Task<MonitorStatus> BuildAsync(
        AgentConfig config, IMonitorStore store, MonitorRuntimeState state, MonitorHostInfo host,
        DateTimeOffset now, TimeZoneInfo zone, IReadOnlyList<AlertRecord> openAlerts, CancellationToken cancellationToken)
    {
        var open = openAlerts;
        var names = config.Sensors.ToDictionary(s => s.Id, s => s.Name, StringComparer.Ordinal);

        var sensors = new List<SensorStatus>();
        foreach (var sensor in config.Sensors)
        {
            var latest = await store.GetLatestAsync(sensor.Id, cancellationToken);
            var runtime = state.Sensors.GetValueOrDefault(sensor.Id) ?? SensorRuntimeState.Unknown;
            var interval = TimeSpan.FromMinutes(sensor.ReadIntervalMinutes ?? config.ReadIntervalMinutes);
            DateTimeOffset nextRead = latest is null ? now : latest.At + interval;
            if (runtime.RetryAt is { } retry && retry > nextRead) nextRead = retry;
            var requested = state.RequestedReads.Contains(sensor.Id);
            if (requested) nextRead = now;
            DateTimeOffset? onRequestFrom = runtime.LastAttemptAt is { } attempt && attempt + ReadOnRequestCooldown > now
                ? attempt + ReadOnRequestCooldown : null;

            sensors.Add(new(
                sensor.Id, sensor.Name, sensor.Tuya.Mac, sensor.Tuya.ProductId,
                interval.TotalMinutes, sensor.WateringBelowPercent, sensor.WateringClearAbovePercent, sensor.ConsecutiveReadings,
                sensor.LowBatteryPercent, sensor.SilentAfterHours,
                latest is null ? null : ReadingStatus.From(latest),
                latest?.MoisturePercent is { } moisture ? MoistureTone(sensor, moisture) : null,
                latest?.BatteryPercent is { } battery && sensor.IsBatteryLow(battery),
                nextRead, state.ReadingNow.Contains(sensor.Id),
                runtime.LastAttemptAt, ResultCode(runtime.LastResult), ResultText(runtime.LastResult, runtime.LastError),
                runtime.Failures, runtime.RetryAt, runtime.LastSignalDbm, runtime.LastSignalAt,
                open.Where(a => a.SensorId == sensor.Id).Select(a => MailTemplates.KindText(a.Kind)).ToList(),
                requested, onRequestFrom));
        }

        var alerts = open
            .OrderBy(a => a.OpenedAt)
            .Select(a => new AlertStatus(a.Id, a.SensorId, names.GetValueOrDefault(a.SensorId, a.SensorId), a.Kind.ToString(),
                MailTemplates.KindText(a.Kind), a.Detail, a.OpenedAt, a.LastNotifiedAt))
            .ToList();

        var monitor = new MonitorSummary(
            config.MonitorName, AppInfo.Version, Environment.MachineName, host.BleBackend, zone.Id, now,
            state.StartedAt, state.LastCycleAt, state.LastCycleError, state.LastCycleErrorAt,
            state.ReadingNow.Select(id => names.GetValueOrDefault(id, id)).ToList(), state.ReadingSince,
            config.ReadIntervalMinutes, config.ScanWindowSeconds, config.RetentionDays, config.Logs.RetentionDays, config.Alerts.ReminderHours,
            host.ConfigPath, host.DatabasePath, host.LogDirectory);

        var reports = new ReportsStatus(config.Reports.Times, config.Reports.CatchUpHours, ReportSchedule.Next(config.Reports.Times, now, zone),
            ReportOnRequestStatus.Unavailable);
        return new(monitor, sensors, alerts, MailStatus.NotConfigured, reports);
    }

    /// <summary>Ostatnie odczyty czujnika, od najnowszego.</summary>
    public static async Task<IReadOnlyList<ReadingStatus>> RecentReadingsAsync(IMonitorStore store, string sensorId, int count, CancellationToken cancellationToken) =>
        (await store.GetRecentAsync(sensorId, count, cancellationToken)).Select(ReadingStatus.From).ToList();

    /// <summary>Te same progi co kolor wilgotności w mailach (<c>MailLayout.MoistureTone</c>).</summary>
    private static string MoistureTone(SensorConfig sensor, int moisture) =>
        moisture < sensor.WateringBelowPercent ? "red" : moisture < sensor.WateringClearAbovePercent ? "amber" : "green";

    private static string? ResultCode(SensorReadFailure? result) => result switch
    {
        null => null,
        SensorReadFailure.None => "ok",
        SensorReadFailure.NotFound => "notFound",
        SensorReadFailure.Unbound => "unbound",
        _ => "error"
    };

    private static string? ResultText(SensorReadFailure? result, string? error) => result switch
    {
        null => null,
        SensorReadFailure.None => "udany odczyt",
        SensorReadFailure.NotFound => "nie znaleziono w reklamach BLE (poza zasięgiem lub czujnik śpi)",
        SensorReadFailure.Unbound => "czujnik niesparowany (np. po resecie przyciskiem) – potrzebne „glebosaurus pair”",
        _ => $"błąd odczytu: {error ?? "nieznany"}"
    };
}
