using Android.Content;
using Android.OS;
using Glebosaurus.Android.Ble;
using Glebosaurus.Cloud;
using Glebosaurus.Core.Cloud;
using Glebosaurus.Core.Config;
using Glebosaurus.Core.Hosting;
using Glebosaurus.Core.Logging;
using Glebosaurus.Core.Monitoring;
using Glebosaurus.Data;
using Microsoft.Extensions.Logging;

namespace Glebosaurus.Android;

internal enum HostPhase
{
    Stopped,
    Starting,
    Running,

    /// <summary>Przebieg zakończony błędem (np. pierwsze uruchomienie bez sieci) – kolejna próba o <see cref="HostState.RetryAt"/>.</summary>
    Retrying,

    NotEnrolled,

    /// <summary>Chmura odrzuciła token przy starcie – potrzebna ponowna rejestracja.</summary>
    Revoked
}

internal sealed record HostState(HostPhase Phase, string? Message = null, DateTimeOffset? RetryAt = null);

/// <summary>Czujnik do podglądu zasięgu: z konfiguracji z chmury albo z jej lokalnej kopii.</summary>
internal sealed record KnownSensor(string Name, string Mac, string Uuid);

/// <summary>
/// Agent w procesie aplikacji – jeden na proces, wspólny dla usługi (praca w tle) i ekranu (stan, rejestracja, podgląd
/// zasięgu). Składa te same części co agent na Windows (<see cref="CloudRuntime"/>: konfiguracja z chmury albo z kopii,
/// synchronizacja, zbieracz, kopia odczytów wszystkich agentów), ale bez nadawcy maili i bez panelu WWW. Przebieg zakończony
/// błędem (np. bez sieci przy pierwszym starcie) wraca po 1, 2, 5, potem co 15 min (<see cref="RestartBackoff"/>).
/// </summary>
internal sealed class AgentHost
{
    /// <summary>Ponowienie nasłuchu po błędzie skanu – dłuższe niż na Windows (1 s): Android liczy każdy start skanu.</summary>
    private static readonly TimeSpan ListenRetryPause = TimeSpan.FromSeconds(8);

    private static readonly Lock Creation = new();
    private static AgentHost? instance;

    private readonly Context context;
    private readonly ILogger logger;
    private readonly Lock gate = new();
    private CancellationTokenSource? stop;
    private Task? loop;
    private volatile HostState state = new(HostPhase.Stopped);
    private volatile Run? current;

    private sealed record Run(CloudPanel Panel, CloudConfigSource Config, CloudSync Sync, string Project);

