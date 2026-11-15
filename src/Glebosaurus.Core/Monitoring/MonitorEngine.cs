using Glebosaurus.Core.Alerts;
using Glebosaurus.Core.Config;
using Glebosaurus.Core.Domain;
using Glebosaurus.Core.Mail;
using Glebosaurus.Core.Reports;
using Microsoft.Extensions.Logging;

namespace Glebosaurus.Core.Monitoring;

/// <summary>
/// Pętla monitora: co kilkadziesiąt sekund sprawdza, które czujniki czekają na odczyt, czyta je, zapisuje wyniki,
/// przelicza alerty, wysyła maile i raporty. Radio, baza i poczta są za interfejsami; czas pochodzi z <see cref="TimeProvider"/>.
/// </summary>
public sealed class MonitorEngine(
    AgentConfig config, IMonitorStore store, ISensorReader reader, IMailGateway? mail,
    TimeProvider clock, ILogger logger, TimeZoneInfo? zone = null, ILogArchive? logs = null)
{
    /// <summary>Po nieudanej wysyłce poczty następna próba dopiero po tym czasie, żeby nie dobijać się co cykl.</summary>
    public static readonly TimeSpan MailRetryDelay = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan UnboundRetryDelay = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Odczyt na żądanie (panel WWW) najwcześniej tyle po ostatniej próbie tego czujnika – każde połączenie kosztuje baterię,
    /// a kilka kliknięć z rzędu nie powinno jej drenować.
    /// </summary>
    public static readonly TimeSpan ForcedReadCooldown = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Raport na żądanie (panel WWW) najwcześniej tyle po poprzedniej próbie – to prawdziwy mail do wszystkich odbiorców
    /// raportów, a kilka kliknięć z rzędu nie powinno zasypać skrzynek.
    /// </summary>
    public static readonly TimeSpan ReportOnRequestCooldown = TimeSpan.FromMinutes(1);

    private readonly TimeZoneInfo timeZone = zone ?? TimeZoneInfo.Local;
    private readonly Dictionary<string, Attempt> attempts = [];
    private readonly Dictionary<string, (int Dbm, DateTimeOffset At)> lastSignal = [];
    private readonly HashSet<long> warnedNoMail = [];
    private DateTimeOffset startedAt;
    private DateTimeOffset mailRetryAt = DateTimeOffset.MinValue;
    private DateTimeOffset nextPurgeAt = DateTimeOffset.MinValue;
    private bool warnedNoReportMail;
    private DateOnly adminLogDoneFor;
    private DateTimeOffset adminLogRetryAt = DateTimeOffset.MinValue;

    // Tylko do migawki stanu dla panelu WWW (State); logika silnika z nich nie korzysta.
    private readonly Dictionary<string, (DateTimeOffset At, SensorReadFailure Result, string? Error)> lastOutcome = [];
    private IReadOnlyList<string> readingNow = [];
    private DateTimeOffset? readingSince;
    private DateTimeOffset? lastCycleAt;
    private (string Message, DateTimeOffset At)? lastCycleError;
    private DateTimeOffset? lastMailSentAt;
    private (string Message, DateTimeOffset At)? lastMailError;
    private volatile MonitorRuntimeState state = MonitorRuntimeState.NotStarted;

    // Odczyty i raport na żądanie: zlecane z innego wątku (panel WWW), więc pod blokadą. Sygnał budzi pętlę przed końcem odstępu.
    private readonly Lock requestGate = new();
    private readonly List<string> requestedReads = [];
    private DateTimeOffset? reportRequestedAt;
    private DateTimeOffset? reportLastAttemptAt;
    private string? reportLastError;
    private TaskCompletionSource wakeSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TimeSpan TickInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Bieżąca migawka stanu (próby, ponowienia, sygnał, poczta, trwający odczyt). Bezpieczna do czytania z innego wątku –
    /// silnik podmienia ją w całości na nową po każdym kroku.
    /// </summary>
    public MonitorRuntimeState State
    {
        get
        {
            var current = state;
            lock (requestGate)
            {
                if (requestedReads.Count == 0 && reportRequestedAt is null && reportLastAttemptAt is null) return current;
                return current with
                {
                    RequestedReads = [.. requestedReads],
                    ReportOnRequest = new(reportRequestedAt, reportLastAttemptAt, reportLastError)
                };
            }
        }
    }

    /// <summary>
    /// Zleca raport mailowy poza harmonogramem – ten sam szablon i ci sami odbiorcy co raport zaplanowany. Można wołać z
    /// dowolnego wątku: pętla budzi się od razu i wysyła go w najbliższym cyklu, po odczytach, które ten cykl robi (raport
    /// pokazuje więc świeże dane). Harmonogramu nie zmienia: zaplanowane raporty idą dalej o swoich porach.
    /// </summary>
    public ReportRequestResult RequestReport()
    {
        if (mail is null || config.Mail is null) return new(ReportRequestStatus.MailNotConfigured);
        var now = clock.GetUtcNow();
        lock (requestGate)
        {
            if (reportRequestedAt is not null) return new(ReportRequestStatus.AlreadyQueued);
            if (reportLastAttemptAt is { } last && now - last < ReportOnRequestCooldown)
                return new(ReportRequestStatus.TooSoon, last + ReportOnRequestCooldown);
            reportRequestedAt = now;
            wakeSignal.TrySetResult();
            return new(ReportRequestStatus.Queued);
        }
    }

    /// <summary>
    /// Zleca odczyt wybranych czujników (null = wszystkich) poza harmonogramem, z pominięciem odstępu po porażkach. Można
    /// wołać z dowolnego wątku: czujniki trafiają do kolejki, a pętla budzi się od razu (albo zaraz po trwającym cyklu) –
    /// radio dalej obsługuje jeden odczyt naraz.
    /// </summary>
    public ReadRequestResult RequestRead(IReadOnlyCollection<string>? sensorIds)
    {
        var now = clock.GetUtcNow();
        var snapshot = state;
        var ids = sensorIds ?? config.Sensors.Select(s => s.Id).ToList();
        var items = new List<ReadRequestItem>();
        lock (requestGate)
        {
            foreach (var id in ids.Distinct(StringComparer.Ordinal))
            {
                var sensor = config.Sensors.FirstOrDefault(s => s.Id == id);
                var lastAttempt = snapshot.Sensors.GetValueOrDefault(id)?.LastAttemptAt;
                if (sensor is null)
                    items.Add(new(id, null, ReadRequestStatus.Unknown));
                else if (requestedReads.Contains(id))
                    items.Add(new(id, sensor.Name, ReadRequestStatus.AlreadyQueued));
                else if (snapshot.ReadingNow.Contains(id))
                    items.Add(new(id, sensor.Name, ReadRequestStatus.InProgress));
                else if (lastAttempt is { } at && now - at < ForcedReadCooldown)
                    items.Add(new(id, sensor.Name, ReadRequestStatus.TooSoon, at + ForcedReadCooldown));
                else
                {
                    requestedReads.Add(id);
                    items.Add(new(id, sensor.Name, ReadRequestStatus.Queued));
                }
            }
            if (items.Any(i => i.Status == ReadRequestStatus.Queued)) wakeSignal.TrySetResult();
        }
        return new(items);
    }

    /// <summary>Czeka na kolejny cykl: <see cref="TickInterval"/> albo krócej, gdy ktoś zlecił odczyt lub raport.</summary>
    private async Task WaitForNextCycleAsync(CancellationToken cancellationToken)
    {
        Task signal;
        lock (requestGate)
        {
            // Zlecone w trakcie cyklu – od razu następny. Raport nie zostaje w kolejce po cyklu, który go obsłużył albo
            // przerwał się błędem (FailPendingReportRequest), więc to nie zapętli silnika.
            if (requestedReads.Count > 0 || reportRequestedAt is not null) return;
            if (wakeSignal.Task.IsCompleted) wakeSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            signal = wakeSignal.Task;
        }
        using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delay = Task.Delay(TickInterval, clock, delayCancellation.Token);
        if (await Task.WhenAny(delay, signal) == delay) await delay;   // przy anulowaniu rzuca OperationCanceledException
        else await delayCancellation.CancelAsync();
    }

    /// <summary>Zabiera z kolejki zlecone czujniki (kolejność konfiguracji).</summary>
    private HashSet<string> TakeRequestedReads()
    {
        lock (requestGate)
        {
            var taken = requestedReads.ToHashSet(StringComparer.Ordinal);
            requestedReads.Clear();
            return taken;
        }
    }

    private sealed record Attempt(DateTimeOffset NextAttemptAt, int Failures);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await store.InitializeAsync(cancellationToken);
        logger.LogInformation("Monitor „{Monitor}” (Glebosaurus {Version}) startuje: {Count} czujników, odczyt co {Interval} min, raporty: {Reports}.",
            config.MonitorName, AppInfo.Version, config.Sensors.Count, config.ReadIntervalMinutes,
            config.Reports.Times.Count == 0 ? "wyłączone" : string.Join(", ", config.Reports.Times));
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await CycleAsync(cancellationToken);
                await WaitForNextCycleAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Cykl monitora zakończył się błędem; kolejna próba za chwilę.");
                try { await WaitForNextCycleAsync(cancellationToken); }
                catch (OperationCanceledException) { break; }
            }
        }
        logger.LogInformation("Monitor zatrzymany.");
    }

    /// <summary>Jeden obrót pętli. Publiczny, żeby testy mogły przesuwać czas i wywoływać go ręcznie.</summary>
    public async Task CycleAsync(CancellationToken cancellationToken)
    {
        if (startedAt == default) startedAt = clock.GetUtcNow();
        try
        {
            var requested = TakeRequestedReads();
            var due = await DueSensorsAsync(requested, cancellationToken);
            if (due.Count > 0) await ReadSensorsAsync(due, requested, cancellationToken);
            await EvaluateAlertsAsync(cancellationToken);
            await NotifyAsync(cancellationToken);
            await SendReportsAsync(cancellationToken);
            await SendReportOnRequestAsync(cancellationToken);
            await SendAdminLogAsync(cancellationToken);
            await PurgeAsync(cancellationToken);
            lastCycleAt = clock.GetUtcNow();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            lastCycleError = (e.Message, clock.GetUtcNow());
            FailPendingReportRequest(e);
            throw;
        }
        finally
        {
            readingNow = [];
            readingSince = null;
            Publish();
        }
    }

    /// <summary>Składa nową migawkę stanu z pól silnika i podmienia ją w całości (patrz <see cref="State"/>).</summary>
    private void Publish()
    {
        var now = clock.GetUtcNow();
        var sensors = new Dictionary<string, SensorRuntimeState>(StringComparer.Ordinal);
        foreach (var sensor in config.Sensors)
        {
            var hasOutcome = lastOutcome.TryGetValue(sensor.Id, out var outcome);
            var hasSignal = lastSignal.TryGetValue(sensor.Id, out var signal);
            var attempt = attempts.GetValueOrDefault(sensor.Id);
            sensors[sensor.Id] = new(
                hasOutcome ? outcome.At : null, hasOutcome ? outcome.Result : null, hasOutcome ? outcome.Error : null,
                attempt?.Failures ?? 0, attempt?.NextAttemptAt,
                hasSignal ? signal.Dbm : null, hasSignal ? signal.At : null);
        }
        state = new(
            startedAt == default ? null : startedAt, lastCycleAt, lastCycleError?.Message, lastCycleError?.At,
            readingNow, readingSince, sensors,
            new(lastMailSentAt, lastMailError?.Message, lastMailError?.At, mailRetryAt > now ? mailRetryAt : null));
    }

    private void MailSent(DateTimeOffset at) => lastMailSentAt = at;

    private void MailFailed(Exception e, DateTimeOffset at) => lastMailError = (e.Message, at);

    private TimeSpan IntervalOf(SensorConfig sensor) => TimeSpan.FromMinutes(sensor.ReadIntervalMinutes ?? config.ReadIntervalMinutes);

    /// <param name="requested">Czujniki zlecone na żądanie – czytane od razu, bez względu na odstęp i ponowienia po porażce.</param>
    private async Task<IReadOnlyList<SensorConfig>> DueSensorsAsync(IReadOnlySet<string> requested, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var due = new List<SensorConfig>();
        foreach (var sensor in config.Sensors)
        {
            if (requested.Contains(sensor.Id))
            {
                due.Add(sensor);
                continue;
            }
            if (attempts.TryGetValue(sensor.Id, out var attempt) && attempt.NextAttemptAt > now) continue;
            var latest = await store.GetLatestAsync(sensor.Id, cancellationToken);
            if (latest is null || now - latest.At >= IntervalOf(sensor)) due.Add(sensor);
        }
        return due;
    }

    private async Task ReadSensorsAsync(IReadOnlyList<SensorConfig> due, IReadOnlySet<string> requested, CancellationToken cancellationToken)
    {
        var onRequest = due.Where(s => requested.Contains(s.Id)).Select(s => s.Name).ToList();
        logger.LogInformation("Odczyt czujników: {Names}{OnRequest}.", string.Join(", ", due.Select(s => s.Name)),
            onRequest.Count == 0 ? "" : $" (na żądanie: {string.Join(", ", onRequest)})");
        readingNow = due.Select(s => s.Id).ToList();
        readingSince = clock.GetUtcNow();
        Publish();
        var reported = new HashSet<string>();
        await foreach (var outcome in reader.ReadAsync(due, TimeSpan.FromSeconds(config.ScanWindowSeconds), cancellationToken))
        {
            reported.Add(outcome.Sensor.Id);
            var now = clock.GetUtcNow();
            var sensor = outcome.Sensor;
            // Sygnał ląduje w dzienniku przy każdym odczycie i każdej porażce, żeby „gubienie zasięgu” dało się później
            // odczytać z logu (poziom tuż przed porażką i jak dawno czujnik był słyszany).
            var signal = SignalStats.Format(outcome.SignalDbm);
            if (outcome.SignalDbm is { } heard) lastSignal[sensor.Id] = (heard, now);
            foreach (var note in outcome.Notes) logger.LogWarning("{Sensor}: uwaga z sesji czujnika: {Note}", sensor.Name, note);
            switch (outcome.Failure)
            {
                case SensorReadFailure.None when outcome.Reading is { MoisturePercent: not null } or { SoilTemperatureCelsius: not null }:
                    var r = outcome.Reading!;
                    // Zaległe rekordy czujnik kasuje po odebraniu, więc zapisujemy je od razu, z czasem czujnika. Rekord z
                    // przyszłości oznacza zły zegar czujnika i nie wiadomo, kiedy go zmierzono – pomijamy go.
                    var earlier = outcome.Earlier.Where(e => e.At < now).ToList();
                    foreach (var e in earlier)
                        await store.AddReadingAsync(new(sensor.Id, e.At, e.MoisturePercent, e.SoilTemperatureCelsius, e.BatteryPercent, e.BatteryState), cancellationToken);
                    // Najnowszy pomiar to „stan teraz”: zapisujemy go z chwilą odbioru (nie z zegarem czujnika), bo od tego
                    // zależy rytm odczytów i raporty.
                    await store.AddReadingAsync(new(sensor.Id, now, r.MoisturePercent, r.SoilTemperatureCelsius, r.BatteryPercent, r.BatteryState), cancellationToken);
                    attempts.Remove(sensor.Id);
                    lastOutcome[sensor.Id] = (now, SensorReadFailure.None, null);
                    logger.LogInformation("{Sensor}: wilgotność {Moisture}%, temperatura {Temperature} °C, bateria {Battery}%, sygnał {Signal}.",
                        sensor.Name, r.MoisturePercent?.ToString() ?? "–", r.SoilTemperatureCelsius?.ToString("0.0") ?? "–", r.BatteryPercent?.ToString() ?? "–", signal);
                    if (earlier.Count > 0)
                        logger.LogInformation("{Sensor}: zapisano {Count} zaległych pomiarów z czujnika (od {Oldest:yyyy-MM-dd HH:mm}).",
                            sensor.Name, earlier.Count, TimeZoneInfo.ConvertTime(earlier.Min(e => e.At), timeZone));
                    break;
                case SensorReadFailure.NotFound:
                    RegisterFailure(sensor, now);
                    lastOutcome[sensor.Id] = (now, SensorReadFailure.NotFound, null);
                    logger.LogWarning("{Sensor}: nie znaleziono w reklamach BLE w oknie {Window} s (poza zasięgiem lub czujnik śpi); {LastSignal}.",
                        sensor.Name, config.ScanWindowSeconds, LastSignalText(sensor, now));
                    break;
                case SensorReadFailure.Unbound:
                    RegisterFailure(sensor, now, UnboundRetryDelay);
                    lastOutcome[sensor.Id] = (now, SensorReadFailure.Unbound, null);
                    logger.LogError("{Sensor}: czujnik jest niesparowany (np. po resecie przyciskiem) – uruchom „glebosaurus pair --id {Id} --confirm-bind” i włóż baterie do czujnika; użyje tego samego klucza.", sensor.Name, sensor.Id);
                    break;
                default:
                    RegisterFailure(sensor, now);
                    lastOutcome[sensor.Id] = (now, SensorReadFailure.Error, outcome.Error ?? "czujnik nie zgłosił pomiarów");
                    logger.LogWarning("{Sensor}: błąd odczytu: {Error} (sygnał {Signal})", sensor.Name, outcome.Error ?? "czujnik nie zgłosił pomiarów", signal);
                    break;
            }
            readingNow = readingNow.Where(id => id != sensor.Id).ToList();
            Publish();
        }
        foreach (var sensor in due.Where(s => !reported.Contains(s.Id)))
        {
            RegisterFailure(sensor, clock.GetUtcNow());
            lastOutcome[sensor.Id] = (clock.GetUtcNow(), SensorReadFailure.Error, "warstwa BLE nie zwróciła wyniku");
            logger.LogWarning("{Sensor}: warstwa BLE nie zwróciła wyniku.", sensor.Name);
        }
    }

    /// <summary>Do ostrzeżenia „nie znaleziono”: kiedy i z jaką mocą czujnik był ostatnio słyszany (od startu monitora).</summary>
    private string LastSignalText(SensorConfig sensor, DateTimeOffset now)
    {
        if (!lastSignal.TryGetValue(sensor.Id, out var last)) return "od startu monitora jeszcze nie słyszany";
        var age = now - last.At;
        var ago = age < TimeSpan.FromHours(2) ? $"{(int)age.TotalMinutes} min temu" : $"{(int)age.TotalHours} h temu";
        return $"ostatnio słyszany: {SignalStats.Format(last.Dbm)}, {ago}";
    }

    /// <summary>Wykładniczy odstęp ponowień (1, 2, 4, 8, do 10 min), nigdy dłuższy niż zwykły odstęp odczytów.</summary>
    private void RegisterFailure(SensorConfig sensor, DateTimeOffset now, TimeSpan? fixedDelay = null)
    {
        var failures = attempts.TryGetValue(sensor.Id, out var previous) ? previous.Failures + 1 : 1;
        var delay = fixedDelay ?? TimeSpan.FromMinutes(Math.Min(Math.Pow(2, failures - 1), Math.Min(10, IntervalOf(sensor).TotalMinutes)));
        attempts[sensor.Id] = new(now + delay, failures);
    }

    private async Task EvaluateAlertsAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var open = await store.GetOpenAlertsAsync(cancellationToken);
        foreach (var sensor in config.Sensors)
        {
            var recent = await store.GetRecentAsync(sensor.Id, Math.Max(sensor.ConsecutiveReadings, 2) + 1, cancellationToken);
            var openForSensor = open.Where(a => a.SensorId == sensor.Id).ToDictionary(a => a.Kind);
            var desired = AlertEvaluator.Evaluate(sensor, recent, startedAt, now, openForSensor.Keys.ToHashSet());

            foreach (var alert in desired.Where(d => !openForSensor.ContainsKey(d.Kind)))
            {
                await store.OpenAlertAsync(sensor.Id, alert.Kind, alert.Detail, now, cancellationToken);
                logger.LogWarning("Alert otwarty: {Sensor} – {Kind}: {Detail}", sensor.Name, MailTemplates.KindText(alert.Kind), alert.Detail);
            }
            foreach (var (kind, alert) in openForSensor.Where(p => desired.All(d => d.Kind != p.Key)))
            {
                await store.ResolveAlertAsync(alert.Id, now, cancellationToken);
                logger.LogInformation("Alert zamknięty: {Sensor} – {Kind}.", sensor.Name, MailTemplates.KindText(kind));
            }
        }
    }

    private async Task NotifyAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var reminder = TimeSpan.FromHours(config.Alerts.ReminderHours);
        var pending = (await store.GetOpenAlertsAsync(cancellationToken))
            .Where(a => a.LastNotifiedAt is null || now - a.LastNotifiedAt >= reminder)
            .ToList();
        if (pending.Count == 0) return;

        if (mail is null || config.Mail is null)
        {
            foreach (var alert in pending.Where(a => warnedNoMail.Add(a.Id)))
                logger.LogWarning("Alert „{Detail}” czeka na wysyłkę, ale poczta nie jest skonfigurowana.", alert.Detail);
            return;
        }
        if (now < mailRetryAt) return;

        var notices = new List<AlertNotice>();
        foreach (var alert in pending)
        {
            var sensor = config.Sensors.FirstOrDefault(s => s.Id == alert.SensorId);
            if (sensor is null) continue;
            notices.Add(new(sensor, alert, await store.GetLatestAsync(alert.SensorId, cancellationToken), alert.LastNotifiedAt is not null));
        }
        if (notices.Count == 0) return;

        try
        {
            await mail.SendAsync(MailTemplates.Alert(notices, config.Mail.AlertRecipients, config.MonitorName, now, timeZone), cancellationToken);
            await store.MarkNotifiedAsync(notices.Select(n => n.Alert.Id).ToList(), now, cancellationToken);
            MailSent(now);
            logger.LogInformation("Wysłano mail o alertach ({Count}).", notices.Count);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            mailRetryAt = now + MailRetryDelay;
            MailFailed(e, now);
            logger.LogWarning(e, "Nie udało się wysłać maila o alertach; ponowię za {Delay} min.", MailRetryDelay.TotalMinutes);
        }
    }

    private async Task SendReportsAsync(CancellationToken cancellationToken)
    {
        if (config.Reports.Times.Count == 0) return;
        var now = clock.GetUtcNow();
        var due = ReportSchedule.Due(config.Reports.Times, now, timeZone, TimeSpan.FromHours(config.Reports.CatchUpHours));
        if (due.Count == 0) return;

        if (mail is null || config.Mail is null)
        {
            if (!warnedNoReportMail)
            {
                warnedNoReportMail = true;
                logger.LogWarning("Raporty są zaplanowane, ale poczta nie jest skonfigurowana – pomijam.");
            }
            return;
        }
        if (now < mailRetryAt) return;

        foreach (var report in due)
        {
            if (await store.HasReportRunAsync(report.Key, cancellationToken)) continue;
            var snapshots = await SnapshotBuilder.BuildAsync(config, store, now, cancellationToken);
            try
            {
                await mail.SendAsync(MailTemplates.Report(snapshots, config.Mail.EffectiveReportRecipients, config.MonitorName, now, timeZone, report.ScheduledAt), cancellationToken);
                await store.RecordReportRunAsync(report.Key, now, cancellationToken);
                MailSent(now);
                logger.LogInformation("Wysłano raport {Key}.", report.Key);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                mailRetryAt = now + MailRetryDelay;
                MailFailed(e, now);
                logger.LogWarning(e, "Nie udało się wysłać raportu {Key}; ponowię za {Delay} min.", report.Key, MailRetryDelay.TotalMinutes);
                return;
            }
        }
    }

    /// <summary>
    /// Raport zlecony z panelu WWW (<see cref="RequestReport"/>): szablon i odbiorcy jak w raporcie zaplanowanym, ale bez
    /// wpisu w <c>report_runs</c> – zaplanowany raport i tak pójdzie o swojej porze. Wstrzymanie wysyłki po błędzie
    /// (<see cref="MailRetryDelay"/>) go nie zatrzymuje ani nie przedłuża: to świadoma akcja użytkownika, a przed zasypaniem
    /// skrzynek chroni <see cref="ReportOnRequestCooldown"/>.
    /// </summary>
    private async Task SendReportOnRequestAsync(CancellationToken cancellationToken)
    {
        lock (requestGate)
            if (reportRequestedAt is null) return;

        var now = clock.GetUtcNow();
        string? error = null;
        if (mail is null || config.Mail is null) error = "poczta nie jest skonfigurowana";   // RequestReport i tak tego nie przepuszcza
        else
        {
            var snapshots = await SnapshotBuilder.BuildAsync(config, store, now, cancellationToken);
            var recipients = config.Mail.EffectiveReportRecipients;
            try
            {
                await mail.SendAsync(MailTemplates.Report(snapshots, recipients, config.MonitorName, now, timeZone), cancellationToken);
                MailSent(now);
                logger.LogInformation("Wysłano raport na żądanie (odbiorców: {Count}).", recipients.Count);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                error = e.Message;
                MailFailed(e, now);
                logger.LogWarning(e, "Nie udało się wysłać raportu na żądanie.");
            }
        }

        lock (requestGate)
        {
            reportRequestedAt = null;
            reportLastAttemptAt = now;
            reportLastError = error;
        }
    }

    /// <summary>
    /// Cykl przerwany błędem przed wysłaniem raportu na żądanie: raport kończy się porażką z tą przyczyną (panel ją pokaże),
    /// zamiast zostać w kolejce i budzić pętlę od razu po każdym kolejnym nieudanym cyklu.
    /// </summary>
    private void FailPendingReportRequest(Exception e)
    {
        lock (requestGate)
        {
            if (reportRequestedAt is null) return;
            reportRequestedAt = null;
            reportLastAttemptAt = clock.GetUtcNow();
            reportLastError = $"cykl monitora przerwany błędem: {e.Message}";
        }
        logger.LogWarning("Raport na żądanie nie został wysłany – cykl monitora zakończył się błędem.");
    }

    /// <summary>
    /// W pierwszym przebiegu każdego dnia (czasu lokalnego) wysyła administratorowi dziennik agenta z całego dnia
    /// poprzedniego. Wysłanie odnotowuje w bazie (klucz „log|RRRR-MM-DD” obok kluczy raportów), więc restart agenta
    /// w ciągu dnia nie wysyła go drugi raz, a nieudana wysyłka jest ponawiana. Gdy poprzedniego dnia agent nie
    /// działał (brak pliku), nie ma czego wysyłać. Własny odstęp ponowień, żeby kłopot z tym mailem nie opóźniał alertów.
    /// </summary>
    private async Task SendAdminLogAsync(CancellationToken cancellationToken)
    {
        if (logs is null || mail is null || config.Mail is not { AdminRecipients.Count: > 0 } mailConfig) return;

        var now = clock.GetUtcNow();
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, timeZone).DateTime).AddDays(-1);
        if (adminLogDoneFor == day) return;                       // sprawdzone w tym dniu – bez zaglądania do bazy co cykl

        var key = $"log|{day:yyyy-MM-dd}";
        if (await store.HasReportRunAsync(key, cancellationToken))
        {
            adminLogDoneFor = day;
            return;
        }
        if (now < mailRetryAt || now < adminLogRetryAt) return;

        var text = await logs.ReadDayAsync(day, cancellationToken);
        if (text is null)
        {
            adminLogDoneFor = day;
            logger.LogInformation("Brak dziennika z {Day:yyyy-MM-dd} – nie ma czego wysłać administratorowi.", day);
            return;
        }

        try
        {
            await mail.SendAsync(MailTemplates.AdminLog(text, day, mailConfig.AdminRecipients, config.MonitorName, now, timeZone), cancellationToken);
            await store.RecordReportRunAsync(key, now, cancellationToken);
            adminLogDoneFor = day;
            MailSent(now);
            logger.LogInformation("Wysłano administratorowi dziennik z {Day:yyyy-MM-dd}.", day);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            adminLogRetryAt = now + MailRetryDelay;
            MailFailed(e, now);
            logger.LogWarning(e, "Nie udało się wysłać dziennika z {Day:yyyy-MM-dd} administratorowi; ponowię za {Delay} min.", day, MailRetryDelay.TotalMinutes);
        }
    }

    private async Task PurgeAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        if (now < nextPurgeAt) return;
        nextPurgeAt = now.AddHours(24);
        await store.PurgeAsync(now.AddDays(-config.RetentionDays), cancellationToken);
    }
}
