using Glebosaurus.Core.Domain;
using Microsoft.Extensions.Logging;

namespace Glebosaurus.Core.Cloud;

/// <summary>Lokalny bufor odczytów do wysłania (w agencie: <c>SqliteMonitorStore</c>, kolumna <c>sync_state</c>).</summary>
public interface IReadingOutbox
{
    /// <summary>Odczyty jeszcze niewysłane, od najstarszego.</summary>
    Task<IReadOnlyList<StoredReading>> GetUnsentAsync(int limit, CancellationToken cancellationToken);

    Task<int> CountUnsentAsync(CancellationToken cancellationToken);

    /// <summary>Przyjęte przez chmurę (nowe albo już tam będące) i odrzucone (nieznany czujnik, zły czas, wartości spoza
    /// zakresu) – ani jedne, ani drugie nie są wysyłane ponownie.</summary>
    Task MarkSentAsync(IReadOnlyCollection<StoredReading> accepted, IReadOnlyCollection<StoredReading> rejected, CancellationToken cancellationToken);
}

/// <summary>Jak agent słyszy czujnik i jak mu idą odczyty – z nasłuchu radia (dostarcza zbieracz).</summary>
public sealed record SensorObservation(DateTimeOffset? LastHeardAt, int? Rssi, DateTimeOffset? LastAttemptAt, DateTimeOffset? LastSuccessAt,
    int ConsecutiveFailures)
{
    public SensorHearing ToHearing() => new(LastHeardAt, Rssi, ConsecutiveFailures);
}

public interface ISensorHearingSource
{
    /// <summary>Aktualny obraz: czujnik → obserwacja (tylko czujniki, które agent kiedykolwiek słyszał albo próbował czytać).</summary>
    IReadOnlyDictionary<string, SensorObservation> Observations();
}

public sealed record CloudSyncOptions
{
    /// <summary>
    /// Odstęp synchronizacji. Odnawia dzierżawy (czujnik 30 min, blokada nadawcy 5 min) i sygnał życia, który decyduje o
    /// „obecności” agenta (<see cref="NotifierLockOptions.AliveWithin"/>) – po jednym chybionym cyklu blokadzie zostają 2 min
    /// (do maila trzeba 1), a obecność trwa (ARCHITEKTURA.md, „Zależności czasów”). Nowe odczyty, oddanie czujnika i polecenia
    /// z panelu budzą synchronizację od razu (<see cref="CloudSync.Wake"/>).
    /// </summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>Odczytów w jednej synchronizacji (baza przyjmuje do 1000).</summary>
    public int BatchSize { get; init; } = 500;

    /// <summary>Przerwa między paczkami, gdy w buforze czeka więcej odczytów (dosyłanie po przerwie sieci).</summary>
    public TimeSpan BacklogDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Kolejne odstępy po nieudanych synchronizacjach (brak sieci, chmura niedostępna); ostatni się powtarza.</summary>
    public IReadOnlyList<TimeSpan> RetryDelays { get; init; } =
        [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)];

    /// <summary>Odstęp po błędzie programu albo protokołu (ponowienie niewiele da, ale nie wolno zamilknąć na zawsze).</summary>
    public TimeSpan BrokenRetryDelay { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Ostrzeżenie w dzienniku, gdy zegar agenta różni się od zegara bazy o więcej niż tyle.</summary>
    public TimeSpan ClockSkewWarning { get; init; } = TimeSpan.FromMinutes(2);
}

public enum SyncOutcome
{
    Synced,

    /// <summary>Udało się, a w buforze czekają kolejne odczyty – następna paczka zaraz.</summary>
    Backlog,

    Failed,

    /// <summary>Token unieważniony – agent przestaje rozmawiać z chmurą (dzierżawy same wygasną).</summary>
    Revoked
}

/// <summary>Migawka stanu synchronizacji do panelu i statusu (bezpieczna do czytania z innego wątku).</summary>
public sealed record CloudSyncState(
    DateTimeOffset? LastSuccessAt, DateTimeOffset? LastAttemptAt, string? LastError, CloudErrorKind? LastErrorKind,
    int ConsecutiveFailures, int UnsentReadings, long? ConfigVersion, bool Revoked, TimeSpan? ClockOffset,
    IReadOnlyList<string> Leases)
{
    public static readonly CloudSyncState NotStarted = new(null, null, null, null, 0, 0, null, false, null, []);

    /// <summary>Obraz chmury z ostatniej udanej synchronizacji (dla panelu); po porażce zostaje poprzedni.</summary>
    public CloudView? View { get; init; }
}

