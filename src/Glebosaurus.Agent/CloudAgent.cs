using Glebosaurus.Agent.Ble;
using Glebosaurus.Cloud;
using Glebosaurus.Cloud.Admin;
using Glebosaurus.Core;
using Glebosaurus.Core.Cloud;
using Glebosaurus.Core.Config;
using Glebosaurus.Core.Hosting;
using Glebosaurus.Core.Monitoring;
using Glebosaurus.Data;
using Glebosaurus.Mail;
using Glebosaurus.Web;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Glebosaurus.Agent;

/// <summary>Agent (sekcja „cloud”): rejestracja i praca ciągła (ARCHITEKTURA.md, zasada 15).</summary>
internal static class CloudAgent
{
    /// <summary>
    /// <c>enroll</c>: jednorazowy kod (od właściciela) → token agenta zapisany obok konfiguracji, chroniony DPAPI / prawami 600.
    /// Ponowna rejestracja (nowy kod) zastępuje token – stary przestaje działać.
    /// </summary>
    public static async Task<int> EnrollAsync(CliOptions options, CancellationToken cancellationToken)
    {
        var loaded = AgentRuntime.LoadConfig(options.Config);
        if (loaded.Config.Cloud is not { } cloud)
        {
            Console.Error.WriteLine("W konfiguracji brakuje sekcji „cloud” (adres projektu i klucz publikowalny) – zob. examples/glebosaurus.config.json.");
            return ExitCodes.Error;
        }
        var code = (options.Code ?? await Cli.ReadSecretAsync("Kod rejestracji (od właściciela): ", cancellationToken)).Trim();
        if (code.Length == 0)
        {
            Console.Error.WriteLine("Nie podano kodu rejestracji.");
            return ExitCodes.Error;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        EnrollResult enrolled;
        try
        {
            enrolled = await SupabaseCloudApi.EnrollAsync(http, cloud.ProjectUrl, cloud.PublishableKey, code, cancellationToken);
        }
        catch (CloudApiException e)
        {
            Console.Error.WriteLine(e.Kind == CloudErrorKind.Unauthorized
                ? "Kod rejestracji jest nieznany, już użyty albo wygasł (albo klucz publikowalny nie pasuje do projektu) – poproś o nowy kod."
                : e.Message);
            return ExitCodes.Error;
        }

        var protector = LocalProtectors.ForThisPlatform();
        var tokens = new AgentTokenStore(loaded.CloudTokenPath!, protector);
        await tokens.SaveAsync(enrolled.Token, cancellationToken);
        Console.WriteLine($"Zarejestrowano agenta „{enrolled.Agent}” – " +
            (enrolled.CanSendMail ? $"zbiera odczyty i może wysyłać maile (priorytet {enrolled.SenderPriority})." : "zbiera odczyty (bez maili)."));
        Console.WriteLine($"Token zapisano w {tokens.Path} ({protector.Description}); nie kopiuj tego pliku na inne urządzenia.");
        Console.WriteLine("Teraz: glebosaurus run (albo uruchom usługę).");
        return ExitCodes.Success;
    }

    /// <summary>
    /// Praca ciągła: konfiguracja z chmury, synchronizacja, zbieracz i nadawca w jednym procesie; z sekcją
    /// <c>web</c> także lokalny panel i kopia odczytów wszystkich agentów, którą panel pokazuje.
    /// </summary>
    /// <param name="ble">null – agent bez Bluetooth (<c>"bluetooth": false</c>): zbieracz bez radia, o czujniki się nie ubiega.</param>
    public static async Task<int> RunAsync(LoadedConfig loaded, CloudAccessConfig cloud, BleHost? ble, SqliteMonitorStore store,
        ILoggerFactory loggers, ILogger logger, CancellationToken cancellationToken)
    {
        var protector = LocalProtectors.ForThisPlatform();
        var tokens = new AgentTokenStore(loaded.CloudTokenPath!, protector);
        string? token;
        try { token = await tokens.LoadAsync(cancellationToken); }
        catch (InvalidDataException e)
        {
            logger.LogError("Nie da się odczytać tokenu agenta: {Error} Zarejestruj agenta ponownie: glebosaurus enroll.", e.Message);
            return ExitCodes.Error;
        }
        if (token is null)
        {
            logger.LogError("Agent nie jest zarejestrowany w chmurze (brak {Path}) – uruchom „glebosaurus enroll” z kodem od właściciela.", tokens.Path);
            return ExitCodes.Error;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var api = new SupabaseCloudApi(http, cloud.ProjectUrl, cloud.PublishableKey, token);
        var clock = TimeProvider.System;
        var ledger = new LeaseLedger(clock);
        var sensorPolicy = new SensorLeasePolicy(clock, ledger);
        var notifierPolicy = new NotifierLockPolicy(clock, ledger);
        var hearing = new SensorHearingTracker(clock);
        var configSource = new CloudConfigSource(api, new CloudConfigCache(loaded.CloudConfigCachePath!, protector), loaded.Config, clock, logger);
        var radio = ble is null ? null : new ShinySensorRadio(ble);
        var bluetooth = loaded.BluetoothText;
        // Agent bez radia nie przekazuje czujników do przydziału – nie ubiega się o żaden (i tak by ich nie słyszał).
        var sync = new CloudSync(api, store, hearing, ledger, sensorPolicy, notifierPolicy,
            () => radio is null ? [] : configSource.Current?.Config.Sensors.Select(s => s.Id).ToList() ?? [],
            () => new AgentStatus(AppInfo.Version, OperatingSystem.IsWindows() ? "windows" : "linux", bluetooth),
            clock, logger);
        var collector = new CollectorEngine(() => configSource.Current, store,
            radio is null ? null : new ListenThenReadSensorReader<SeenSensor>(radio, clock, logger),
            radio, sensorPolicy, hearing, api, loaded.LogFiles, clock, logger);
        var notifier = new NotifierEngine(() => configSource.Current, api, notifierPolicy,
            credentials => new GmailSender(credentials.GmailAddress!, credentials.AppPassword!), new HealthchecksHeartbeat(http),
            () => sync.State, clock, logger);
        var withPanel = loaded.Config.Web is { Enabled: true };
        var mirror = withPanel
            ? new CloudReadingMirror(api, store, cloud.ProjectUrl.Host, TimeSpan.FromDays(loaded.Config.RetentionDays), clock, logger)
            : null;
        var runtime = new CloudRuntime(configSource, sync, collector, notifier, logger, mirror);

        WebPanel? panel = null;
        var panelData = withPanel
            ? new CloudPanel(cloud.ProjectUrl.Host, () => configSource.Current, store, api,
                () => collector.State, () => sync.State, () => notifier.MailState, () => mirror!.State, sync.Wake, clock, logger)
            : null;
        runtime.Loaded = async ct =>
        {
            if (radio is null && configSource.Current is { Agent.CanSendMail: false } current)
                logger.LogWarning("Agent „{Agent}” nie ma ani Bluetooth (\"bluetooth\": false), ani roli nadawcy – poza wysyłaniem dziennika do chmury nic nie robi. " +
                    "Rola nadawcy: glebosaurus cloud agent --admin PLIK --name {Agent} --sender-priority 1 --confirm (agent przejmie ją sam).",
                    current.Agent.Name, current.Agent.Name);
            if (panelData is not null) panel = await AgentWebPanel.TryStartAsync(loaded, configSource, panelData, store, loggers, logger, ct);
        };

        logger.LogInformation("Chmura: projekt {Host}; token i kopia konfiguracji chronione ({Protection}).",
            cloud.ProjectUrl.Host, protector.Description);

        using var backupStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var (backupData, backupLoop) = StartDailyBackup(loaded, cloud, logger, backupStop.Token);
        try
        {
            await runtime.RunAsync(cancellationToken);
            return ExitCodes.Success;
        }
        catch (CloudApiException e) when (e.Kind == CloudErrorKind.Unauthorized)
        {
            logger.LogError("Chmura odrzuciła token agenta ({Error}) – agent unieważniony? Potrzebna ponowna rejestracja: glebosaurus enroll.", e.Message);
            return ExitCodes.Error;
        }
        catch (InvalidOperationException e)
        {
            logger.LogError("{Error}", e.Message);
            return ExitCodes.Error;
        }
        finally
        {
            if (panel is not null) await panel.DisposeAsync();
            await backupStop.CancelAsync();
            try { await backupLoop; }
            catch (OperationCanceledException) { }
            if (backupData is not null) await backupData.DisposeAsync();
        }
    }

    /// <summary>
    /// Codzienna kopia chmury (sekcja <c>cloud.backup</c>): plik administratora z hasłem bazy musi wskazywać ten sam projekt.
    /// Problem z kopią to tylko wpis w dzienniku – agent pracuje dalej.
    /// </summary>
    private static (NpgsqlDataSource? Data, Task Loop) StartDailyBackup(LoadedConfig loaded, CloudAccessConfig cloud, ILogger logger,
        CancellationToken cancellationToken)
    {
        if (cloud.Backup is not { } backup) return (null, Task.CompletedTask);
        CloudAdminSettings settings;
        try
        {
            settings = CloudAdminSettings.Load(loaded.CloudBackupAdminPath!);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            logger.LogError("Kopia zapasowa chmury wyłączona: nie da się odczytać {File} ({Error}).", loaded.CloudBackupAdminPath, e.Message);
            return (null, Task.CompletedTask);
        }
        if (!string.Equals(settings.Url.Host, cloud.ProjectUrl.Host, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogError("Kopia zapasowa chmury wyłączona: {File} dotyczy innego projektu ({Other}) niż cloud.url ({Project}).",
                loaded.CloudBackupAdminPath, settings.Url.Host, cloud.ProjectUrl.Host);
            return (null, Task.CompletedTask);
        }
        if (settings.SecretKey is not null)
            logger.LogWarning("Plik {File} ma klucz tajny Supabase – agent go nie potrzebuje (do kopii wystarczy adres i hasło bazy); usuń go z pliku na tym urządzeniu.",
                loaded.CloudBackupAdminPath);

        var data = settings.CreateDataSource("glebosaurus-backup");
        var directory = loaded.CloudBackupDirectory!;
        var daily = new DailyCloudBackup(ct => CloudBackup.CreateAsync(data, settings.ProjectRef, directory, TimeProvider.System, ct),
            directory, settings.ProjectRef, backup.Keep, TimeZoneInfo.Local, TimeProvider.System, logger);
        logger.LogInformation("Kopia zapasowa chmury: raz dziennie do {Directory} (zostaje {Keep} najnowszych); ostatnia: {Last}.",
            directory, backup.Keep, daily.LastBackupAt is { } last ? last.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) : "brak");
        return (data, daily.RunAsync(cancellationToken));
    }
}
