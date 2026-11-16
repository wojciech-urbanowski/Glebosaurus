using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Glebosaurus.Core.Config;
using Glebosaurus.Core.Monitoring;
using Microsoft.Extensions.Logging;

namespace Glebosaurus.Core.Cloud;

public sealed record CollectorOptions
{
    public TimeSpan TickInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Co ile nasłuch wszystkich czujników z konfiguracji (słyszenia do przydziału)…</summary>
    public TimeSpan HearEvery { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>…i jak długi. Sparowany czujnik nadaje rzadko (pakiet co kilka–kilkanaście sekund).</summary>
    public TimeSpan HearWindow { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Zapas dzierżawy na każdy czujnik wcześniej w tej samej kolejce odczytów (odczyt trwa 11–14 s).</summary>
    public TimeSpan PerQueuedSensor { get; init; } = TimeSpan.FromSeconds(20);

    public TimeSpan LogRetryDelay { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Dłuższy log jest obcinany od początku (jak załącznik logu administratora).</summary>
    public int MaxLogCharacters { get; init; } = 5_000_000;
}

/// <summary>
/// Zbieracz w trybie chmurowym (ARCHITEKTURA.md, zasada 14, kawałek d): czyta tylko czujniki, na które ma dzierżawę (z
/// zapasem na nasłuch i kolejkę), co kilka minut słucha wszystkich czujników z konfiguracji (słyszenia do przydziału),
/// zapisuje odczyty do lokalnego bufora (wysyła je <see cref="CloudSync"/>), wykonuje polecenia <c>read_now</c>,
/// <c>release</c> i <c>send_log</c> i raz dziennie wysyła dziennik z poprzedniego dnia (nadawca zrobi z niego log
/// administratora). Alertów nie liczy – to robi nadawca z odczytów wszystkich agentów.
/// </summary>
public sealed class CollectorEngine(
    Func<CloudAgentConfig?> config, IMonitorStore store, ISensorReader reader, ISensorListener listener,
    SensorLeasePolicy leases, SensorHearingTracker hearing, ICloudApi api, ILogArchive? logs,
    TimeProvider clock, ILogger logger, CollectorOptions? options = null)
{
    private static readonly TimeSpan UnboundRetryDelay = TimeSpan.FromMinutes(30);

    private readonly CollectorOptions settings = options ?? new();
    private readonly Dictionary<string, Attempt> attempts = new(StringComparer.Ordinal);
    private readonly Lock gate = new();
    private readonly Dictionary<string, List<long>> requested = new(StringComparer.Ordinal);
    private readonly List<(long Id, JsonObject Result)> unreported = [];
    private TaskCompletionSource wakeSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DateTimeOffset lastHearAt = DateTimeOffset.MinValue;
    private DateOnly? logUploadedFor;
    private DateTimeOffset logRetryAt = DateTimeOffset.MinValue;
    private DateTimeOffset nextPurgeAt = DateTimeOffset.MinValue;

    private sealed record Attempt(DateTimeOffset NextAttemptAt, int Failures);

    /// <summary>Po zapisaniu nowych odczytów (np. obudzenie synchronizacji).</summary>
    public Action? ReadingsStored { get; set; }

    /// <summary>Po oddaniu czujnika poleceniem (synchronizacja przekaże oddanie od razu).</summary>
    public Action? LeasesChanged { get; set; }

    public void Wake()
    {
        lock (gate) wakeSignal.TrySetResult();
    }

    /// <summary>
    /// Polecenie z chmury. <c>read_now</c> trafia do kolejki najbliższego cyklu (bez względu na odstęp odczytów),
    /// <c>release</c> oddaje czujnik od razu, <c>send_log</c> wysyła dziennik. Wynik idzie do chmury (<c>command_done</c>).
    /// </summary>
    /// <returns>false – polecenie nie jest dla zbieracza (np. <c>report_now</c>, <c>pair</c>).</returns>
    public async Task<bool> HandleCommandAsync(CloudCommand command, CancellationToken cancellationToken)
    {
        var current = config();
        switch (command.Kind)
        {
            case "read_now":
                if (command.Sensor is not { } sensorId || current?.Config.Sensors.Any(s => s.Id == sensorId) != true)
                {
                    await ReportAsync(command.Id, Failure("agent nie zna tego czujnika (wyłączony albo bez danych Tuya)"), cancellationToken);
                    return true;
                }
                lock (gate)
                {
                    if (!requested.TryGetValue(sensorId, out var ids)) requested[sensorId] = ids = [];
                    ids.Add(command.Id);
                    wakeSignal.TrySetResult();
                }
                logger.LogInformation("Polecenie z chmury: odczyt {Sensor} na żądanie.", sensorId);
                return true;

            case "release":
                if (command.Sensor is { } released)
                {
                    leases.Release(released);
                    LeasesChanged?.Invoke();
                    logger.LogInformation("Polecenie z chmury: oddaję czujnik {Sensor}.", released);
                }
                await ReportAsync(command.Id, new JsonObject { ["ok"] = true }, cancellationToken);
                return true;

            case "send_log":
                var day = RequestedDay(command) ?? (current is null ? DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime) : LocalToday(current));
                JsonObject result;
                try
                {
                    result = await UploadLogAsync(day, cancellationToken) is { } size
                        ? new JsonObject { ["ok"] = true, ["day"] = Day(day), ["sizeBytes"] = size }
                        : Failure($"brak dziennika z {Day(day)}");
                }
                catch (CloudApiException e)
                {
                    result = Failure(e.Message);
                }
                await ReportAsync(command.Id, result, cancellationToken);
                return true;

            default:
                return false;
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await CycleAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Cykl zbieracza zakończył się błędem; kolejna próba za chwilę.");
            }
            try { await WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
        }
    }

    /// <summary>Jeden obrót: nasłuch (co kilka minut), odczyty należne i zlecone, dziennik dnia poprzedniego, sprzątanie.</summary>
    public async Task CycleAsync(CancellationToken cancellationToken)
    {
        if (config() is not { } current) return;
        var agent = current.Config;
        await RetryReportsAsync(cancellationToken);

        if (clock.GetUtcNow() - lastHearAt >= settings.HearEvery) await HearAsync(agent, cancellationToken);

        Dictionary<string, List<long>> onRequest;
        lock (gate)
        {
            onRequest = new(requested, StringComparer.Ordinal);
            requested.Clear();
        }
        var due = await DueAsync(agent, onRequest, cancellationToken);
        if (due.Count > 0) await ReadAsync(agent, due, onRequest, cancellationToken);
        foreach (var (sensorId, ids) in onRequest.Where(r => due.All(d => d.Id != r.Key)))
            foreach (var id in ids)
                await ReportAsync(id, Failure("agent nie ma teraz ważnej dzierżawy tego czujnika"), cancellationToken);

        await UploadDailyLogAsync(current, cancellationToken);
        await PurgeAsync(agent, cancellationToken);
    }

    private async Task HearAsync(AgentConfig agent, CancellationToken cancellationToken)
    {
        lastHearAt = clock.GetUtcNow();
        if (agent.Sensors.Count == 0) return;
        IReadOnlyList<HeardSensor> heard;
        try
        {
            heard = await listener.HearAsync(agent.Sensors, settings.HearWindow, cancellationToken);
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Nasłuch BLE (kto słyszy które czujniki) przerwany błędem: {Error}; kolejna próba za {Minutes} min.",
                e.Message, settings.HearEvery.TotalMinutes);
            return;
        }
        foreach (var sensor in heard) hearing.RecordHeard(sensor.SensorId, sensor.Rssi);
        logger.LogDebug("Nasłuch: słyszę {Heard} z {Count} czujników.", heard.Count, agent.Sensors.Count);
    }

    /// <summary>
    /// Do odczytu: czujniki z dzierżawą ważną co najmniej na odczyt, nasłuch i kolejkę przed nimi; zlecone – od razu,
    /// pozostałe – po odstępie odczytów i bez wstrzymania po porażkach.
    /// </summary>
    private async Task<List<SensorConfig>> DueAsync(AgentConfig agent, IReadOnlyDictionary<string, List<long>> onRequest,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var due = new List<SensorConfig>();
        foreach (var sensor in agent.Sensors)
        {
            if (!onRequest.ContainsKey(sensor.Id))
            {
                if (attempts.TryGetValue(sensor.Id, out var attempt) && attempt.NextAttemptAt > now) continue;
                var latest = await store.GetLatestAsync(sensor.Id, cancellationToken);
                if (latest is not null && now - latest.At < Interval(agent, sensor)) continue;
            }
            if (!leases.CanRead(sensor.Id, TimeSpan.FromSeconds(agent.ScanWindowSeconds) + settings.PerQueuedSensor * due.Count)) continue;
            due.Add(sensor);
        }
        return due;
    }

    private async Task ReadAsync(AgentConfig agent, IReadOnlyList<SensorConfig> due, IReadOnlyDictionary<string, List<long>> onRequest,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Odczyt czujników: {Names}.", string.Join(", ", due.Select(s => s.Name)));
        var reported = new HashSet<string>(StringComparer.Ordinal);
        var stored = false;
        await foreach (var outcome in reader.ReadAsync(due, TimeSpan.FromSeconds(agent.ScanWindowSeconds), cancellationToken))
        {
            var sensor = outcome.Sensor;
            reported.Add(sensor.Id);
            var now = clock.GetUtcNow();
            if (outcome.Failure != SensorReadFailure.NotFound) hearing.RecordHeard(sensor.Id, outcome.SignalDbm);
            foreach (var note in outcome.Notes) logger.LogWarning("{Sensor}: uwaga z sesji czujnika: {Note}", sensor.Name, note);

            JsonObject result;
            if (outcome is { Failure: SensorReadFailure.None, Reading: { } reading } && (reading.MoisturePercent is not null || reading.SoilTemperatureCelsius is not null))
            {
                // Zaległe rekordy czujnik kasuje po odebraniu – od razu do bazy; rekord z przyszłości to zły zegar czujnika.
                foreach (var earlier in outcome.Earlier.Where(e => e.At < now))
                    await store.AddReadingAsync(new(sensor.Id, earlier.At, earlier.MoisturePercent, earlier.SoilTemperatureCelsius,
                        earlier.BatteryPercent, earlier.BatteryState), cancellationToken);
                await store.AddReadingAsync(new(sensor.Id, now, reading.MoisturePercent, reading.SoilTemperatureCelsius,
                    reading.BatteryPercent, reading.BatteryState), cancellationToken);
                stored = true;
                attempts.Remove(sensor.Id);
                hearing.RecordAttempt(sensor.Id, success: true);
                logger.LogInformation("{Sensor}: wilgotność {Moisture}%, temperatura {Temperature} °C, bateria {Battery}%, sygnał {Signal}.",
                    sensor.Name, reading.MoisturePercent?.ToString(CultureInfo.InvariantCulture) ?? "–",
                    reading.SoilTemperatureCelsius?.ToString("0.0", CultureInfo.InvariantCulture) ?? "–",
                    reading.BatteryPercent?.ToString(CultureInfo.InvariantCulture) ?? "–", SignalStats.Format(outcome.SignalDbm));
                result = new JsonObject
                {
                    ["ok"] = true, ["at"] = now.ToString("O", CultureInfo.InvariantCulture), ["moisture"] = reading.MoisturePercent,
                    ["battery"] = reading.BatteryPercent
                };
            }
            else
            {
                var error = outcome.Failure switch
                {
                    SensorReadFailure.NotFound => $"nie znaleziono w reklamach BLE w oknie {agent.ScanWindowSeconds} s",
                    SensorReadFailure.Unbound => "czujnik jest niesparowany (np. po resecie przyciskiem) – potrzebne ponowne parowanie",
                    _ => outcome.Error ?? "czujnik nie zgłosił pomiarów"
                };
                RegisterFailure(agent, sensor, now, outcome.Failure == SensorReadFailure.Unbound ? UnboundRetryDelay : null);
                hearing.RecordAttempt(sensor.Id, success: false);
                if (outcome.Failure == SensorReadFailure.Unbound) logger.LogError("{Sensor}: {Error}.", sensor.Name, error);
                else logger.LogWarning("{Sensor}: {Error} (sygnał {Signal}).", sensor.Name, error, SignalStats.Format(outcome.SignalDbm));
                result = Failure(error);
            }
            if (onRequest.TryGetValue(sensor.Id, out var ids))
                foreach (var id in ids) await ReportAsync(id, result, cancellationToken);
        }

        foreach (var sensor in due.Where(s => !reported.Contains(s.Id)))
        {
            RegisterFailure(agent, sensor, clock.GetUtcNow());
            hearing.RecordAttempt(sensor.Id, success: false);
            logger.LogWarning("{Sensor}: warstwa BLE nie zwróciła wyniku.", sensor.Name);
            if (onRequest.TryGetValue(sensor.Id, out var ids))
                foreach (var id in ids) await ReportAsync(id, Failure("warstwa BLE nie zwróciła wyniku"), cancellationToken);
        }
        if (stored) ReadingsStored?.Invoke();
    }

    private static TimeSpan Interval(AgentConfig agent, SensorConfig sensor) =>
        TimeSpan.FromMinutes(sensor.ReadIntervalMinutes ?? agent.ReadIntervalMinutes);

    /// <summary>Wykładniczy odstęp ponowień (1, 2, 4, 8, do 10 min), nigdy dłuższy niż zwykły odstęp odczytów – jak w trybie samodzielnym.</summary>
    private void RegisterFailure(AgentConfig agent, SensorConfig sensor, DateTimeOffset now, TimeSpan? fixedDelay = null)
    {
        var failures = attempts.TryGetValue(sensor.Id, out var previous) ? previous.Failures + 1 : 1;
        var delay = fixedDelay ?? TimeSpan.FromMinutes(Math.Min(Math.Pow(2, failures - 1), Math.Min(10, Interval(agent, sensor).TotalMinutes)));
        attempts[sensor.Id] = new(now + delay, failures);
    }

    /// <summary>Raz dziennie (pierwszy cykl nowego dnia w strefie raportów) – dziennik z całego poprzedniego dnia do chmury.</summary>
    private async Task UploadDailyLogAsync(CloudAgentConfig current, CancellationToken cancellationToken)
    {
        if (logs is null) return;
        var now = clock.GetUtcNow();
        var yesterday = LocalToday(current).AddDays(-1);
        if (logUploadedFor == yesterday || now < logRetryAt) return;
        try
        {
            var size = await UploadLogAsync(yesterday, cancellationToken);
            logUploadedFor = yesterday;
            if (size is { } bytes) logger.LogInformation("Wysłano do chmury dziennik z {Day} ({Size} B po spakowaniu).", Day(yesterday), bytes);
        }
        catch (CloudApiException e)
        {
            logRetryAt = now + settings.LogRetryDelay;
            logger.LogWarning("Nie udało się wysłać dziennika z {Day} do chmury: {Error}; ponowię za {Minutes} min.",
                Day(yesterday), e.Message, settings.LogRetryDelay.TotalMinutes);
        }
    }

    /// <returns>Rozmiar po spakowaniu; null – tego dnia nie ma dziennika.</returns>
    private async Task<int?> UploadLogAsync(DateOnly day, CancellationToken cancellationToken)
    {
        if (logs is null) return null;
        var text = await logs.ReadDayAsync(day, cancellationToken);
        if (text is null) return null;
        if (text.Length > settings.MaxLogCharacters)
            text = "[… początek dziennika obcięty …]\n" + text[^settings.MaxLogCharacters..];
        using var buffer = new MemoryStream();
        await using (var gzip = new GZipStream(buffer, CompressionLevel.SmallestSize))
            await gzip.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken);
        return (await api.LogPutAsync(day, buffer.ToArray(), cancellationToken)).SizeBytes;
    }