/// <summary>
/// Co agent wie o pozostałych z ostatniej udanej synchronizacji: kim jest, kto trzyma które czujniki i blokadę nadawcy,
/// kto jeszcze pracuje. Czasy w odpowiedzi chmury są względne – liczy się je od <paramref name="At"/> (chwila odpowiedzi).
/// </summary>
public sealed record CloudView(DateTimeOffset At, AgentIdentity Agent, IReadOnlyList<SensorLeaseView> Sensors, NotifierView? Notifier,
    IReadOnlyList<AgentView> Agents);

/// <summary>
/// Synchronizacja agenta z chmurą (ARCHITEKTURA.md, zasady 3 i 14): co <see cref="CloudSyncOptions.Interval"/> jedno
/// <c>agent_sync</c> – sygnał życia, słyszenia, paczka odczytów z lokalnego bufora, oddania i prośby o dzierżawy według
/// <see cref="SensorLeasePolicy"/> i <see cref="NotifierLockPolicy"/>. Odpowiedź uaktualnia <see cref="LeaseLedger"/>
/// (ważność od chwili wysłania), stan wysłania odczytów, polecenia i wersję konfiguracji. Bez sieci odczyty czekają w
/// buforze, a dzierżawy po prostu wygasają; po powrocie sieci bufor jest dosyłany paczkami – idempotentnie, więc utrata
/// odpowiedzi (baza zapisała, agent nie wie) kończy się ponownym wysłaniem bez dubli.
/// </summary>
public sealed class CloudSync(
    ICloudApi api, IReadingOutbox outbox, ISensorHearingSource hearing, LeaseLedger ledger,
    SensorLeasePolicy sensorPolicy, NotifierLockPolicy notifierPolicy,
    Func<IReadOnlyCollection<string>> sensorIds, Func<AgentStatus> status,
    TimeProvider clock, ILogger logger, CloudSyncOptions? options = null)
{
    private readonly CloudSyncOptions settings = options ?? new();
    private readonly Lock gate = new();
    private readonly HashSet<long> deliveredCommands = [];
    private IReadOnlyList<CloudCommand> lastCommands = [];
    private TaskCompletionSource wakeSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile CloudSyncState state = CloudSyncState.NotStarted;
    private CloudErrorKind? lastFailure;
    private bool warnedSkew;

    /// <summary>Czy nadawca jest między wysyłkami – tylko wtedy wolno oddać blokadę ważniejszemu kandydatowi.</summary>
    public Func<bool> NotifierSafePoint { get; set; } = () => true;

    /// <summary>Po każdej udanej synchronizacji (nie przy zatrzymaniu) – np. odświeżenie konfiguracji i rozdział poleceń.
    /// Błąd w nim trafia do dziennika i nie psuje synchronizacji.</summary>
    public Func<CancellationToken, Task>? Synced { get; set; }

    public CloudSyncState State => state;

    /// <summary>Wersja konfiguracji w chmurze z ostatniej udanej synchronizacji (zmiana = trzeba pobrać <c>get_config</c>).</summary>
    public long? ConfigVersion => state.ConfigVersion;

    /// <summary>Budzi pętlę przed końcem odstępu (np. nowy odczyt, zakończone polecenie). Po porażce nie skraca ponowień.</summary>
    public void Wake()
    {
        lock (gate) wakeSignal.TrySetResult();
    }

    /// <summary>Polecenia z ostatniej synchronizacji, których jeszcze nie oddano (każde raz; wraca w odpowiedziach, dopóki
    /// agent nie zgłosi wyniku – wtedy jest pomijane po id).</summary>
    public IReadOnlyList<CloudCommand> TakeNewCommands()
    {
        lock (gate)
        {
            var fresh = lastCommands.Where(c => deliveredCommands.Add(c.Id)).ToList();
            return fresh;
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            SyncOutcome outcome;
            try
            {
                outcome = await SyncOnceAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Synchronizacja z chmurą zakończyła się nieoczekiwanym błędem.");
                outcome = SyncOutcome.Failed;
            }
            if (outcome == SyncOutcome.Revoked) return;
            try
            {
                await WaitAsync(NextDelay(outcome), allowWake: outcome is SyncOutcome.Synced, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>Odstęp do następnej synchronizacji po danym wyniku.</summary>
    public TimeSpan NextDelay(SyncOutcome outcome) => outcome switch
    {
        SyncOutcome.Synced => settings.Interval,
        SyncOutcome.Backlog => settings.BacklogDelay,
        SyncOutcome.Revoked => Timeout.InfiniteTimeSpan,
        _ when lastFailure is CloudErrorKind.Invalid or CloudErrorKind.Protocol => settings.BrokenRetryDelay,
        _ => settings.RetryDelays[Math.Clamp(state.ConsecutiveFailures - 1, 0, settings.RetryDelays.Count - 1)]
    };

    /// <summary>Jedna synchronizacja. Publiczna, żeby testy mogły ją wołać na ręcznie przesuwanym czasie.</summary>
    public async Task<SyncOutcome> SyncOnceAsync(CancellationToken cancellationToken)
    {
        if (state.Revoked) return SyncOutcome.Revoked;
        var ids = sensorIds();
        var observations = hearing.Observations().Where(o => ids.Contains(o.Key)).ToDictionary();
        var plan = sensorPolicy.Plan(ids, observations.ToDictionary(o => o.Key, o => o.Value.ToHearing()))
            .Merge(notifierPolicy.Plan(NotifierSafePoint()));
        var outcome = await SendAsync(plan, observations, cancellationToken);
        if (outcome is SyncOutcome.Synced or SyncOutcome.Backlog && Synced is { } after)
        {
            try { await after(cancellationToken); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Obsługa po synchronizacji (konfiguracja, polecenia) zakończyła się błędem.");
            }
        }
        return outcome;
    }

    /// <summary>
    /// Planowe zatrzymanie (po zakończeniu trwającego odczytu i wysyłki): ostatnia paczka odczytów i oddanie wszystkich
    /// dzierżaw – inni agenci przejmą czujniki i blokadę w sekundy, a nie po 32 min. Błąd sieci tylko odnotowuje.
    /// </summary>
    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        if (state.Revoked) return;
        var plan = sensorPolicy.ReleaseAll().Merge(notifierPolicy.ReleaseForShutdown());
        var outcome = await SendAsync(plan, new Dictionary<string, SensorObservation>(), cancellationToken);
        if (outcome == SyncOutcome.Failed)
            logger.LogWarning("Nie udało się oddać dzierżaw przy zatrzymaniu – wrócą do puli po wygaśnięciu (do ok. 32 min).");
    }

    private async Task<SyncOutcome> SendAsync(LeasePlan plan, IReadOnlyDictionary<string, SensorObservation> observations,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var batch = await outbox.GetUnsentAsync(settings.BatchSize, cancellationToken);
        var request = new SyncRequest(status())
        {
            Sightings = [.. observations.Select(o => Sighting(o.Key, o.Value, now))],
            Readings = [.. batch.Select(ReadingReport.From)],
            Releases = plan.Releases,
            Claims = plan.Claims
        };

        var sentAt = ledger.Now();
        SyncResponse response;
        try
        {
            response = await api.SyncAsync(request, cancellationToken);
        }
        catch (CloudApiException e)
        {
            return await FailedAsync(e, now, cancellationToken);
        }

        ledger.Apply(sentAt, response.Leases);
        sensorPolicy.Observe(response);
        notifierPolicy.Observe(response);
        await MarkReadingsAsync(batch, response.Readings, cancellationToken);
        lock (gate)
        {
            lastCommands = response.Commands;
            deliveredCommands.IntersectWith(response.Commands.Select(c => c.Id));
        }

        var received = clock.GetUtcNow();
        var offset = response.ServerTime - received;
        if (offset.Duration() > settings.ClockSkewWarning && !warnedSkew)
        {
            warnedSkew = true;
            logger.LogWarning("Zegar agenta różni się od zegara chmury o {Offset:0} s – dzierżawy liczą się na zegarze bazy, ale czasy odczytów są z zegara agenta.",
                offset.TotalSeconds);
        }
        if (lastFailure is not null) logger.LogInformation("Synchronizacja z chmurą znów działa.");
        lastFailure = null;

        var unsent = await outbox.CountUnsentAsync(cancellationToken);
        state = new(received, now, null, null, 0, unsent, response.ConfigVersion, false, offset,
            [.. ledger.Known().Select(l => l.Resource)])
        {
            View = new(received, response.Agent, response.Sensors, response.Notifier, response.Agents)
        };
        return unsent > 0 && batch.Count >= settings.BatchSize ? SyncOutcome.Backlog : SyncOutcome.Synced;
    }

    private async Task MarkReadingsAsync(IReadOnlyList<StoredReading> batch, ReadingsResult result, CancellationToken cancellationToken)
    {
        if (batch.Count == 0) return;
        var rejectedKeys = result.Rejected
            .Where(r => r.Sensor is not null && r.At is not null)
            .Select(r => (r.Sensor!, r.At!.Value.ToUnixTimeMilliseconds()))
            .ToHashSet();
        var rejected = batch.Where(r => rejectedKeys.Contains((r.SensorId, r.At.ToUnixTimeMilliseconds()))).ToList();
        var accepted = batch.Except(rejected).ToList();
        await outbox.MarkSentAsync(accepted, rejected, cancellationToken);
        foreach (var reason in result.Rejected.GroupBy(r => r.Reason))
            logger.LogWarning("Chmura odrzuciła {Count} odczytów ({Reason}) – nie będą wysyłane ponownie.", reason.Count(), reason.Key);
    }

    private async Task<SyncOutcome> FailedAsync(CloudApiException e, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var current = state;
        var revoked = e.Kind == CloudErrorKind.Unauthorized;
        int unsent;
        try { unsent = await outbox.CountUnsentAsync(cancellationToken); }
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { unsent = current.UnsentReadings; }
        state = current with
        {
            LastAttemptAt = now, LastError = e.Message, LastErrorKind = e.Kind, ConsecutiveFailures = current.ConsecutiveFailures + 1,
            UnsentReadings = unsent, Revoked = revoked, Leases = [.. ledger.Known().Select(l => l.Resource)]
        };
        if (revoked)
            logger.LogError("Chmura odrzuciła token agenta (unieważniony?) – agent przestaje się synchronizować; dzierżawy wygasną same. Potrzebna ponowna rejestracja (enroll).");
        else if (lastFailure != e.Kind)
            logger.LogWarning("Synchronizacja z chmurą nie powiodła się: {Error}; odczyty czekają w buforie ({Unsent}).", e.Message, unsent);
        lastFailure = e.Kind;
        return revoked ? SyncOutcome.Revoked : SyncOutcome.Failed;
    }

    private static SightingReport Sighting(string sensorId, SensorObservation observation, DateTimeOffset now) =>
        new(sensorId, observation.Rssi, Ago(observation.LastHeardAt, now), Ago(observation.LastAttemptAt, now), Ago(observation.LastSuccessAt, now),
            observation.ConsecutiveFailures);

    private static double? Ago(DateTimeOffset? at, DateTimeOffset now) =>
        at is { } value ? Math.Max(0, Math.Round((now - value).TotalSeconds, 1)) : null;

    private async Task WaitAsync(TimeSpan delay, bool allowWake, CancellationToken cancellationToken)
    {
        if (delay == Timeout.InfiniteTimeSpan) return;
        if (!allowWake)
        {
            await Task.Delay(delay, clock, cancellationToken);
            return;
        }
        Task signal;
        lock (gate)
        {
            if (wakeSignal.Task.IsCompleted) wakeSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            signal = wakeSignal.Task;
        }
        using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var wait = Task.Delay(delay, clock, delayCancellation.Token);
        if (await Task.WhenAny(wait, signal) == wait) await wait;
        else await delayCancellation.CancelAsync();
        cancellationToken.ThrowIfCancellationRequested();
    }
}
