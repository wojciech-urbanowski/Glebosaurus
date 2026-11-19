using Glebosaurus.Core.Domain;
using Glebosaurus.Core.Monitoring;
using Microsoft.Extensions.Logging;

namespace Glebosaurus.Core.Cloud;

/// <param name="LastSeenAt">Ostatnia synchronizacja agenta (według zegara tego agenta, przeliczona z odpowiedzi chmury).</param>
/// <param name="HeldSensors">Ile czujników ma teraz w dzierżawie.</param>
public sealed record CloudAgentStatus(string Name, bool CanSendMail, int? SenderPriority, DateTimeOffset? LastSeenAt, bool IsMe, int HeldSensors)
{
    /// <summary>Urządzenie agenta z jego ostatniej synchronizacji (program; telefon: bateria, temperatura, sieć) – agent
    /// dokłada sygnał życia do każdego <c>agent_sync</c>, a chmura oddaje go innym agentom; null – jeszcze nie wiadomo.</summary>
    public AgentDeviceStatus? Device { get; init; }
}

/// <summary>Chmura w statusie lokalnego panelu – bez sekretów (token, hasło Gmaila, klucze Tuya nie mają tu pola).</summary>
/// <param name="Project">Adres (host) projektu Supabase.</param>
/// <param name="Agent">Nazwa tego agenta w chmurze; null przed pierwszą konfiguracją.</param>
/// <param name="ConfigCachedAt">Agent pracuje na lokalnej kopii konfiguracji z tej chwili (chmura była niedostępna przy starcie).</param>
/// <param name="ConfigWarnings">Czujniki z chmury pominięte przez agenta (np. bez danych Tuya) i inne uwagi do konfiguracji.</param>
/// <param name="Notifier">Agent, który trzyma blokadę nadawcy (wysyła maile); null – nikt, maile czekają.</param>
/// <param name="MirrorEnabled">Agent trzyma kopię odczytów wszystkich agentów (panel pokazuje wtedy pomiary wszystkich).</param>
public sealed record CloudStatus(
    string Project, string? Agent, bool CanSendMail, int? SenderPriority,
    DateTimeOffset? LastSyncAt, DateTimeOffset? LastSyncAttemptAt, string? LastSyncError, int SyncFailures, int UnsentReadings, bool Revoked,
    double? ClockOffsetSeconds, long? ConfigVersion, DateTimeOffset? ConfigCachedAt, IReadOnlyList<string> ConfigWarnings,
    string? Notifier, bool NotifierIsMe, DateTimeOffset? NotifierUntil, IReadOnlyList<CloudAgentStatus> Agents,
    bool MirrorEnabled, DateTimeOffset? MirrorAt, string? MirrorError);

