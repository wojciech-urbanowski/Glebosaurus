using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Glebosaurus.Core.Config;

/// <summary>
/// Plik konfiguracji agenta (glebosaurus.config.json): dostęp do chmury i ustawienia tego urządzenia. Czujniki, progi,
/// raporty i poczta są w chmurze – trafiają tam z pliku konfiguracji chmury (<see cref="CloudSetupLoader"/>, polecenie
/// <c>cloud config</c>).
/// </summary>
public static partial class AgentConfigLoader
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true
    };

    /// <summary>Ustawienia, które są wspólne dla wszystkich agentów i żyją w chmurze – w pliku agenta byłyby po cichu pomijane.</summary>
    private static readonly string[] CloudOnlyKeys = ["sensors", "mail", "reports", "alerts", "readIntervalMinutes", "scanWindowSeconds"];

    public static AgentConfig Load(string path)
    {
        AgentConfig config;
        try
        {
            var text = File.ReadAllText(path);
            if (JsonNode.Parse(text, documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) is JsonObject root
                && root.Select(p => p.Key).FirstOrDefault(key => CloudOnlyKeys.Contains(key, StringComparer.OrdinalIgnoreCase)) is { } shared)
                throw new InvalidDataException(
                    $"„{shared}” nie należy do pliku agenta – czujniki, progi, raporty i poczta są w chmurze. Wpisz je do pliku " +
                    "konfiguracji chmury i wyślij poleceniem: glebosaurus cloud config --admin PLIK --file PLIK (wzór: examples/cloud-config.json).");
            config = JsonSerializer.Deserialize<AgentConfig>(text, JsonOptions)
                ?? throw new InvalidDataException("Pusty plik konfiguracji.");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"Nieprawidłowy JSON w {path}: {e.Message}", e);
        }
        Validate(config);
        return config;
    }

    public static string ResolveDatabasePath(AgentConfig config, string configPath) =>
        Path.GetFullPath(Path.IsPathRooted(config.Database)
            ? config.Database
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath))!, config.Database));

    /// <summary>Sprawdza ustawienia urządzenia i dostęp do chmury (bez sekcji <c>cloud</c> agent nie ma z czym pracować).</summary>
    public static void Validate(AgentConfig config)
    {
        if (config.RetentionDays < 1) throw new InvalidDataException("retentionDays: minimum 1.");
        if (config.Logs.RetentionDays is < 1 or > 365) throw new InvalidDataException("logs.retentionDays: od 1 do 365.");
        if (config.Cloud is not { } cloud)
            throw new InvalidDataException("W konfiguracji brakuje sekcji „cloud” (adres projektu Supabase i klucz publikowalny) – wzór: examples/glebosaurus.config.json.");
        ValidateCloud(cloud);

        if (config.Web is { } web)
        {
            if (web.Port is < 1024 or > 65535) throw new InvalidDataException("web.port: od 1024 do 65535.");
            if (web.SessionHours is < 0.25 or > 720) throw new InvalidDataException("web.sessionHours: od 0,25 do 720.");
            web.ParseAllowedNetworks();
        }
    }

    private static void ValidateCloud(CloudAccessConfig cloud)
    {
        if (!Uri.TryCreate(cloud.Url.Trim().TrimEnd('/'), UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps
            || !SupabaseHost().IsMatch(url.Host) || url.AbsolutePath != "/")
            throw new InvalidDataException("cloud.url: podaj adres projektu Supabase, np. https://abcdefghijklmnopqrst.supabase.co.");
        if (!cloud.PublishableKey.StartsWith("sb_publishable_", StringComparison.Ordinal))
            throw new InvalidDataException("cloud.publishableKey: podaj klucz publikowalny projektu (sb_publishable_…), nie klucz tajny.");
        if (string.IsNullOrWhiteSpace(cloud.TokenFile) || string.IsNullOrWhiteSpace(cloud.ConfigCacheFile))
            throw new InvalidDataException("cloud.tokenFile i cloud.configCacheFile nie mogą być puste.");
        if (cloud.Backup is { } backup)
        {
            if (string.IsNullOrWhiteSpace(backup.AdminFile))
                throw new InvalidDataException("cloud.backup.adminFile: podaj plik z adresem projektu i hasłem bazy (jak cloud-admin.json, bez klucza tajnego).");
            if (string.IsNullOrWhiteSpace(backup.Directory))
                throw new InvalidDataException("cloud.backup.directory nie może być pusty.");
            if (backup.Keep is < 1 or > 365)
                throw new InvalidDataException("cloud.backup.keep: od 1 do 365 kopii.");
        }
    }

    public static string ResolveRelative(string configPath, string file) =>
        Path.GetFullPath(Path.IsPathRooted(file) ? file : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath))!, file));

    [GeneratedRegex("^[a-z0-9]{20}\\.supabase\\.co$")]
    private static partial Regex SupabaseHost();
}
