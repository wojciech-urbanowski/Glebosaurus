namespace Glebosaurus.Core.Cloud;

/// <summary>Co agent prosi przedłużyć lub przyznać i co oddaje w najbliższej synchronizacji.</summary>
public sealed record LeasePlan(IReadOnlyList<string> Claims, IReadOnlyList<LeaseRelease> Releases)
{
    public static readonly LeasePlan Empty = new([], []);

    public LeasePlan Merge(LeasePlan other) => new([.. Claims, .. other.Claims], [.. Releases, .. other.Releases]);
}

/// <summary>Jak agent słyszy czujnik (z własnego nasłuchu): kiedy ostatnio, z jaką mocą (średnia ważnych próbek) i ile
/// odczytów z rzędu się nie udało.</summary>
public sealed record SensorHearing(DateTimeOffset? LastHeardAt, int? Rssi, int ConsecutiveFailures);

public sealed record SensorLeaseOptions
{
    /// <summary>Bez tyle ważności agent nie zaczyna odczytu (odczyt trwa 11–14 s, plus nasłuch).</summary>
    public TimeSpan MinimumForRead { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>O czujnik ubiega się tylko agent, który go słyszał w tym czasie.</summary>
    public TimeSpan HeardWithin { get; init; } = TimeSpan.FromMinutes(10);

    public int FailuresBeforeRelease { get; init; } = 3;

    /// <summary>Oddanie: inny agent słyszy czujnik co najmniej o tyle lepiej…</summary>
    public int BetterByDb { get; init; } = 6;

    /// <summary>…nieprzerwanie przez tyle czasu (histereza – bez przerzucania czujnika tam i z powrotem).</summary>
    public TimeSpan BetterFor { get; init; } = TimeSpan.FromMinutes(10);

    public TimeSpan CooldownAfterFailures { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan CooldownAfterHandover { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Wolny czujnik: przy tym sygnale i lepszym agent bierze go od razu…</summary>
    public int StrongSignalDbm { get; init; } = -70;

    /// <summary>…a za każdy dB słabiej czeka tyle dłużej (lepiej słyszący zdąży pierwszy), najwyżej <see cref="MaxWait"/>.</summary>
    public TimeSpan WaitPerDb { get; init; } = TimeSpan.FromSeconds(6);

    public TimeSpan MaxWait { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>Sygnał przyjmowany, gdy czujnik był słyszany, ale bez ważnej mocy (Windows podaje −127 w ok. 1/3 pakietów).</summary>
    public int UnknownSignalDbm { get; init; } = -95;
}

/// <summary>
/// Przydział czujników między agentów (ARCHITEKTURA.md, zasada 2) – decyzje po stronie agenta; o tym, kto naprawdę dostaje
/// dzierżawę, rozstrzyga baza. Agent prosi tylko o czujnik, który słyszy; przy wolnym czujniku czeka tym dłużej, im słabszy
/// sygnał; trzymany czujnik przedłuża w każdej synchronizacji, a oddaje po kilku porażkach z rzędu albo gdy inny agent
/// dłuższy czas słyszy go wyraźnie lepiej. Po oddaniu przez chwilę się o niego nie ubiega.
/// </summary>
public sealed class SensorLeasePolicy(TimeProvider clock, LeaseLedger ledger, SensorLeaseOptions? options = null)
{
    private readonly SensorLeaseOptions settings = options ?? new();
    private readonly Lock gate = new();   // Plan i Observe – wątek synchronizacji, Release – zbieracz (polecenie „oddaj”)
    private readonly Dictionary<string, DateTimeOffset> freeSince = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> betterSince = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> cooldownUntil = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LeaseRelease> pendingReleases = new(StringComparer.Ordinal);
    private Dictionary<string, SensorLeaseView> view = new(StringComparer.Ordinal);
    private string? me;

    /// <summary>Stan z ostatniej udanej synchronizacji (po <see cref="LeaseLedger.Apply"/>): kto trzyma czujniki, kto je słyszy.</summary>
    public void Observe(SyncResponse response)
    {
        lock (gate)
        {
            me = response.Agent.Name;
            view = response.Sensors.ToDictionary(s => s.Sensor, StringComparer.Ordinal);
            pendingReleases.Clear();
        }
    }

    /// <summary>Czy wolno zacząć odczyt: dzierżawa ważna jeszcze co najmniej <see cref="SensorLeaseOptions.MinimumForRead"/>
    /// (plus <paramref name="extra"/> – np. nasłuch i odczyty czujników przed nim w tej samej kolejce).</summary>
    public bool CanRead(string sensorId, TimeSpan extra = default) =>
        ledger.Holds(CloudProtocol.SensorResource(sensorId), settings.MinimumForRead + extra);

    /// <summary>Oddanie na żądanie (polecenie <c>release</c>, zatrzymanie agenta) – od razu przestaje czytać ten czujnik.</summary>
    public void Release(string sensorId, TimeSpan? cooldown = null)
    {
        lock (gate) ReleaseCore(sensorId, cooldown);
    }

    private void ReleaseCore(string sensorId, TimeSpan? cooldown)
    {
        var resource = CloudProtocol.SensorResource(sensorId);
        if (ledger.Epoch(resource) is { } epoch) pendingReleases[resource] = new(resource, epoch);
        ledger.Forget(resource);
        betterSince.Remove(sensorId);
        freeSince.Remove(sensorId);
        cooldownUntil[sensorId] = clock.GetUtcNow() + (cooldown ?? settings.CooldownAfterHandover);
    }

    /// <summary>Planowe zatrzymanie: oddaje wszystkie czujniki (po zakończeniu trwającego odczytu) – inni przejmą je w sekundy.</summary>
    public LeasePlan ReleaseAll()
    {
        lock (gate)
        {
            foreach (var lease in ledger.Known())
                if (CloudProtocol.TryParseSensorResource(lease.Resource, out var sensorId)) ReleaseCore(sensorId, null);
            return new([], [.. pendingReleases.Values]);
        }
    }

    public LeasePlan Plan(IEnumerable<string> sensorIds, IReadOnlyDictionary<string, SensorHearing> hearing)
    {
        lock (gate) return PlanCore(sensorIds, hearing);
    }

    private LeasePlan PlanCore(IEnumerable<string> sensorIds, IReadOnlyDictionary<string, SensorHearing> hearing)
    {
        var now = clock.GetUtcNow();
        var claims = new List<string>();
        foreach (var sensorId in sensorIds)
        {
            var resource = CloudProtocol.SensorResource(sensorId);
            hearing.TryGetValue(sensorId, out var heard);
            view.TryGetValue(sensorId, out var seen);

            if (ledger.Epoch(resource) is not null)
            {
                if (heard is { } h && h.ConsecutiveFailures >= settings.FailuresBeforeRelease)
                {
                    ReleaseCore(sensorId, settings.CooldownAfterFailures);
                    continue;
                }
                if (heard?.Rssi is { } mine && seen?.BestOther is { } other && other.Rssi >= mine + settings.BetterByDb)
                {
                    betterSince.TryAdd(sensorId, now);
                    if (now - betterSince[sensorId] >= settings.BetterFor)
                    {
                        ReleaseCore(sensorId, settings.CooldownAfterHandover);
                        continue;
                    }
                }
                else betterSince.Remove(sensorId);
                claims.Add(resource);
                continue;
            }

            betterSince.Remove(sensorId);
            if (cooldownUntil.TryGetValue(sensorId, out var until) && until > now) continue;
            if (heard?.LastHeardAt is not { } heardAt || now - heardAt > settings.HeardWithin)
            {
                freeSince.Remove(sensorId);
                continue;
            }
            if (seen?.Holder is { } holder)
            {
                freeSince.Remove(sensorId);
                if (holder == me) claims.Add(resource);   // po restarcie agenta baza wciąż ma go za właściciela
                continue;
            }
            freeSince.TryAdd(sensorId, now);
            if (now - freeSince[sensorId] >= WaitFor(heard.Rssi)) claims.Add(resource);
        }
        return new(claims, [.. pendingReleases.Values]);
    }

    /// <summary>Ile agent czeka na wolny czujnik przy danym sygnale.</summary>
    public TimeSpan WaitFor(int? rssi)
    {
        var below = settings.StrongSignalDbm - (rssi ?? settings.UnknownSignalDbm);
        if (below <= 0) return TimeSpan.Zero;
        var wait = settings.WaitPerDb * below;
        return wait > settings.MaxWait ? settings.MaxWait : wait;
    }
}

public sealed record NotifierLockOptions
{
    /// <summary>Kandydat z priorytetem N czeka N razy tyle, zanim sięgnie po wolną blokadę (1 = najważniejszy).</summary>
    public TimeSpan PriorityStep { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Kandydat uchodzi za obecnego, gdy jego sygnał życia jest nie starszy niż…</summary>
    public TimeSpan AliveWithin { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>Ważniejszy kandydat musi być obecny tyle czasu, zanim obecny nadawca odda mu blokadę.</summary>
    public TimeSpan HandoverAfter { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Bez tyle ważności blokady nadawca nie zaczyna wysyłki maila.</summary>
    public TimeSpan MinimumForMail { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Po oddaniu ważniejszemu nie sięga po blokadę, dopóki tamten jest obecny – przez tyle czasu.</summary>
    public TimeSpan CooldownAfterHandover { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// Blokada nadawcy „nadaję teraz” po stronie kandydata (ARCHITEKTURA.md, zasada 2): bez blokady żadnego maila; przy wolnej
/// blokadzie czeka według priorytetu; trzymaną przedłuża, a oddaje ważniejszemu kandydatowi, który wrócił i jest obecny
/// dłuższą chwilę – tylko w bezpiecznym momencie (nic w trakcie wysyłki).
/// </summary>
public sealed class NotifierLockPolicy(TimeProvider clock, LeaseLedger ledger, NotifierLockOptions? options = null)
{
    private readonly NotifierLockOptions settings = options ?? new();
    private readonly Lock gate = new();
    private AgentIdentity? me;
    private NotifierView? notifier;
    private IReadOnlyList<AgentView> agents = [];
    private DateTimeOffset? serverTime;
    private DateTimeOffset? freeSince;
    private DateTimeOffset? betterAliveSince;
    private DateTimeOffset cooldownUntil = DateTimeOffset.MinValue;
    private LeaseRelease? pendingRelease;

    public void Observe(SyncResponse response)
    {
        lock (gate)
        {
            me = response.Agent;
            notifier = response.Notifier;
            agents = response.Agents;
            serverTime = response.ServerTime;
            pendingRelease = null;
        }
    }

    /// <summary>Agenci z ostatniej synchronizacji z czasem bazy, do którego liczy się ich <c>lastSeenSecondsAgo</c>
    /// (nadawca wysyła z tego alert „agent milczy”); null przed pierwszą synchronizacją.</summary>
    public (IReadOnlyList<AgentView> Agents, DateTimeOffset ServerTime)? AgentsSnapshot
    {
        get { lock (gate) return serverTime is { } at ? (agents, at) : null; }
    }

    /// <summary>Czy wolno teraz zacząć wysyłkę maila: blokada jest i zostało jej co najmniej <see cref="NotifierLockOptions.MinimumForMail"/>.</summary>
    public bool CanSendMail => ledger.Holds(CloudProtocol.NotifierResource, settings.MinimumForMail);

    /// <summary>Epoka blokady do <c>mail_claim</c> i pozostałych czynności nadawcy.</summary>
    public long? Epoch => ledger.Epoch(CloudProtocol.NotifierResource);

    /// <param name="safePoint">true między cyklami wysyłki – tylko wtedy blokadę wolno oddać ważniejszemu kandydatowi.</param>
    public LeasePlan Plan(bool safePoint = true)
    {
        lock (gate) return PlanCore(safePoint);
    }

    private LeasePlan PlanCore(bool safePoint)
    {
        if (me is null) return Pending();   // przed pierwszą synchronizacją agent nie zna swojej roli
        var now = clock.GetUtcNow();
        var resource = CloudProtocol.NotifierResource;
        var held = ledger.Epoch(resource) is not null;

        if (!me.CanSendMail)
        {
            if (held) Give(now);
            return Pending();
        }

        var myPriority = me.SenderPriority ?? int.MaxValue;
        var betterAlive = agents.Any(a => a.CanSendMail && a.Name != me.Name && (a.SenderPriority ?? int.MaxValue) < myPriority
                                          && a.LastSeenSecondsAgo is { } ago && ago <= settings.AliveWithin.TotalSeconds);
        if (held)
        {
            if (betterAlive)
            {
                betterAliveSince ??= now;
                if (safePoint && now - betterAliveSince >= settings.HandoverAfter)
                {
                    Give(now);
                    return Pending();
                }
            }
            else betterAliveSince = null;
            return new([resource], []);
        }

        betterAliveSince = null;
        if (betterAlive && now < cooldownUntil) return Pending();
        if (notifier?.Holder is { } holder)
        {
            freeSince = null;
            return holder == me.Name ? new([resource], []) : Pending();
        }
        freeSince ??= now;
        return now - freeSince >= settings.PriorityStep * myPriority ? new([resource], []) : Pending();
    }

    /// <summary>Planowe zatrzymanie: oddaje blokadę (po zakończeniu trwającej wysyłki).</summary>
    public LeasePlan ReleaseForShutdown()
    {
        lock (gate)
        {
            if (ledger.Epoch(CloudProtocol.NotifierResource) is not null) Give(clock.GetUtcNow(), cooldown: false);
            return Pending();
        }
    }

    private void Give(DateTimeOffset now, bool cooldown = true)
    {
        var resource = CloudProtocol.NotifierResource;
        if (ledger.Epoch(resource) is { } epoch) pendingRelease = new(resource, epoch);
        ledger.Forget(resource);
        betterAliveSince = null;
        freeSince = null;
        if (cooldown) cooldownUntil = now + settings.CooldownAfterHandover;
    }

    private LeasePlan Pending() => pendingRelease is { } release ? new([], [release]) : LeasePlan.Empty;
}