    private async Task PurgeAsync(AgentConfig agent, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        if (now < nextPurgeAt) return;
        nextPurgeAt = now.AddHours(24);
        await store.PurgeAsync(now.AddDays(-agent.RetentionDays), cancellationToken);
    }

    /// <summary>Wynik polecenia do chmury; nieudane zgłoszenie – ponowienie w następnym cyklu.</summary>
    private async Task ReportAsync(long commandId, JsonObject result, CancellationToken cancellationToken)
    {
        try
        {
            await api.CommandDoneAsync(commandId, result, cancellationToken);
        }
        catch (CloudApiException e) when (e.Kind is CloudErrorKind.Unavailable)
        {
            lock (gate) unreported.Add((commandId, result));
            logger.LogWarning("Nie udało się zgłosić wyniku polecenia {Id}: {Error}; ponowię.", commandId, e.Message);
        }
        catch (CloudApiException e)
        {
            logger.LogWarning("Chmura nie przyjęła wyniku polecenia {Id}: {Error}.", commandId, e.Message);
        }
    }

    private async Task RetryReportsAsync(CancellationToken cancellationToken)
    {
        List<(long Id, JsonObject Result)> pending;
        lock (gate)
        {
            pending = [.. unreported];
            unreported.Clear();
        }
        foreach (var (id, result) in pending) await ReportAsync(id, result, cancellationToken);
    }

    private DateOnly LocalToday(CloudAgentConfig current) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), current.TimeZone).DateTime);

    private static DateOnly? RequestedDay(CloudCommand command) =>
        command.Params?["day"] is JsonValue value && value.TryGetValue<string>(out var text)
        && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;

    private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static JsonObject Failure(string error) => new() { ["ok"] = false, ["error"] = error };

    private async Task WaitAsync(CancellationToken cancellationToken)
    {
        Task signal;
        lock (gate)
        {
            if (requested.Count > 0) return;
            if (wakeSignal.Task.IsCompleted) wakeSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            signal = wakeSignal.Task;
        }
        using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delay = Task.Delay(settings.TickInterval, clock, delayCancellation.Token);
        if (await Task.WhenAny(delay, signal) == delay) await delay;
        else await delayCancellation.CancelAsync();
        cancellationToken.ThrowIfCancellationRequested();
    }
}
