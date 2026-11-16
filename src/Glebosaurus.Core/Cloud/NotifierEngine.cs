using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Glebosaurus.Core.Alerts;
using Glebosaurus.Core.Domain;
using Glebosaurus.Core.Mail;
using Glebosaurus.Core.Monitoring;
using Glebosaurus.Core.Reports;
using Microsoft.Extensions.Logging;

namespace Glebosaurus.Core.Cloud;

/// <summary>Sygnał „nadawca działa” dla strażnika (healthchecks.io).</summary>
public interface IHeartbeat
{
    Task PingAsync(Uri url, CancellationToken cancellationToken);
}

public sealed record NotifierOptions
{
    public TimeSpan TickInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Co ile alerty są przeliczane i wysyłane (każde przeliczenie to zapytanie o odczyty i alerty w chmurze).</summary>
    public TimeSpan AlertEvery { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Po pewnej porażce SMTP (połączenie, logowanie) kolejna próba dopiero po tym czasie.</summary>
    public TimeSpan MailRetryDelay { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan HeartbeatEvery { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Agent bez synchronizacji dłużej niż tyle – mail „agent milczy” do administratora.</summary>
    public TimeSpan AgentSilentAfter { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Log administratora za wczoraj najwcześniej o tej porze (agenci wysyłają dzienniki w pierwszym cyklu dnia).</summary>
    public TimeOnly AdminLogAfter { get; init; } = new(1, 0);

    /// <summary>Jak często sprawdzać, czy doszły dzienniki kolejnych agentów (np. agent wrócił po przerwie).</summary>
    public TimeSpan AdminLogCheckEvery { get; init; } = TimeSpan.FromHours(1);

    /// <summary>Hasło Gmaila pobierane ponownie po takim czasie (zmiana w chmurze dociera bez restartu).</summary>
    public TimeSpan CredentialsMaxAge { get; init; } = TimeSpan.FromHours(1);

    /// <summary>Najkrótsze okno odczytów do alertów (dłuższe, gdy któryś czujnik ma dłuższe <c>silentAfterHours</c>).</summary>
    public TimeSpan MinimumAlertWindow { get; init; } = TimeSpan.FromHours(12);

    /// <summary>Pełne odświeżenie okna odczytów (dociąga też odczyty dosłane z opóźnieniem); pomiędzy – tylko nowe.</summary>
    public TimeSpan FullRefreshEvery { get; init; } = TimeSpan.FromHours(1);

    public TimeSpan IncrementalOverlap { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Podpis w nagłówku maili – nadawcą bywa różny agent, a dla czytelnika to ciągle „Glebosaurus”.</summary>
    public string MonitorName { get; init; } = "Glebosaurus";
}

public enum MailOutcome
{
    Sent,

    /// <summary>Ten mail ma już inny nadawca albo wcześniejsza próba (z historii kolejki) – nic do zrobienia.</summary>
    AlreadyClaimed,

    /// <summary>Przerwa w trakcie wysyłania treści – mail może dojść albo nie; nie jest ponawiany (bez duplikatów).</summary>
    Uncertain,

    /// <summary>Serwer na pewno nie dostał treści – rezerwacja oddana, kolejna próba po <see cref="NotifierOptions.MailRetryDelay"/>.</summary>
    Failed,

    /// <summary>Trwa przerwa po porażce SMTP albo blokada jest za krótka – próba później.</summary>
    Postponed
}

/// <summary>
/// Nadawca w trybie chmurowym (ARCHITEKTURA.md, zasady 2 i 14, kawałek e): pracuje tylko z blokadą nadawcy (z co najmniej
/// minutą ważności) i z hasłem Gmaila pobranym na tę epokę. Liczy alerty z odczytów wszystkich agentów i zapisuje je we
/// wspólnym stanie w chmurze, wysyła maile o alertach (z przypomnieniami), raporty o stałych porach i na żądanie, log
/// administratora (osobno z dziennika każdego agenta), „agent milczy” i sygnał dla strażnika. Każdy mail idzie przez
/// kolejkę: najpierw rezerwacja klucza w chmurze, potem SMTP, potem odnotowanie – mail wychodzi najwyżej raz, także przy
/// zmianie nadawcy w trakcie.
/// </summary>
public sealed class NotifierEngine(
    Func<CloudAgentConfig?> config, ICloudApi api, NotifierLockPolicy lockPolicy, Func<MailCredentials, IMailGateway> mailFactory,
    IHeartbeat? heartbeat, Func<CloudSyncState> syncState, TimeProvider clock, ILogger logger, NotifierOptions? options = null)
{
    private readonly NotifierOptions settings = options ?? new();
    private readonly Lock gate = new();
    private readonly List<long> reportRequests = [];
    private readonly HashSet<string> settledKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<StoredReading>> readings = new(StringComparer.Ordinal);
    private TaskCompletionSource wakeSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DateTimeOffset startedAt;
    private MailCredentials? credentials;
    private IMailGateway? gateway;
    private long credentialsEpoch;
    private DateTimeOffset credentialsAt;
    private DateTimeOffset lastAlertEvaluation = DateTimeOffset.MinValue;
    private DateTimeOffset lastFullRefresh = DateTimeOffset.MinValue;
    private DateTimeOffset lastFetch = DateTimeOffset.MinValue;
    private DateTimeOffset mailRetryAt = DateTimeOffset.MinValue;
    private DateTimeOffset lastHeartbeat = DateTimeOffset.MinValue;
    private DateTimeOffset adminLogCheckedAt = DateTimeOffset.MinValue;
    private DateTimeOffset lastCloudWarning = DateTimeOffset.MinValue;
    private bool smtpHealthy = true;
    private bool warnedNoPassword, warnedNoRecipients, warnedBadPingUrl;
    private volatile bool busy;

    /// <summary>true w trakcie cyklu wysyłki – wtedy blokady nie wolno oddać (<see cref="CloudSync.NotifierSafePoint"/>).</summary>
    public bool Busy => busy;

    public void Wake()
    {
        lock (gate) wakeSignal.TrySetResult();
    }

    /// <summary><c>report_now</c> – raport w najbliższym cyklu (klucz <c>report-request|&lt;id&gt;</c>); inne polecenia – nie dla nadawcy.</summary>
    public Task<bool> HandleCommandAsync(CloudCommand command, CancellationToken cancellationToken)
    {
        if (command.Kind != "report_now") return Task.FromResult(false);
        lock (gate)
        {
            reportRequests.Add(command.Id);
            wakeSignal.TrySetResult();
        }
        logger.LogInformation("Polecenie z chmury: raport na żądanie ({Id}).", command.Id);
        return Task.FromResult(true);
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
                logger.LogError(e, "Cykl nadawcy zakończył się błędem; kolejna próba za chwilę.");
            }
            try { await WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
        }
    }

    public async Task CycleAsync(CancellationToken cancellationToken)
    {
        if (config() is not { Agent.CanSendMail: true } current) return;
        if (startedAt == default) startedAt = clock.GetUtcNow();
        if (!lockPolicy.CanSendMail || lockPolicy.Epoch is not { } epoch)
        {
            Forget();
            return;
        }

        busy = true;
        try
        {
            if (!await EnsureCredentialsAsync(epoch, cancellationToken)) return;
            var now = clock.GetUtcNow();
            if (now - lastAlertEvaluation >= settings.AlertEvery)
            {
                lastAlertEvaluation = now;
                await RefreshReadingsAsync(current, cancellationToken);
                await EvaluateAlertsAsync(current, epoch, cancellationToken);
                await NotifyAlertsAsync(current, epoch, cancellationToken);
            }
            await SendScheduledReportsAsync(current, epoch, cancellationToken);
            await SendRequestedReportsAsync(current, epoch, cancellationToken);
            await SendAdminLogsAsync(current, epoch, cancellationToken);
            await NotifySilentAgentsAsync(current, epoch, cancellationToken);
            await HeartbeatAsync(cancellationToken);
        }
        catch (CloudApiException e) when (e.Kind == CloudErrorKind.NotNotifier)
        {
            logger.LogWarning("Blokada nadawcy przestała być nasza w trakcie pracy – przerywam wysyłkę; dalej wyśle ten, kto ją przejmie.");
            Forget();
        }
        catch (CloudApiException e) when (e.Kind == CloudErrorKind.Unavailable)
        {
            var now = clock.GetUtcNow();
            if (now - lastCloudWarning >= settings.MailRetryDelay)
            {
                lastCloudWarning = now;
                logger.LogWarning("Nadawca: chmura niedostępna ({Error}) – maile czekają.", e.Message);
            }
        }
        finally
        {
            busy = false;
        }
    }

    // ---- Dane poczty ------------------------------------------------------------------------------------------------

    private async Task<bool> EnsureCredentialsAsync(long epoch, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        if (gateway is not null && credentialsEpoch == epoch && now - credentialsAt < settings.CredentialsMaxAge) return true;
        var fresh = await api.MailCredentialsAsync(epoch, cancellationToken);
        if (string.IsNullOrWhiteSpace(fresh.AppPassword) || string.IsNullOrWhiteSpace(fresh.GmailAddress))
        {
            if (!warnedNoPassword) logger.LogError("Nadawca ma blokadę, ale w chmurze brak adresu albo hasła aplikacji Gmail – maile nie wychodzą.");
            warnedNoPassword = true;
            Forget();
            return false;
        }
        warnedNoPassword = false;
        if (credentialsEpoch != epoch)
            logger.LogInformation("Nadawca: mam blokadę (epoka {Epoch}) i dane poczty – wysyłam maile.", epoch);
        credentials = fresh;
        credentialsEpoch = epoch;
        credentialsAt = now;
        gateway = mailFactory(fresh);
        return true;
    }

    /// <summary>Bez blokady hasło nie zostaje nawet w pamięci.</summary>
    private void Forget()
    {
        credentials = null;
        gateway = null;
    }

    // ---- Odczyty i alerty -------------------------------------------------------------------------------------------

    private async Task RefreshReadingsAsync(CloudAgentConfig current, CancellationToken cancellationToken)
    {
        var ids = current.Config.Sensors.Select(s => s.Id).ToList();
        if (ids.Count == 0) return;
        var now = clock.GetUtcNow();
        var window = AlertWindow(current);
        var full = now - lastFullRefresh >= settings.FullRefreshEvery;
        var from = full ? now - window : lastFetch - settings.IncrementalOverlap;
        // Kilka minut „w przyszłość” – zegar agenta bywa odrobinę przed zegarem nadawcy.
        var fetched = await api.ReadingsRangeAsync(ids, from, now + TimeSpan.FromMinutes(10), cancellationToken);
        if (full)
        {
            readings.Clear();
            lastFullRefresh = now;
        }
        foreach (var group in fetched.GroupBy(r => r.SensorId))
        {
            if (!readings.TryGetValue(group.Key, out var list)) readings[group.Key] = list = [];
            list.AddRange(group.Where(r => list.All(known => known.At != r.At)));
        }
        foreach (var list in readings.Values)
        {
            list.RemoveAll(r => r.At < now - window);
            list.Sort((a, b) => a.At.CompareTo(b.At));
        }
        lastFetch = now;
    }

    private TimeSpan AlertWindow(CloudAgentConfig current)
    {
        var longest = current.Config.Sensors.Select(s => TimeSpan.FromHours(s.SilentAfterHours) + TimeSpan.FromHours(1)).DefaultIfEmpty(TimeSpan.Zero).Max();
        return longest > settings.MinimumAlertWindow ? longest : settings.MinimumAlertWindow;
    }

    private IReadOnlyList<StoredReading> Recent(string sensorId, int count) =>
        readings.TryGetValue(sensorId, out var list) ? [.. list.AsEnumerable().Reverse().Take(count)] : [];

    private StoredReading? Latest(string sensorId) => readings.TryGetValue(sensorId, out var list) && list.Count > 0 ? list[^1] : null;

    private async Task EvaluateAlertsAsync(CloudAgentConfig current, long epoch, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var open = await api.AlertListAsync(cancellationToken);
        foreach (var sensor in current.Config.Sensors)
        {
            var openForSensor = open.Where(a => a.Sensor == sensor.Id).ToDictionary(a => a.Kind);
            var desired = AlertEvaluator.Evaluate(sensor, Recent(sensor.Id, Math.Max(sensor.ConsecutiveReadings, 2) + 1), startedAt, now,
                openForSensor.Keys.ToHashSet());
            foreach (var alert in desired.Where(d => !openForSensor.ContainsKey(d.Kind)))
                if ((await api.AlertOpenAsync(epoch, sensor.Id, alert.Kind, alert.Detail, cancellationToken)).Created)
                    logger.LogWarning("Alert otwarty: {Sensor} – {Kind}: {Detail}", sensor.Name, MailTemplates.KindText(alert.Kind), alert.Detail);
            foreach (var (kind, alert) in openForSensor.Where(p => desired.All(d => d.Kind != p.Key)))
                if (await api.AlertResolveAsync(epoch, alert.Id, cancellationToken))
                    logger.LogInformation("Alert zamknięty: {Sensor} – {Kind}.", sensor.Name, MailTemplates.KindText(kind));
        }
    }

    /// <summary>
    /// Mail o nowych alertach i przypomnienia. Każdy alert ma własny klucz (rezerwowany osobno), a jeden mail może objąć
    /// kilka – do maila trafiają tylko te, które ten nadawca zarezerwował. Przypomnienie ma w kluczu numer doby od otwarcia.
    /// </summary>
    private async Task NotifyAlertsAsync(CloudAgentConfig current, long epoch, CancellationToken cancellationToken)
    {
        if (current.Mail is not { AlertRecipients.Count: > 0 } mail)
        {
            WarnNoRecipients();
            return;
        }
        var now = clock.GetUtcNow();
        var reminder = TimeSpan.FromHours(current.Config.Alerts.ReminderHours);
        var sensors = current.Config.Sensors.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var pending = (await api.AlertListAsync(cancellationToken))
            .Where(a => sensors.ContainsKey(a.Sensor) && (a.LastNotifiedAt is null || now - a.LastNotifiedAt >= reminder))
            .Select(a => (Alert: a, Key: AlertKey(a, now, reminder)))
            .ToList();
        if (pending.Count == 0) return;

        await SendAsync([.. pending.Select(p => p.Key)], claimed => MailTemplates.Alert(
                [.. pending.Where(p => claimed.Contains(p.Key)).Select(p =>
                    new AlertNotice(sensors[p.Alert.Sensor], p.Alert.ToRecord(), Latest(p.Alert.Sensor), p.Alert.LastNotifiedAt is not null))],
                mail.AlertRecipients, settings.MonitorName, now, current.TimeZone),
            epoch, cancellationToken);

        // Wysłane albo załatwione wcześniej (także przez innego nadawcę) – w chmurze odnotowane, żeby nie wracały co minutę.
        var settled = pending.Where(p => settledKeys.Contains(p.Key)).Select(p => p.Alert.Id).ToList();
        if (settled.Count > 0) await api.AlertNotifiedAsync(epoch, settled, cancellationToken);
    }

    /// <summary><c>alert|czujnik|rodzaj|początek|n</c> – n = 0 dla pierwszego maila, potem pełne okresy przypomnień od otwarcia.</summary>
    public static string AlertKey(CloudAlert alert, DateTimeOffset now, TimeSpan reminder)
    {
        var round = alert.LastNotifiedAt is null ? 0 : Math.Max(1, (long)Math.Floor((now - alert.OpenedAt) / reminder));
        return string.Create(CultureInfo.InvariantCulture,
            $"alert|{alert.Sensor}|{alert.Kind}|{alert.OpenedAt.UtcDateTime:yyyy-MM-ddTHH:mm:ss.ffffff}Z|{round}");
    }

    // ---- Raporty ----------------------------------------------------------------------------------------------------

    private async Task SendScheduledReportsAsync(CloudAgentConfig current, long epoch, CancellationToken cancellationToken)
    {
        var times = current.Config.Reports.Times;
        if (times.Count == 0) return;
        var now = clock.GetUtcNow();
        var due = ReportSchedule.Due(times, now, current.TimeZone, TimeSpan.FromHours(current.Config.Reports.CatchUpHours))
            .Where(r => !settledKeys.Contains("report|" + r.Key)).ToList();
        if (due.Count == 0) return;
        if (ReportRecipients(current) is not { Count: > 0 } recipients)
        {
            WarnNoRecipients();
            return;
        }
        var snapshots = await SnapshotsAsync(current, cancellationToken);
        foreach (var report in due)
        {
            var outcome = await SendAsync(["report|" + report.Key],
                _ => MailTemplates.Report(snapshots, recipients, settings.MonitorName, now, current.TimeZone, report.ScheduledAt), epoch, cancellationToken);
            if (outcome is MailOutcome.Failed or MailOutcome.Postponed) return;
        }
    }

    private async Task SendRequestedReportsAsync(CloudAgentConfig current, long epoch, CancellationToken cancellationToken)
    {
        List<long> ids;
        lock (gate)
        {
            ids = [.. reportRequests];
            reportRequests.Clear();
        }
        if (ids.Count == 0) return;
        var recipients = ReportRecipients(current);
        var snapshots = recipients.Count > 0 ? await SnapshotsAsync(current, cancellationToken) : [];
        var now = clock.GetUtcNow();
        foreach (var id in ids)
        {
            if (recipients.Count == 0)
            {
                await DoneAsync(id, new JsonObject { ["ok"] = false, ["error"] = "brak odbiorców raportów w konfiguracji" }, cancellationToken);
                continue;
            }
            var outcome = await SendAsync([string.Create(CultureInfo.InvariantCulture, $"report-request|{id}")],
                _ => MailTemplates.Report(snapshots, recipients, settings.MonitorName, now, current.TimeZone), epoch, cancellationToken);
            if (outcome is MailOutcome.Failed or MailOutcome.Postponed)
            {
                lock (gate) reportRequests.Add(id);   // spróbuje po przerwie (polecenie w chmurze czeka do 10 min)
                continue;
            }
            await DoneAsync(id, outcome == MailOutcome.Uncertain
                ? new JsonObject { ["ok"] = false, ["error"] = "wysyłka przerwana w trakcie – mail mógł nie dojść" }
                : new JsonObject { ["ok"] = true }, cancellationToken);
        }
    }

    private static IReadOnlyList<string> ReportRecipients(CloudAgentConfig current) =>
        current.Mail is not { } mail ? [] : mail.ReportRecipients.Count > 0 ? mail.ReportRecipients : mail.AlertRecipients;

    /// <summary>Stan wszystkich czujników do raportu – z odczytów wszystkich agentów (75 h na wykres) i wspólnych alertów.</summary>
    private async Task<IReadOnlyList<SensorSnapshot>> SnapshotsAsync(CloudAgentConfig current, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var sensors = current.Config.Sensors;
        var history = sensors.Count == 0 ? [] : await api.ReadingsRangeAsync([.. sensors.Select(s => s.Id)],
            now - SnapshotBuilder.HistoryWindow, now + TimeSpan.FromMinutes(10), cancellationToken);
        var open = await api.AlertListAsync(cancellationToken);
        return [.. sensors.Select(sensor =>
        {
            var own = history.Where(r => r.SensorId == sensor.Id).ToList();
            return new SensorSnapshot(sensor, own.MaxBy(r => r.At) ?? Latest(sensor.Id),
                [.. open.Where(a => a.Sensor == sensor.Id).Select(a => a.Kind)]) { History = own };
        })];
    }

    // ---- Log administratora i cisza agentów ---------------------------------------------------------------------------

    /// <summary>Log z wczoraj – osobny mail z dziennika każdego agenta (ten sam szablon co w trybie samodzielnym).</summary>
    private async Task SendAdminLogsAsync(CloudAgentConfig current, long epoch, CancellationToken cancellationToken)
    {
        if (current.Mail is not { AdminRecipients.Count: > 0 } mail) return;
        var now = clock.GetUtcNow();
        var local = TimeZoneInfo.ConvertTime(now, current.TimeZone);
        if (TimeOnly.FromDateTime(local.DateTime) < settings.AdminLogAfter || now - adminLogCheckedAt < settings.AdminLogCheckEvery) return;
        adminLogCheckedAt = now;

        var day = DateOnly.FromDateTime(local.DateTime).AddDays(-1);
        foreach (var log in await api.LogGetAsync(epoch, day, cancellationToken))
        {
            var key = string.Create(CultureInfo.InvariantCulture, $"log|{day:yyyy-MM-dd}|{log.Agent}");
            if (settledKeys.Contains(key)) continue;
            string text;
            try { text = Gunzip(log.ContentGzipBase64); }
            catch (Exception e) when (e is InvalidDataException or FormatException)
            {
                logger.LogWarning("Dziennik agenta {Agent} z {Day:yyyy-MM-dd} jest uszkodzony ({Error}) – pomijam.", log.Agent, day, e.Message);
                continue;
            }
            var outcome = await SendAsync([key], _ => MailTemplates.AdminLog(text, day, mail.AdminRecipients, log.Agent, now, current.TimeZone),
                epoch, cancellationToken);
            if (outcome is MailOutcome.Failed or MailOutcome.Postponed)
            {
                adminLogCheckedAt = now - settings.AdminLogCheckEvery + settings.MailRetryDelay;
                return;
            }
        }
    }

    /// <summary>„Agent milczy” – raz na epizod ciszy (klucz z chwilą ostatniego kontaktu co do minuty).</summary>
    private async Task NotifySilentAgentsAsync(CloudAgentConfig current, long epoch, CancellationToken cancellationToken)
    {
        if (lockPolicy.AgentsSnapshot is not { } snapshot || current.Mail is not { } mail) return;
        var recipients = mail.AdminRecipients.Count > 0 ? mail.AdminRecipients : mail.AlertRecipients;
        if (recipients.Count == 0) return;
        var now = clock.GetUtcNow();
        foreach (var agent in snapshot.Agents.Where(a => a.Name != current.Agent.Name && a.LastSeenSecondsAgo is not null))
        {
            if (TimeSpan.FromSeconds(agent.LastSeenSecondsAgo!.Value) < settings.AgentSilentAfter) continue;
            var lastSeen = snapshot.ServerTime - TimeSpan.FromSeconds(Math.Round(agent.LastSeenSecondsAgo.Value));
            var key = string.Create(CultureInfo.InvariantCulture, $"agent-silent|{agent.Name}|{lastSeen.UtcDateTime:yyyy-MM-ddTHH:mm}Z");
            if (settledKeys.Contains(key)) continue;
            var outcome = await SendAsync([key], _ => MailTemplates.AgentSilent(agent.Name, lastSeen, recipients, settings.MonitorName, now, current.TimeZone),
                epoch, cancellationToken);
            if (outcome is MailOutcome.Failed or MailOutcome.Postponed) return;
        }
    }

    // ---- Strażnik ---------------------------------------------------------------------------------------------------

    /// <summary>Sygnał tylko, gdy działa wszystko: blokada, synchronizacja z chmurą (udana w ostatnich 3 min) i SMTP.
    /// Cisza dłuższa niż ustawiona w healthchecks.io = mail stamtąd.</summary>
    private async Task HeartbeatAsync(CancellationToken cancellationToken)
    {
        if (heartbeat is null || credentials?.HealthchecksPingUrl is not { Length: > 0 } address) return;
        var now = clock.GetUtcNow();
        if (now - lastHeartbeat < settings.HeartbeatEvery) return;
        var sync = syncState();
        var healthy = lockPolicy.CanSendMail && smtpHealthy && sync.ConsecutiveFailures == 0
                      && sync.LastSuccessAt is { } synced && now - synced <= TimeSpan.FromMinutes(3);
        if (!healthy) return;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
        {
            if (!warnedBadPingUrl) logger.LogError("Adres sygnału healthchecks.io w chmurze nie jest adresem https – strażnik nie dostaje sygnałów.");
            warnedBadPingUrl = true;
            return;
        }
        lastHeartbeat = now;
        try { await heartbeat.PingAsync(url, cancellationToken); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Sygnał dla strażnika nie przeszedł: {Error}", e.Message);
        }
    }

    // ---- Kolejka maili ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Rezerwacja kluczy w chmurze (z epoką blokady), SMTP, odnotowanie. Klucze już załatwione są pomijane; mail powstaje
    /// tylko z kluczy zarezerwowanych teraz. Pewna porażka SMTP oddaje rezerwację (można ponowić), niepewna ją zostawia.
    /// </summary>
    private async Task<MailOutcome> SendAsync(IReadOnlyList<string> keys, Func<IReadOnlySet<string>, MailMessage> build, long epoch,
        CancellationToken cancellationToken)
    {
        var open = keys.Where(k => !settledKeys.Contains(k)).Distinct(StringComparer.Ordinal).ToList();
        if (open.Count == 0) return MailOutcome.AlreadyClaimed;
        var now = clock.GetUtcNow();
        if (now < mailRetryAt || !lockPolicy.CanSendMail || gateway is not { } mailer) return MailOutcome.Postponed;

        var claimed = new List<string>();
        try
        {
            foreach (var key in open)
            {
                var result = await api.MailClaimAsync(key, epoch, cancellationToken);
                if (result.Claimed) claimed.Add(key);
                else if (result.AlreadyClaimed) settledKeys.Add(key);
                else throw new CloudApiException(CloudErrorKind.NotNotifier, "mail_claim: blokada nadawcy już nie jest nasza");
            }
        }
        catch (CloudApiException)
        {
            await AbandonAsync(claimed, cancellationToken);
            throw;
        }
        if (claimed.Count == 0) return MailOutcome.AlreadyClaimed;

        var message = build(claimed.ToHashSet(StringComparer.Ordinal)) with { MessageId = MessageId(claimed[0]) };
        try
        {
            await mailer.SendAsync(message, cancellationToken);
        }
        catch (MailTransportException e) when (!e.MaybeDelivered)
        {
            await AbandonAsync(claimed, cancellationToken);
            mailRetryAt = now + settings.MailRetryDelay;
            smtpHealthy = false;
            logger.LogWarning("Nie udało się wysłać maila „{Subject}”: {Error}; ponowię za {Minutes} min.", message.Subject, e.Message,
                settings.MailRetryDelay.TotalMinutes);
            return MailOutcome.Failed;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            foreach (var key in claimed) settledKeys.Add(key);
            mailRetryAt = now + settings.MailRetryDelay;
            smtpHealthy = false;
            logger.LogError(e, "Wysyłka maila „{Subject}” przerwana w trakcie – mógł dojść albo nie; nie ponawiam, żeby nie było duplikatu.", message.Subject);
            return MailOutcome.Uncertain;
        }

        smtpHealthy = true;
        foreach (var key in claimed)
        {
            settledKeys.Add(key);
            try { await api.MailSentAsync(key, message.MessageId, cancellationToken); }
            catch (CloudApiException e) { logger.LogWarning("Mail wysłany, ale chmura go nie odnotowała ({Key}): {Error}", key, e.Message); }
        }
        logger.LogInformation("Wysłano mail „{Subject}” (odbiorców: {Count}).", message.Subject, message.To.Count);
        return MailOutcome.Sent;
    }

    private async Task AbandonAsync(IEnumerable<string> keys, CancellationToken cancellationToken)
    {
        foreach (var key in keys)
        {
            try { await api.MailAbandonAsync(key, cancellationToken); }
            catch (CloudApiException e) { logger.LogWarning("Nie udało się oddać rezerwacji {Key} ({Error}) – ten mail przepadnie.", key, e.Message); }
        }
    }

    /// <summary>Message-ID z klucza kolejki – ten sam mail ma zawsze ten sam identyfikator.</summary>
    public static string MessageId(string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32] + "@glebosaurus";

    private async Task DoneAsync(long commandId, JsonObject result, CancellationToken cancellationToken)
    {
        try
        {
            if (!await api.CommandDoneAsync(commandId, result, cancellationToken))
                logger.LogWarning("Chmura nie przyjęła wyniku polecenia {Id} (wygasło albo nie było skierowane do tego agenta).", commandId);
        }
        catch (CloudApiException e) { logger.LogWarning("Nie udało się zgłosić wyniku polecenia {Id}: {Error}", commandId, e.Message); }
    }

    private void WarnNoRecipients()
    {
        if (!warnedNoRecipients) logger.LogWarning("Nadawca: w konfiguracji chmury nie ma odbiorców alertów ani raportów – te maile nie wychodzą.");
        warnedNoRecipients = true;
    }

    private static string Gunzip(string base64)
    {
        using var gzip = new GZipStream(new MemoryStream(Convert.FromBase64String(base64)), CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private async Task WaitAsync(CancellationToken cancellationToken)
    {
        Task signal;
        lock (gate)
        {
            // Przebudzenie sprzed końca cyklu nie skraca czekania – raport zlecony w trakcie cyklu poczeka najwyżej jeden
            // odstęp, a odłożony po porażce SMTP nie zakręci pętlą.
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
