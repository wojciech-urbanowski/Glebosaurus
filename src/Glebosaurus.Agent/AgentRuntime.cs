
using Glebosaurus.Core.Config;
using Glebosaurus.Core.Logging;
using Microsoft.Extensions.Logging;

namespace Glebosaurus.Agent;

internal sealed record LoadedConfig(AgentConfig Config, string ConfigPath, string DatabasePath)
{
    public string LogDirectory => Path.Combine(Path.GetDirectoryName(ConfigPath)!, "logs");

    /// <summary>Pliki dziennika agenta w <see cref="LogDirectory"/> (zapis, odczyt wczorajszego dziennika, retencja).</summary>
    public DailyLogFiles LogFiles => new(LogDirectory);

    /// <summary>Skrót hasła panelu WWW (tworzy go polecenie web-password); obok konfiguracji, poza repozytorium.</summary>
    public string WebPasswordPath => Path.Combine(Path.GetDirectoryName(ConfigPath)!, "glebosaurus-web.password");

    /// <summary>Certyfikat HTTPS panelu WWW z kluczem prywatnym (PFX bez hasła – chroniony prawami pliku, jak klucze czujników).</summary>
    public string WebCertificatePath => Path.Combine(Path.GetDirectoryName(ConfigPath)!, "glebosaurus-web.pfx");

    /// <summary>Plik z tokenem agenta (tworzy go enroll); null bez sekcji „cloud”.</summary>
    public string? CloudTokenPath => Config.Cloud is { } cloud ? AgentConfigLoader.ResolveRelative(ConfigPath, cloud.TokenFile) : null;

    /// <summary>Ostatnia dobra konfiguracja z chmury (z kluczami Tuya); null bez sekcji „cloud”.</summary>
    public string? CloudConfigCachePath => Config.Cloud is { } cloud ? AgentConfigLoader.ResolveRelative(ConfigPath, cloud.ConfigCacheFile) : null;

    /// <summary>Codzienna kopia chmury: plik administratora z hasłem bazy; null bez sekcji „cloud.backup”.</summary>
    public string? CloudBackupAdminPath => Config.Cloud?.Backup is { } backup ? AgentConfigLoader.ResolveRelative(ConfigPath, backup.AdminFile) : null;

    /// <summary>Codzienna kopia chmury: katalog kopii; null bez sekcji „cloud.backup”.</summary>
    public string? CloudBackupDirectory => Config.Cloud?.Backup is { } backup ? AgentConfigLoader.ResolveRelative(ConfigPath, backup.Directory) : null;
}

internal static class AgentRuntime
{
    public const string DefaultConfigFile = "glebosaurus.config.json";

    public static LoadedConfig LoadConfig(string? path)
    {
        var configPath = Path.GetFullPath(path ?? DefaultConfigFile);
        if (!File.Exists(configPath))
            throw new FileNotFoundException($"Nie znaleziono pliku konfiguracji agenta {configPath}. Wskaż go przez --config (wzór: examples/glebosaurus.config.json).");
        var config = AgentConfigLoader.Load(configPath);
        return new(config, configPath, AgentConfigLoader.ResolveDatabasePath(config, configPath));
    }

    /// <summary>Dziennik do pliku zawsze; na konsolę tylko, gdy ktoś patrzy (uruchomienie interaktywne).</summary>
    public static ILoggerFactory CreateLogging(DailyLogFiles files, int keepDays, bool console) => LoggerFactory.Create(builder =>
    {
        builder.SetMinimumLevel(LogLevel.Information);
        builder.AddFilter("Microsoft", LogLevel.Warning);
        builder.AddFilter("Shiny", LogLevel.Warning);
        if (console) builder.AddSimpleConsole(options => { options.SingleLine = true; options.TimestampFormat = "HH:mm:ss "; });
        builder.AddProvider(new FileLoggerProvider(files, keepDays));
    });
}