    private AgentHost(Context context)
    {
        this.context = context;
        Storage = new AgentStorage(context);
        RecentLog = new RecentLogProvider();
        Loggers = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddFilter("System.Net.Http", LogLevel.Warning);
            builder.AddProvider(new FileLoggerProvider(Storage.LogFiles, 7));
            builder.AddProvider(RecentLog);
            builder.AddProvider(new LogcatLoggerProvider());
        });
        logger = Loggers.CreateLogger("Glebosaurus");
        Radio = new AndroidRadio(context, logger);
    }

    public static AgentHost Get(Context context)
    {
        lock (Creation) return instance ??= new AgentHost(context.ApplicationContext ?? context);
    }

    public AgentStorage Storage { get; }
    public RecentLogProvider RecentLog { get; }
    public ILoggerFactory Loggers { get; }
    public AndroidRadio Radio { get; }
    public HostState State => state;
    public bool IsRunning => loop is { IsCompleted: false };

    public void Start()
    {
        lock (gate)
        {
            if (IsRunning) return;
            stop = new CancellationTokenSource();
            var token = stop.Token;
            loop = Task.Run(() => SuperviseAsync(token), CancellationToken.None);
        }
    }

    /// <summary>Czyste zatrzymanie: zbieracz kończy bieżący odczyt, ostatnia synchronizacja oddaje dzierżawy (najwyżej ok. 30 s).</summary>
    public async Task StopAsync()
    {
        Task? running;
        lock (gate)
        {
            stop?.Cancel();
            running = loop;
        }
        if (running is null) return;
        try { await running.WaitAsync(TimeSpan.FromSeconds(60)); }
        catch (TimeoutException) { logger.LogWarning("Agent nie zatrzymał się w 60 s."); }
    }

    /// <summary>Rejestracja kodem z QR: token od chmury, zapis (Keystore) i adres projektu. Działający agent jest zatrzymywany.</summary>
    /// <exception cref="CloudApiException">Kod nieznany, użyty albo wygasły (<see cref="CloudErrorKind.Unauthorized"/>) albo brak sieci.</exception>
    public async Task<EnrollResult> EnrollAsync(EnrollmentLink link, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var enrolled = await SupabaseCloudApi.EnrollAsync(http, link.ProjectUrl, link.PublishableKey, link.Code, cancellationToken);
        await StopAsync();
        await Storage.SaveEnrollmentAsync(link, enrolled, cancellationToken);
        logger.LogInformation("Zarejestrowano agenta „{Agent}” w {Project} ({Role}).", enrolled.Agent, link.ProjectUrl.Host,
            enrolled.CanSendMail ? "w chmurze z rolą nadawcy – telefon i tak nie wysyła maili" : "zbieracz");
        return enrolled;
    }

    /// <summary>Stan do ekranu: sekcje z <see cref="AgentScreenText"/>; null, gdy agent nie pracuje albo jeszcze nie ma konfiguracji.</summary>
    public async Task<IReadOnlyList<ScreenSection>?> ScreenAsync(CancellationToken cancellationToken)
    {
        if (await StatusAsync(cancellationToken) is not { } status) return null;
        return AgentScreenText.Build(status, PhoneState.Read(context, Radio.State), TimeZoneInfo.Local);
    }

    /// <summary>Jedno zdanie do stałego powiadomienia.</summary>
    public async Task<string> SummaryAsync(CancellationToken cancellationToken)
    {
        var host = state;
        if (host.Phase != HostPhase.Running) return Describe(host);
        if (await StatusAsync(cancellationToken) is not { Cloud: { } cloud } status) return "Wczytuje konfigurację…";
        if (cloud.Revoked) return "Agent odcięty – zarejestruj ponownie";
        var mine = status.Sensors.Count(s => s.HolderIsMe);
        var sync = cloud.SyncFailures > 0 ? "brak łączności z chmurą"
            : cloud.LastSyncAt is { } at ? $"synchronizacja {at.ToLocalTime():HH:mm}" : "pierwsza synchronizacja…";
        return $"Czyta {mine} z {status.Sensors.Count} czujników · {sync}";
    }

    public static string Describe(HostState host) => host.Phase switch
    {
        HostPhase.Stopped => "Agent zatrzymany.",
        HostPhase.Starting => "Agent się uruchamia…",
        HostPhase.Running => "Agent pracuje.",
        HostPhase.Retrying => $"Agent czeka na ponowną próbę o {host.RetryAt?.ToLocalTime():HH:mm}: {host.Message}",
        HostPhase.NotEnrolled => host.Message ?? "Telefon nie jest zarejestrowany w chmurze.",
        HostPhase.Revoked => "Chmura odrzuciła token agenta – zarejestruj telefon ponownie nowym kodem.",
        _ => host.Phase.ToString()
    };

    /// <summary>Czujniki z bieżącej konfiguracji, a bez działającego agenta – z lokalnej kopii (do podglądu zasięgu).</summary>
    public async Task<IReadOnlyList<KnownSensor>> KnownSensorsAsync(CancellationToken cancellationToken)
    {
        if (current?.Config.Current is { } config)
            return [.. config.Config.Sensors.Select(s => new KnownSensor(s.Name, s.Tuya.Mac, s.Tuya.Uuid))];
        try
        {
            if (await Storage.ConfigCache.LoadAsync(cancellationToken) is { } cached)
                return [.. cached.Config.Sensors.Select(s => new KnownSensor(s.Name, s.Mac, s.Tuya?.Uuid ?? ""))];
        }
        catch (InvalidDataException e)
        {
            logger.LogWarning("Kopia konfiguracji nieczytelna: {Error}", e.Message);
        }
        return [];
    }

    /// <summary>Odczyt na żądanie wszystkich czujników – polecenia w chmurze do właścicieli dzierżaw (jak w panelu WWW).</summary>
    public async Task<string> RequestReadAllAsync(CancellationToken cancellationToken)
    {
        if (current is not { } run) return "Agent nie pracuje.";
        var result = await run.Panel.RequestReadAsync(null, cancellationToken);
        var queued = result.Items.Count(i => i.Status == ReadRequestStatus.Queued);
        var unavailable = result.Items.Count(i => i.Status == ReadRequestStatus.Unavailable);
        return unavailable > 0 ? "Chmura niedostępna – nie zlecono odczytu."
            : $"Zlecono odczyt {queued} czujników (czyta je agent z dzierżawą; czujnik najwyżej raz na minutę).";
    }

    private async Task<MonitorStatus?> StatusAsync(CancellationToken cancellationToken)
    {
        if (current is not { } run || run.Config.Current is null) return null;
        try
        {
            return await run.Panel.StatusAsync(new MonitorHostInfo("Android", "", Storage.DatabasePath, Storage.LogDirectory), cancellationToken);
        }
        catch (InvalidOperationException) { return null; }   // konfiguracja jeszcze się wczytuje
    }

    private void SetState(HostState next)
    {
        state = next;
        StateChanged?.Invoke();
    }

    /// <summary>Zmiana fazy agenta (wątek tła) – usługa odświeża wtedy powiadomienie.</summary>
    public event Action? StateChanged;

    private async Task SuperviseAsync(CancellationToken cancellationToken)
    {
        var backoff = new RestartBackoff();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var started = TimeProvider.System.GetTimestamp();
                SetState(new(HostPhase.Starting));
                var outcome = await RunOnceAsync(cancellationToken);
                if (cancellationToken.IsCancellationRequested) break;
                if (outcome.Phase is HostPhase.NotEnrolled or HostPhase.Revoked)
                {
                    SetState(outcome);
                    return;
                }
                var delay = backoff.Next(ExitCodes.Error, TimeProvider.System.GetElapsedTime(started));
                SetState(outcome with { Phase = HostPhase.Retrying, RetryAt = DateTimeOffset.Now + delay });
                logger.LogWarning("Agent zatrzymał się ({Reason}) – kolejna próba za {Minutes} min.", outcome.Message, delay.TotalMinutes);
                await Task.Delay(delay, cancellationToken);
            }
        }
        catch (System.OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception e)
        {
            logger.LogError(e, "Nadzór agenta zakończył się błędem.");
        }
        SetState(new(HostPhase.Stopped));
    }

    private async Task<HostState> RunOnceAsync(CancellationToken cancellationToken)
    {
        if (Storage.Cloud is not { } cloud)
            return new(HostPhase.NotEnrolled, "Telefon nie jest zarejestrowany – zeskanuj kod QR od właściciela.");
        string? token;
        try { token = await Storage.Tokens.LoadAsync(cancellationToken); }
        catch (InvalidDataException e) { return new(HostPhase.NotEnrolled, $"Token agenta nieczytelny ({e.Message}) – zarejestruj telefon ponownie."); }
        if (token is null) return new(HostPhase.NotEnrolled, "Brak tokenu agenta – zarejestruj telefon kodem QR.");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var api = new SupabaseCloudApi(http, cloud.Url, cloud.PublishableKey, token);
        var clock = TimeProvider.System;
        var ledger = new LeaseLedger(clock);
        var sensorPolicy = new SensorLeasePolicy(clock, ledger);
        // Telefon nie wysyła maili (decyzja 2026-11-16) – nawet z rolą nadawcy nadaną w chmurze przez pomyłkę.
        var notifierPolicy = new NotifierLockPolicy(clock, ledger, new NotifierLockOptions { CollectorOnly = true });
        var hearing = new SensorHearingTracker(clock);
        var local = new AgentConfig
        {
            MonitorName = Build.Model ?? "android",
            Database = Storage.DatabasePath,
            Cloud = new CloudAccessConfig { Url = cloud.Url.GetLeftPart(UriPartial.Authority), PublishableKey = cloud.PublishableKey }
        };
        var configSource = new CloudConfigSource(api, Storage.ConfigCache, local, clock, logger);
        var store = new SqliteMonitorStore(Storage.DatabasePath);
        await store.InitializeAsync(cancellationToken);
        var sync = new CloudSync(api, store, hearing, ledger, sensorPolicy, notifierPolicy,
            () => configSource.Current?.Config.Sensors.Select(s => s.Id).ToList() ?? [],
            () => PhoneState.Read(context, Radio.State), clock, logger);
        var collector = new CollectorEngine(() => configSource.Current, store,
            new ListenThenReadSensorReader<AndroidFound>(Radio, clock, logger, ListenRetryPause), Radio, sensorPolicy, hearing, api,
            Storage.LogFiles, clock, logger);
        // Kopia odczytów wszystkich agentów: ekran pokazuje najnowszy odczyt każdego czujnika, także czytanego przez bestia5.
        var mirror = new CloudReadingMirror(api, store, cloud.Url.Host, TimeSpan.FromDays(local.RetentionDays), clock, logger);
        var runtime = new CloudRuntime(configSource, sync, collector, notifier: null, logger, mirror);
        var panel = new CloudPanel(cloud.Url.Host, () => configSource.Current, store, api, () => collector.State, () => sync.State,
            () => MailRuntimeState.Unused, () => mirror.State, sync.Wake, clock, logger);

        logger.LogInformation("Agent Android {Version} ({Model}, Android {Release}): projekt {Project}, sekrety: {Protection}.",
            Glebosaurus.Core.AppInfo.Version, Build.Model, Build.VERSION.Release, cloud.Url.Host, Storage.Protector.Description);
        current = new(panel, configSource, sync, cloud.Url.Host);
        SetState(new(HostPhase.Running));
        try
        {
            await runtime.RunAsync(cancellationToken);
            return new(HostPhase.Stopped);
        }
        catch (CloudApiException e) when (e.Kind == CloudErrorKind.Unauthorized)
        {
            logger.LogError("Chmura odrzuciła token agenta ({Error}) – potrzebna ponowna rejestracja.", e.Message);
            return new(HostPhase.Revoked, e.Message);
        }
        catch (System.OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(HostPhase.Stopped);
        }
        catch (Exception e)
        {
            if (e is InvalidOperationException) logger.LogError("{Error}", e.Message);
            else logger.LogError(e, "Agent zakończył pracę błędem.");
            return new(HostPhase.Retrying, e.Message);
        }
        finally
        {
            current = null;
        }
    }
}
