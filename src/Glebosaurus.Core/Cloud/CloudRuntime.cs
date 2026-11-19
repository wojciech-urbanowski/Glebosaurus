using Microsoft.Extensions.Logging;

namespace Glebosaurus.Core.Cloud;

/// <summary>
/// Agent w jednym procesie (ARCHITEKTURA.md, zasada 15): konfiguracja z chmury (albo z kopii), pętla
/// synchronizacji, zbieracz i nadawca – każde we własnej pętli. Po każdej udanej synchronizacji: odświeżenie konfiguracji
/// przy nowej wersji i rozdział poleceń (zbieracz, nadawca). Agent z lokalnym panelem ma jeszcze kopię odczytów wszystkich
/// agentów (<see cref="CloudReadingMirror"/>). Agent bez poczty (Android) nie ma nadawcy – <c>notifier</c> = null.
/// Zatrzymanie: zbieracz i nadawca kończą bieżący krok (odczyt albo wysyłkę), potem ostatnia synchronizacja oddaje dzierżawy
/// i blokadę – inni przejmują je w sekundy.
/// </summary>
public sealed class CloudRuntime
{
    /// <summary>Ile najwyżej czekać na oddanie dzierżaw przy zatrzymaniu (sieć może nie odpowiadać).</summary>
    public static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(30);

    private readonly CloudConfigSource configSource;
    private readonly CloudSync sync;
    private readonly CollectorEngine collector;
    private readonly NotifierEngine? notifier;
    private readonly CloudReadingMirror? mirror;
    private readonly ILogger logger;

    public CloudRuntime(CloudConfigSource configSource, CloudSync sync, CollectorEngine collector, NotifierEngine? notifier, ILogger logger,
        CloudReadingMirror? mirror = null)
    {
        this.configSource = configSource;
        this.sync = sync;
        this.collector = collector;
        this.notifier = notifier;
        this.mirror = mirror;
        this.logger = logger;

        collector.ReadingsStored = sync.Wake;
        collector.LeasesChanged = sync.Wake;
        sync.NotifierSafePoint = () => notifier is null || !notifier.Busy;
        sync.Synced = AfterSyncAsync;
    }

    /// <exception cref="InvalidOperationException">Brak konfiguracji (ani chmury, ani kopii).</exception>
    /// <exception cref="CloudApiException">Token odrzucony przy starcie.</exception>
    /// <summary>Po wczytaniu konfiguracji, zanim ruszą pętle (np. start lokalnego panelu, który bez niej nie ma czego
    /// pokazać). Błąd trafia do dziennika i nie zatrzymuje agenta.</summary>
    public Func<CancellationToken, Task>? Loaded { get; set; }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await configSource.LoadAsync(cancellationToken);
        if (Loaded is { } loaded)
        {
            try { await loaded(cancellationToken); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Start po wczytaniu konfiguracji (panel) nie powiódł się – agent pracuje dalej.");
            }
        }
        using var loops = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var collecting = collector.RunAsync(loops.Token);
        var notifying = notifier?.RunAsync(loops.Token) ?? Task.CompletedTask;
        var mirroring = mirror?.RunAsync(loops.Token) ?? Task.CompletedTask;
        try
        {
            await sync.RunAsync(cancellationToken);
            if (!cancellationToken.IsCancellationRequested)
            {
                // Token unieważniony: synchronizacja stoi, dzierżawy wygasną same – zbieracz kończy, co ma, i czeka na koniec.
                logger.LogError("Agent nie ma już dostępu do chmury – pracuje do wygaśnięcia dzierżaw i czeka na zatrzymanie.");
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            await loops.CancelAsync();
            await WaitQuietlyAsync(collecting, "zbieracz");
            await WaitQuietlyAsync(notifying, "nadawca");
            await WaitQuietlyAsync(mirroring, "kopia odczytów");
            using var timeout = new CancellationTokenSource(ShutdownTimeout);
            try { await sync.ShutdownAsync(timeout.Token); }
            catch (Exception e) when (e is OperationCanceledException or CloudApiException)
            {
                logger.LogWarning("Oddanie dzierżaw przy zatrzymaniu nie powiodło się ({Error}) – wrócą do puli po wygaśnięciu.", e.Message);
            }
            logger.LogInformation("Agent zatrzymany.");
        }
    }

    /// <summary>Po udanej synchronizacji: nowa konfiguracja (nowsza wersja albo zmieniona rola agenta) i polecenia do
    /// zbieracza albo nadawcy.</summary>
    public async Task AfterSyncAsync(CancellationToken cancellationToken)
    {
        await configSource.RefreshAsync(sync.ConfigVersion, sync.State.View?.Agent, cancellationToken);
        foreach (var command in sync.TakeNewCommands())
        {
            if (await collector.HandleCommandAsync(command, cancellationToken)) continue;
            if (notifier is not null && await notifier.HandleCommandAsync(command, cancellationToken)) continue;
            logger.LogWarning("Polecenie z chmury „{Kind}” ({Id}) nie jest obsługiwane przez tego agenta – wygaśnie samo.", command.Kind, command.Id);
        }
    }

    private async Task WaitQuietlyAsync(Task loop, string name)
    {
        try { await loop; }
        catch (OperationCanceledException) { }
        catch (Exception e) { logger.LogError(e, "Pętla „{Name}” zakończyła się błędem przy zatrzymaniu.", name); }
    }
}
