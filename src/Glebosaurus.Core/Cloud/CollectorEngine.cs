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
    /// <summary>Obrót pętli: sprawdzenie, co jest do odczytu. Polecenia (<c>read_now</c>) budzą ją od razu, osobnym sygnałem.</summary>
    public TimeSpan TickInterval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Co ile nasłuch wszystkich czujników z konfiguracji (słyszenia do przydziału)… Nasłuch rusza w pierwszym obrocie po
    /// upływie tego czasu, więc faktycznie co <c>HearEvery</c> + do jednego <see cref="TickInterval"/> (4,5–5 min). Dwa
    /// kolejne nasłuchy muszą się mieścić w <see cref="SensorLeaseOptions.HeardWithin"/> (10 min) – jeden niesłyszany
    /// nasłuch nie może zerować czekania na wolny czujnik (ARCHITEKTURA.md, „Zależności czasów”).
    /// </summary>
    public TimeSpan HearEvery { get; init; } = TimeSpan.FromMinutes(4);

    /// <summary>…i jak długi. Sparowany czujnik nadaje rzadko (pakiet co kilka–kilkanaście sekund).</summary>
    public TimeSpan HearWindow { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Kolejne odstępy po nieudanych odczytach (ostatni się powtarza), nigdy dłuższe niż zwykły odstęp odczytów czujnika.
    /// Każda nieudana próba, która doszła do połączenia, kosztuje baterię czujnika.
    /// </summary>
    public IReadOnlyList<TimeSpan> RetryDelays { get; init; } =
        [TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10)];

    /// <summary>Sesja urwana w trakcie historii czujnika: reszta i stan bieżący po krótkiej przerwie (czujnik właśnie był
    /// połączony do 3 min) – bez liczenia porażki.</summary>
    public TimeSpan IncompleteRetryDelay { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Zapas dzierżawy na każdy czujnik wcześniej w tej samej kolejce odczytów (odczyt trwa 11–14 s).</summary>
    public TimeSpan PerQueuedSensor { get; init; } = TimeSpan.FromSeconds(20);

    public TimeSpan LogRetryDelay { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Dłuższy log jest obcinany od początku (jak załącznik logu administratora).</summary>
    public int MaxLogCharacters { get; init; } = 5_000_000;
}

/// <summary>
/// Zbieracz (ARCHITEKTURA.md, zasada 14, kawałek d): czyta tylko czujniki, na które ma dzierżawę (z
/// zapasem na nasłuch i kolejkę), co kilka minut słucha wszystkich czujników z konfiguracji (słyszenia do przydziału),
/// zapisuje odczyty do lokalnego bufora (wysyła je <see cref="CloudSync"/>), wykonuje polecenia <c>read_now</c>,
/// <c>release</c> i <c>send_log</c> i raz dziennie wysyła dziennik z poprzedniego dnia (nadawca zrobi z niego log
/// administratora). Alertów nie liczy – to robi nadawca z odczytów wszystkich agentów.
/// </summary>
/// <param name="reader">Odczyt czujników; null razem z <paramref name="listener"/> – agent bez Bluetooth
/// (<see cref="AgentConfig.Bluetooth"/> = false): nie słucha i nie czyta, zlecony odczyt zgłasza jako niewykonalny, a
/// dziennik dnia poprzedniego, <c>send_log</c>, <c>release</c> i sprzątanie bazy działają jak zwykle.</param>
/// <param name="listener">Nasłuch wszystkich czujników (słyszenia do przydziału); null – agent bez Bluetooth.</param>
public sealed class CollectorEngine(
    Func<CloudAgentConfig?> config, IMonitorStore store, ISensorReader? reader, ISensorListener? listener,
    SensorLeasePolicy leases, SensorHearingTracker hearing, ICloudApi api, ILogArchive? logs,
    TimeProvider clock, ILogger logger, CollectorOptions? options = null)
{
    /// <summary>Wynik zleconego odczytu na agencie bez Bluetooth (chmura kieruje odczyt do właściciela dzierżawy, więc
    /// trafia tu tylko wyjątkowo – np. polecenie właściciela wskazujące tego agenta).</summary>
    public const string NoRadioError = "agent bez Bluetooth (\"bluetooth\": false) – nie czyta czujników";

    private readonly bool hasRadio = (reader is null) == (listener is null)
        ? reader is not null
        : throw new ArgumentException("Zbieracz ma radio do odczytu i nasłuchu albo nie ma go wcale (agent bez Bluetooth).", nameof(reader));

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

    // Tylko do migawki stanu dla lokalnego panelu (State); logika zbieracza z nich nie korzysta.
    private readonly Dictionary<string, (DateTimeOffset At, SensorReadFailure Result, string? Error)> lastOutcome = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Dbm, DateTimeOffset At)> lastSignal = new(StringComparer.Ordinal);
    private IReadOnlyList<string> readingNow = [];
    private DateTimeOffset? readingSince;
    private DateTimeOffset startedAt;
    private DateTimeOffset? lastCycleAt;
    private (string Message, DateTimeOffset At)? lastCycleError;
    private volatile MonitorRuntimeState state = MonitorRuntimeState.NotStarted;

    private sealed record Attempt(DateTimeOffset NextAttemptAt, int Failures);

    /// <summary>
    /// Migawka stanu dla lokalnego panelu (próby, ponowienia, sygnał, trwający odczyt), bez poczty (tę ma nadawca). Bezpieczna do czytania z innego wątku: podmieniana w całości po każdym kroku.
    /// </summary>
    public MonitorRuntimeState State => state;

    /// <summary>Czy agent ma radio (false – <c>"bluetooth": false</c>: nie słucha, nie czyta, nie ubiega się o czujniki).</summary>
    public bool HasRadio => hasRadio;

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
                if (!hasRadio)
                {
                    await ReportAsync(command.Id, Failure(NoRadioError), cancellationToken);
                    return true;
                }
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
        if (startedAt == default) startedAt = clock.GetUtcNow();
        try
        {
            await RetryReportsAsync(cancellationToken);

            if (hasRadio && clock.GetUtcNow() - lastHearAt >= settings.HearEvery) await HearAsync(agent, cancellationToken);

            Dictionary<string, List<long>> onRequest;
            lock (gate)
            {
                onRequest = new(requested, StringComparer.Ordinal);
                requested.Clear();
            }
            var due = hasRadio ? await DueAsync(agent, onRequest, cancellationToken) : [];
            if (due.Count > 0) await ReadAsync(agent, due, onRequest, cancellationToken);
            foreach (var (sensorId, ids) in onRequest.Where(r => due.All(d => d.Id != r.Key)))
                foreach (var id in ids)
                    await ReportAsync(id, Failure("agent nie ma teraz ważnej dzierżawy tego czujnika"), cancellationToken);

            await UploadDailyLogAsync(current, cancellationToken);
            await PurgeAsync(agent, cancellationToken);
            lastCycleAt = clock.GetUtcNow();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            lastCycleError = (e.Message, clock.GetUtcNow());
            throw;
        }
        finally
        {
            readingNow = [];
            readingSince = null;
            Publish(agent);
        }
    }

    /// <summary>Składa nową migawkę stanu z pól zbieracza i podmienia ją w całości (patrz <see cref="State"/>).</summary>
    private void Publish(AgentConfig agent)
    {
        var sensors = new Dictionary<string, SensorRuntimeState>(StringComparer.Ordinal);
        foreach (var sensor in agent.Sensors)
        {
            var hasOutcome = lastOutcome.TryGetValue(sensor.Id, out var outcome);
            var hasSignal = lastSignal.TryGetValue(sensor.Id, out var signal);
            var attempt = attempts.GetValueOrDefault(sensor.Id);
            sensors[sensor.Id] = new(
                hasOutcome ? outcome.At : null, hasOutcome ? outcome.Result : null, hasOutcome ? outcome.Error : null,
                attempt?.Failures ?? 0, attempt?.NextAttemptAt,
                hasSignal ? signal.Dbm : null, hasSignal ? signal.At : null);
        }
        state = new(startedAt == default ? null : startedAt, lastCycleAt, lastCycleError?.Message, lastCycleError?.At,
            readingNow, readingSince, sensors);
    }

    private async Task HearAsync(AgentConfig agent, CancellationToken cancellationToken)
    {
        lastHearAt = clock.GetUtcNow();
        if (agent.Sensors.Count == 0) return;
        IReadOnlyList<HeardSensor> heard;
        try
        {
            heard = await listener!.HearAsync(agent.Sensors, settings.HearWindow, cancellationToken);
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Nasłuch BLE (kto słyszy które czujniki) przerwany błędem: {Error}; kolejna próba za {Minutes} min.",
                e.Message, settings.HearEvery.TotalMinutes);
            return;
        }
        foreach (var sensor in heard)
        {
            hearing.RecordHeard(sensor.SensorId, sensor.Rssi);
            if (sensor.Rssi is { } rssi) lastSignal[sensor.SensorId] = (rssi, lastHearAt);
        }
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
            var requested = onRequest.ContainsKey(sensor.Id);
            if (!requested && attempts.TryGetValue(sensor.Id, out var attempt) && attempt.NextAttemptAt > now) continue;
            // Najpierw dzierżawa (w pamięci) – baza tylko dla czujników, które ten agent w ogóle może czytać.
            if (!leases.CanRead(sensor.Id, TimeSpan.FromSeconds(agent.ScanWindowSeconds) + settings.PerQueuedSensor * due.Count)) continue;
            if (!requested)
            {
                var latest = await store.GetLatestAsync(sensor.Id, cancellationToken);
                if (latest is not null && now - latest.At < Interval(agent, sensor)) continue;
            }
            due.Add(sensor);
        }
        return due;
    }

    private async Task ReadAsync(AgentConfig agent, IReadOnlyList<SensorConfig> due, IReadOnlyDictionary<string, List<long>> onRequest,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Odczyt czujników: {Names}.", string.Join(", ", due.Select(s => s.Name)));
        readingNow = [.. due.Select(s => s.Id)];
        readingSince = clock.GetUtcNow();
        Publish(agent);
        var reported = new HashSet<string>(StringComparer.Ordinal);
        var stored = false;
        await foreach (var outcome in reader!.ReadAsync(due, TimeSpan.FromSeconds(agent.ScanWindowSeconds), cancellationToken))
        {
            var sensor = outcome.Sensor;
            reported.Add(sensor.Id);
            var now = clock.GetUtcNow();
            if (outcome.Failure != SensorReadFailure.NotFound) hearing.RecordHeard(sensor.Id, outcome.SignalDbm);
            if (outcome.SignalDbm is { } dbm) lastSignal[sensor.Id] = (dbm, now);
            foreach (var note in outcome.Notes) logger.LogWarning("{Sensor}: uwaga z sesji czujnika: {Note}", sensor.Name, note);

            JsonObject result;
            if (outcome.Failure == SensorReadFailure.Incomplete)
            {
                // Historia zapisana, stanu bieżącego brak: połączenie działało (bez porażki w dzierżawie), szybko po resztę.
                var saved = await StoreEarlierAsync(sensor, outcome, now, cancellationToken);
                stored |= saved > 0;
                attempts[sensor.Id] = new(now + settings.IncompleteRetryDelay, 0);
                var incomplete = $"sesja urwana w trakcie historii czujnika – zapisano {saved} zaległych pomiarów, reszta i stan bieżący przy następnym połączeniu";
                lastOutcome[sensor.Id] = (now, SensorReadFailure.Error, incomplete);
                hearing.RecordAttempt(sensor.Id, success: true);
                logger.LogInformation("{Sensor}: {Message} (sygnał {Signal}).", sensor.Name, incomplete, SignalStats.Format(outcome.SignalDbm));
                result = Failure(incomplete);
            }
            else if (outcome is { Failure: SensorReadFailure.None, Reading: { } reading } && (reading.MoisturePercent is not null || reading.SoilTemperatureCelsius is not null))
            {
                await StoreEarlierAsync(sensor, outcome, now, cancellationToken);
                await store.AddReadingAsync(new(sensor.Id, now, reading.MoisturePercent, reading.SoilTemperatureCelsius,
                    reading.BatteryPercent, reading.BatteryState), cancellationToken);
                stored = true;
                attempts.Remove(sensor.Id);
                lastOutcome[sensor.Id] = (now, SensorReadFailure.None, null);
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
                lastOutcome[sensor.Id] = outcome.Failure is SensorReadFailure.NotFound or SensorReadFailure.Unbound
                    ? (now, outcome.Failure, null)
                    : (now, SensorReadFailure.Error, error);
                hearing.RecordAttempt(sensor.Id, success: false);
                if (outcome.Failure == SensorReadFailure.Unbound) logger.LogError("{Sensor}: {Error}.", sensor.Name, error);
                else logger.LogWarning("{Sensor}: {Error} (sygnał {Signal}).", sensor.Name, error, SignalStats.Format(outcome.SignalDbm));
                result = Failure(error);
            }
            if (onRequest.TryGetValue(sensor.Id, out var ids))
                foreach (var id in ids) await ReportAsync(id, result, cancellationToken);
            readingNow = [.. readingNow.Where(id => id != sensor.Id)];
            Publish(agent);
        }

        foreach (var sensor in due.Where(s => !reported.Contains(s.Id)))
        {
            lastOutcome[sensor.Id] = (clock.GetUtcNow(), SensorReadFailure.Error, "warstwa BLE nie zwróciła wyniku");
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

    /// <summary>Zaległe rekordy czujnik kasuje po odebraniu – od razu do bazy; rekord z przyszłości to zły zegar czujnika.</summary>
    private async Task<int> StoreEarlierAsync(SensorConfig sensor, SensorReadOutcome outcome, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var count = 0;
        foreach (var earlier in outcome.Earlier.Where(e => e.At < now))
        {
            await store.AddReadingAsync(new(sensor.Id, earlier.At, earlier.MoisturePercent, earlier.SoilTemperatureCelsius,
                earlier.BatteryPercent, earlier.BatteryState), cancellationToken);
            count++;
        }
        return count;
    }

    /// <summary>Odstęp ponowień z <see cref="CollectorOptions.RetryDelays"/> (2, 5, 10 min), nigdy dłuższy niż zwykły odstęp odczytów.</summary>
    private void RegisterFailure(AgentConfig agent, SensorConfig sensor, DateTimeOffset now, TimeSpan? fixedDelay = null)
    {
        var failures = attempts.TryGetValue(sensor.Id, out var previous) ? previous.Failures + 1 : 1;
        var delay = fixedDelay ?? RetryDelay(failures, Interval(agent, sensor));
        attempts[sensor.Id] = new(now + delay, failures);
    }

    private TimeSpan RetryDelay(int failures, TimeSpan interval)
    {
        var delay = settings.RetryDelays[Math.Clamp(failures - 1, 0, settings.RetryDelays.Count - 1)];
        return delay < interval ? delay : interval;
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
            if (!await api.CommandDoneAsync(commandId, result, cancellationToken))
                logger.LogWarning("Chmura nie przyjęła wyniku polecenia {Id} (wygasło albo nie było skierowane do tego agenta).", commandId);
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
