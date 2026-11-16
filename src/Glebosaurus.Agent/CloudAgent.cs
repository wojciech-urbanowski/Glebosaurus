using Glebosaurus.Agent.Ble;
using Glebosaurus.Cloud;
using Glebosaurus.Core;
using Glebosaurus.Core.Cloud;
using Glebosaurus.Core.Config;
using Glebosaurus.Core.Hosting;
using Glebosaurus.Core.Monitoring;
using Glebosaurus.Data;
using Glebosaurus.Mail;
using Microsoft.Extensions.Logging;

namespace Glebosaurus.Agent;

/// <summary>Agent w trybie chmurowym (sekcja „cloud”): rejestracja i praca ciągła (ARCHITEKTURA.md, zasada 15).</summary>
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
            Console.Error.WriteLine("W konfiguracji brakuje sekcji „cloud” (adres projektu i klucz publikowalny) – zob. examples/glebosaurus.cloud.config.json.");
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

    /// <summary>Praca ciągła w trybie chmurowym: konfiguracja z chmury, synchronizacja, zbieracz i nadawca w jednym procesie.</summary>
    public static async Task<int> RunAsync(LoadedConfig loaded, CloudAccessConfig cloud, BleHost ble, SqliteMonitorStore store, ILogger logger,
        CancellationToken cancellationToken)
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
        var radio = new ShinySensorRadio(ble);
        var sync = new CloudSync(api, store, hearing, ledger, sensorPolicy, notifierPolicy,
            () => configSource.Current?.Config.Sensors.Select(s => s.Id).ToList() ?? [],
            () => new AgentStatus(AppInfo.Version, OperatingSystem.IsWindows() ? "windows" : "linux", BleHost.Backend),
            clock, logger);
        var collector = new CollectorEngine(() => configSource.Current, store, new ListenThenReadSensorReader<SeenSensor>(radio, clock, logger),
            radio, sensorPolicy, hearing, api, loaded.LogFiles, clock, logger);
        var notifier = new NotifierEngine(() => configSource.Current, api, notifierPolicy,
            credentials => new GmailSender(credentials.GmailAddress!, credentials.AppPassword!), new HealthchecksHeartbeat(http),
            () => sync.State, clock, logger);
        var runtime = new CloudRuntime(configSource, sync, collector, notifier, logger);

        logger.LogInformation("Tryb chmurowy: projekt {Host}; token i kopia konfiguracji chronione ({Protection}).",
            cloud.ProjectUrl.Host, protector.Description);
        if (loaded.Config.Web is { Enabled: true })
            logger.LogWarning("Panel WWW w trybie chmurowym jeszcze nie działa (faza 2.1.4) – monitor pracuje bez panelu.");

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
    }
}