/// <summary>
/// Lokalny panel WWW agenta (ARCHITEKTURA.md, zasada 15): status składany z konfiguracji z chmury, lokalnej
/// bazy (własne odczyty i kopia odczytów innych agentów), migawek zbieracza, synchronizacji i nadawcy oraz alertów ze
/// wspólnego stanu w chmurze. Odczyt i raport na żądanie idą poleceniem przez chmurę tam, gdzie jest władza: odczyt do
/// właściciela dzierżawy czujnika (może nim być ten agent), raport do obecnego nadawcy. Konfiguracji nie zmienia.
/// </summary>
public sealed class CloudPanel(
    string project, Func<CloudAgentConfig?> config, IMonitorStore store, ICloudApi api,
    Func<MonitorRuntimeState> collector, Func<CloudSyncState> sync, Func<MailRuntimeState> mail, Func<ReadingMirrorState>? mirror,
    Action wakeSync, TimeProvider clock, ILogger logger)
{
    /// <summary>Jak długo zlecony odczyt „czeka” w panelu, jeśli nowy odczyt nie przyjdzie wcześniej (polecenie trafia do
    /// właściciela przy jego synchronizacji, ok. minuty, potem odczyt i wysłanie).</summary>
    public static readonly TimeSpan ReadPending = TimeSpan.FromMinutes(3);

    /// <summary>To samo polecenie najwyżej raz na minutę – tak liczy też chmura (<c>command_create</c>).</summary>
    public static readonly TimeSpan RequestCooldown = TimeSpan.FromMinutes(1);

    /// <summary>Alerty z chmury co najwyżej tak często (panel odświeża się co kilka sekund w trakcie odczytów).</summary>
    public static readonly TimeSpan AlertsMaxAge = TimeSpan.FromSeconds(30);

    private readonly Lock gate = new();
    private readonly Dictionary<string, DateTimeOffset> requestedReads = new(StringComparer.Ordinal);
    private DateTimeOffset? reportRequestedAt;
    private string? reportError;
    private (DateTimeOffset At, IReadOnlyList<AlertRecord> Alerts)? alerts;

    public async Task<MonitorStatus> StatusAsync(MonitorHostInfo host, CancellationToken cancellationToken)
    {
        var current = config() ?? throw new InvalidOperationException("Konfiguracja agenta jeszcze się wczytuje.");
        var now = clock.GetUtcNow();
        var syncState = sync();
        var view = syncState.View;
        var me = view?.Agent.Name ?? current.Agent.Name;

        var pending = new List<string>();
        Dictionary<string, DateTimeOffset> requested;
        lock (gate) requested = new(requestedReads, StringComparer.Ordinal);
        foreach (var (id, at) in requested)
        {
            if (now - at >= ReadPending) continue;
            var latest = await store.GetLatestAsync(id, cancellationToken);
            if (latest is null || latest.At < at) pending.Add(id);
        }

        var runtime = collector() with { RequestedReads = pending };
        var status = await MonitorStatusBuilder.BuildAsync(current.Config, store, runtime, host, now, current.TimeZone,
            await AlertsAsync(now, cancellationToken), cancellationToken);

        var leases = view?.Sensors.ToDictionary(s => s.Sensor, StringComparer.Ordinal) ?? [];
        var sensors = status.Sensors.Select(sensor =>
        {
            var lease = leases.GetValueOrDefault(sensor.Id);
            DateTimeOffset? from = requested.TryGetValue(sensor.Id, out var at) && at + RequestCooldown > now ? at + RequestCooldown : null;
            if (sensor.ReadOnRequestFrom is { } own && (from is null || own > from)) from = own;
            return sensor with
            {
                ReadOnRequestFrom = from,
                Holder = lease?.Holder,
                HolderIsMe = lease?.Holder is { } holder && holder == me,
                HolderUntil = lease?.HolderExpiresInSeconds is { } seconds && view is not null ? view.At.AddSeconds(seconds) : null,
                BestOtherAgent = lease?.BestOther?.Agent,
                BestOtherRssi = lease?.BestOther?.Rssi
            };
        }).ToList();

        var mailState = mail();
        var mailStatus = current.Mail is { } m
            ? new MailStatus(true, m.GmailAddress, m.AlertRecipients, m.ReportRecipients.Count > 0 ? m.ReportRecipients : m.AlertRecipients,
                m.AdminRecipients, mailState.LastSentAt, mailState.LastError, mailState.LastErrorAt, mailState.RetryAt)
            : MailStatus.NotConfigured;

        DateTimeOffset? lastReport, reportFrom;
        string? lastReportError;
        lock (gate) (lastReport, lastReportError) = (reportRequestedAt, reportError);
        reportFrom = lastReport + RequestCooldown;
        var reports = status.Reports with
        {
            OnRequest = new(view?.Notifier is not null, false, lastReport, lastReportError, reportFrom > now ? reportFrom : null)
        };

        var held = view?.Sensors.Where(s => s.Holder is not null).GroupBy(s => s.Holder!).ToDictionary(g => g.Key, g => g.Count()) ?? [];
        var mirrorState = mirror?.Invoke();
        var cloud = new CloudStatus(
            project, me, view?.Agent.CanSendMail ?? current.Agent.CanSendMail, view?.Agent.SenderPriority ?? current.Agent.SenderPriority,
            syncState.LastSuccessAt, syncState.LastAttemptAt, syncState.LastError, syncState.ConsecutiveFailures, syncState.UnsentReadings,
            syncState.Revoked, syncState.ClockOffset?.TotalSeconds is { } offset ? Math.Round(offset, 1) : null,
            current.Version, current.CachedAt, current.Warnings,
            view?.Notifier?.Holder, view?.Notifier?.Holder == me && view?.Notifier is not null,
            view?.Notifier is { } notifier ? view.At.AddSeconds(notifier.ExpiresInSeconds) : null,
            [.. (view?.Agents ?? []).Select(a => new CloudAgentStatus(a.Name, a.CanSendMail, a.SenderPriority,
                a.LastSeenSecondsAgo is { } ago ? view!.At.AddSeconds(-ago) : null, a.Name == me, held.GetValueOrDefault(a.Name))
            {
                Device = a.Status is { } device ? AgentDevice.Describe(device) : null
            })],
            mirrorState is not null, mirrorState?.LastSuccessAt,
            mirrorState is { LastError: { } error } && (mirrorState.LastSuccessAt is null || mirrorState.LastAttemptAt > mirrorState.LastSuccessAt) ? error : null);

        return status with
        {
            Sensors = sensors,
            Mail = mailStatus,
            Reports = reports,
            Monitor = status.Monitor with { Name = me },
            Cloud = cloud
        };
    }

    /// <summary>Odczyt na żądanie: polecenie <c>read_now</c> w chmurze dla każdego czujnika (null = wszystkich z konfiguracji).</summary>
    public async Task<ReadRequestResult> RequestReadAsync(IReadOnlyCollection<string>? sensorIds, CancellationToken cancellationToken)
    {
        var current = config();
        var known = current?.Config.Sensors.ToDictionary(s => s.Id, s => s.Name, StringComparer.Ordinal) ?? [];
        var items = new List<ReadRequestItem>();
        foreach (var id in (sensorIds ?? [.. known.Keys]).Distinct(StringComparer.Ordinal))
        {
            if (!known.TryGetValue(id, out var name))
            {
                items.Add(new(id, null, ReadRequestStatus.Unknown));
                continue;
            }
            try
            {
                var created = await api.CommandCreateAsync("read_now", id, cancellationToken);
                if (created.Created) lock (gate) requestedReads[id] = clock.GetUtcNow();
                items.Add(new(id, name, created.Created ? ReadRequestStatus.Queued : ReadRequestStatus.AlreadyQueued));
            }
            catch (CloudApiException e)
            {
                logger.LogWarning("Panel WWW: nie udało się zlecić odczytu {Sensor} przez chmurę: {Error}", name, e.Message);
                items.Add(new(id, name, ReadRequestStatus.Unavailable));
            }
        }
        // Właścicielem może być ten agent – wtedy polecenie przyjdzie przy najbliższej synchronizacji.
        if (items.Any(i => i.Status == ReadRequestStatus.Queued)) wakeSync();
        return new(items);
    }

    /// <summary>Raport na żądanie: polecenie <c>report_now</c> w chmurze – wyśle go obecny nadawca (prawdziwy mail).</summary>
    public async Task<ReportRequestResult> RequestReportAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        lock (gate)
        {
            if (reportRequestedAt is { } last && now - last < RequestCooldown) return new(ReportRequestStatus.TooSoon, last + RequestCooldown);
        }
        try
        {
            var created = await api.CommandCreateAsync("report_now", null, cancellationToken);
            lock (gate) (reportRequestedAt, reportError) = (now, null);
            if (created.Created) wakeSync();
            return new(created.Created ? ReportRequestStatus.Queued : ReportRequestStatus.AlreadyQueued);
        }
        catch (CloudApiException e)
        {
            lock (gate) (reportRequestedAt, reportError) = (now, e.Message);
            logger.LogWarning("Panel WWW: nie udało się zlecić raportu przez chmurę: {Error}", e.Message);
            return new(ReportRequestStatus.Unavailable);
        }
    }

    /// <summary>Otwarte alerty ze wspólnego stanu w chmurze; bez chmury – ostatnie znane (albo żadne).</summary>
    private async Task<IReadOnlyList<AlertRecord>> AlertsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        (DateTimeOffset At, IReadOnlyList<AlertRecord> Alerts)? cached;
        lock (gate) cached = alerts;
        if (cached is { } fresh && now - fresh.At < AlertsMaxAge) return fresh.Alerts;
        try
        {
            var list = (await api.AlertListAsync(cancellationToken)).Select(a => a.ToRecord()).ToList();
            lock (gate) alerts = (now, list);
            return list;
        }
        catch (CloudApiException)
        {
            // Bez sieci: poprzednia lista (jeśli była), a kolejna próba dopiero po odstępie – panel nie czeka co 3 s na sieć.
            var stale = cached?.Alerts ?? [];
            lock (gate) alerts = (now, stale);
            return stale;
        }
    }
}
