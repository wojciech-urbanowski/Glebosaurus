using Glebosaurus.Core.Domain;
using Microsoft.Extensions.Logging;

namespace Glebosaurus.Core.Cloud;

/// <summary>
/// Lokalna kopia odczytów z chmury (w agencie: <c>SqliteMonitorStore</c>). Odczyty innych agentów trafiają do tej samej
/// tabeli co własne, ale jako już przyjęte przez chmurę – nie wracają do bufora wysyłki.
/// </summary>
public interface IReadingMirrorStore
{
    /// <summary>Numer ostatniego odczytu z chmury <paramref name="source"/> zapisanego lokalnie; null – jeszcze nic.</summary>
    Task<long?> GetFeedCursorAsync(string source, CancellationToken cancellationToken);

    /// <summary>
    /// Dopisuje odczyty z chmury (te, których jeszcze nie ma – własnych nie zmienia) i nowy numer ostatniego odczytu, w
    /// jednej transakcji: po przerwie kopia rusza dokładnie od miejsca, w którym skończyła.
    /// </summary>
    /// <returns>Ile odczytów faktycznie dopisano (bez tych, które już były – np. własnych).</returns>
    Task<int> AddFromCloudAsync(string source, IReadOnlyCollection<StoredReading> readings, long cursor, CancellationToken cancellationToken);
}

public sealed record ReadingMirrorOptions
{
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Odczytów w jednej paczce (baza oddaje do 10 000).</summary>
    public int PageSize { get; init; } = 5000;

    /// <summary>Odstępy po kolejnych porażkach (brak sieci, chmura niedostępna); ostatni się powtarza.</summary>
    public IReadOnlyList<TimeSpan> RetryDelays { get; init; } =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10)];
}

/// <param name="Cursor">Numer ostatniego odczytu z chmury w kopii.</param>
/// <param name="Added">Ile nowych odczytów dopisała ostatnia udana kopia.</param>
/// <param name="Stopped">Token odrzucony – kopia stoi do ponownej rejestracji.</param>
public sealed record ReadingMirrorState(DateTimeOffset? LastSuccessAt, DateTimeOffset? LastAttemptAt, string? LastError, long? Cursor, int Added,
    bool Stopped)
{
    public static readonly ReadingMirrorState NotStarted = new(null, null, null, null, 0, false);
}

/// <summary>
/// Kopia odczytów wszystkich agentów u agenta z lokalnym panelem WWW (ARCHITEKTURA.md, zasada 15): co kilka minut
/// dociąga z chmury (<c>readings_feed</c>) odczyty przyjęte po ostatnim znanym numerze – także te dosłane z opóźnieniem –
/// i zapisuje je w lokalnej bazie. Panel, wykresy i odstęp odczytów zbieracza widzą wtedy pomiary wszystkich agentów,
/// także bez sieci (z tym, co zdążyło przyjść). Odczyty starsze niż lokalna retencja są pomijane.
/// </summary>
public sealed class CloudReadingMirror(
    ICloudApi api, IReadingMirrorStore store, string source, TimeSpan retention, TimeProvider clock, ILogger logger,
    ReadingMirrorOptions? options = null)
{
    private readonly ReadingMirrorOptions settings = options ?? new();
    private readonly Lock gate = new();
    private TaskCompletionSource wakeSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile ReadingMirrorState state = ReadingMirrorState.NotStarted;
    private int failures;
    private bool caughtUp;

    public ReadingMirrorState State => state;

    /// <summary>Budzi pętlę przed końcem odstępu (np. po zleceniu odczytu z panelu). Po porażce nie skraca ponowień.</summary>
    public void Wake()
    {
        lock (gate) wakeSignal.TrySetResult();
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            bool ok;
            try
            {
                ok = await PullAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Kopia odczytów z chmury zakończyła się nieoczekiwanym błędem.");
                ok = false;
            }
            if (state.Stopped) return;
            try
            {
                await WaitAsync(ok ? settings.Interval : settings.RetryDelays[Math.Clamp(failures - 1, 0, settings.RetryDelays.Count - 1)],
                    allowWake: ok, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>Dociąga wszystko, co czeka (paczkami). Publiczna, żeby testy mogły ją wołać na ręcznie przesuwanym czasie.</summary>
    /// <returns>false – chmura niedostępna albo odrzuciła token (stan mówi, co się stało).</returns>
    public async Task<bool> PullAsync(CancellationToken cancellationToken)
    {
        if (state.Stopped) return false;
        var now = clock.GetUtcNow();
        var cursor = await store.GetFeedCursorAsync(source, cancellationToken) ?? 0;
        var added = 0;
        try
        {
            while (true)
            {
                var feed = await api.ReadingsFeedAsync(cursor, settings.PageSize, cancellationToken);
                var keepFrom = clock.GetUtcNow() - retention;
                var readings = feed.Readings.Select(r => r.ToStored()).Where(r => r.At >= keepFrom).ToList();
                if (feed.Last != cursor || readings.Count > 0)
                    added += await store.AddFromCloudAsync(source, readings, Math.Max(feed.Last, cursor), cancellationToken);
                cursor = Math.Max(feed.Last, cursor);
                if (!feed.More || feed.Readings.Count == 0) break;
            }
        }
        catch (CloudApiException e)
        {
            failures++;
            var stopped = e.Kind == CloudErrorKind.Unauthorized;
            if (stopped)
                logger.LogError("Kopia odczytów z chmury: chmura odrzuciła token agenta – kopia stoi do ponownej rejestracji.");
            else if (failures == 1)
                logger.LogWarning("Kopia odczytów z chmury nie powiodła się: {Error}; panel pokazuje to, co już jest w lokalnej bazie.", e.Message);
            state = state with { LastAttemptAt = now, LastError = e.Message, Cursor = cursor, Stopped = stopped };
            return false;
        }

        if (failures > 0) logger.LogInformation("Kopia odczytów z chmury znów działa.");
        failures = 0;
        if (!caughtUp)
        {
            caughtUp = true;
            logger.LogInformation("Kopia odczytów z chmury: dopisano {Count} odczytów innych agentów (numer {Cursor}).", added, cursor);
        }
        else if (added > 0)
        {
            logger.LogDebug("Kopia odczytów z chmury: dopisano {Count}.", added);
        }
        state = new(clock.GetUtcNow(), now, null, cursor, added, false);
        return true;
    }

    private async Task WaitAsync(TimeSpan delay, bool allowWake, CancellationToken cancellationToken)
    {
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
