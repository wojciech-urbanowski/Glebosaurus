using Glebosaurus.Core.Config;
using Microsoft.Extensions.Logging;

namespace Glebosaurus.Core.Cloud;

/// <summary>
/// Konfiguracja agenta w trybie chmurowym (ARCHITEKTURA.md, zasady 1 i 14): przy starcie z chmury (<c>get_config</c>),
/// a bez niej z ostatniej dobrej kopii; potem odświeżana, gdy synchronizacja zobaczy nowszą wersję. Zła konfiguracja w
/// chmurze nie zatrzymuje agenta – zostaje poprzednia, a tej wersji agent drugi raz nie pobiera.
/// </summary>
/// <param name="local">Ustawienia z pliku na urządzeniu: baza, retencja, panel (reszta przychodzi z chmury).</param>
public sealed class CloudConfigSource(ICloudApi api, CloudConfigCache cache, AgentConfig local, TimeProvider clock, ILogger logger)
{
    private volatile CloudAgentConfig? current;
    private long? rejectedVersion;

    /// <summary>Obecna konfiguracja; null przed <see cref="LoadAsync"/>.</summary>
    public CloudAgentConfig? Current => current;

    /// <summary>Po każdej zmianie konfiguracji (już po zapisaniu kopii). Woła się w wątku odświeżania.</summary>
    public event Action<CloudAgentConfig>? Changed;

    /// <summary>
    /// Start agenta: z chmury, a gdy jej nie ma (sieć, uśpiony projekt, błąd) – z ostatniej dobrej kopii.
    /// </summary>
    /// <exception cref="CloudApiException">Token odrzucony (<see cref="CloudErrorKind.Unauthorized"/>) – bez sensu pracować dalej.</exception>
    /// <exception cref="InvalidOperationException">Ani chmury, ani kopii (pierwsze uruchomienie wymaga sieci).</exception>
    public async Task<CloudAgentConfig> LoadAsync(CancellationToken cancellationToken)
    {
        string reason;
        try
        {
            var cloud = await api.GetConfigAsync(cancellationToken);
            var mapped = CloudConfigMapper.Map(cloud, local);
            await SaveAsync(cloud, cancellationToken);
            Use(mapped, $"z chmury (wersja {mapped.Version})");
            return mapped;
        }
        catch (CloudApiException e) when (e.Kind != CloudErrorKind.Unauthorized)
        {
            reason = e.Message;
        }
        catch (InvalidDataException e)
        {
            reason = e.Message;
            logger.LogError("Konfiguracja z chmury jest nie do użycia: {Error}", e.Message);
        }

        CachedCloudConfig? cached;
        try { cached = await cache.LoadAsync(cancellationToken); }
        catch (InvalidDataException e)
        {
            throw new InvalidOperationException($"Brak konfiguracji: chmura niedostępna ({reason}), a lokalnej kopii nie da się odczytać ({e.Message}).", e);
        }
        if (cached is null)
            throw new InvalidOperationException($"Brak konfiguracji: chmura niedostępna ({reason}), a lokalnej kopii jeszcze nie ma – pierwsze uruchomienie wymaga sieci.");

        var fallback = CloudConfigMapper.Map(cached.Config, local, cached.SavedAt);
        logger.LogWarning("Chmura niedostępna ({Reason}) – agent pracuje na ostatniej dobrej kopii konfiguracji z {SavedAt:yyyy-MM-dd HH:mm} UTC.",
            reason, cached.SavedAt.UtcDateTime);
        Use(fallback, $"z lokalnej kopii (wersja {fallback.Version})");
        return fallback;
    }

    /// <summary>
    /// Po synchronizacji: pobiera konfigurację, gdy chmura ma nowszą wersję albo agent pracuje na kopii. Błąd sieci – spróbuje
    /// przy następnej synchronizacji; zła konfiguracja – zostaje poprzednia.
    /// </summary>
    /// <returns>true, gdy konfiguracja się zmieniła.</returns>
    public async Task<bool> RefreshAsync(long? cloudVersion, CancellationToken cancellationToken)
    {
        var known = current;
        if (cloudVersion is not { } version || version == rejectedVersion) return false;
        if (known is { FromCache: false } && version <= known.Version) return false;

        CloudConfig cloud;
        try { cloud = await api.GetConfigAsync(cancellationToken); }
        catch (CloudApiException e) when (e.Kind != CloudErrorKind.Unauthorized)
        {
            logger.LogWarning("Nie udało się pobrać nowej konfiguracji (wersja {Version}): {Error}; spróbuję przy następnej synchronizacji.", version, e.Message);
            return false;
        }

        CloudAgentConfig mapped;
        try { mapped = CloudConfigMapper.Map(cloud, local); }
        catch (InvalidDataException e)
        {
            rejectedVersion = cloud.ConfigVersion;
            logger.LogError("Konfiguracja z chmury (wersja {Version}) jest nie do użycia: {Error}; agent zostaje przy poprzedniej.", cloud.ConfigVersion, e.Message);
            return false;
        }
        await SaveAsync(cloud, cancellationToken);
        Use(mapped, $"odświeżona z chmury (wersja {mapped.Version})");
        Changed?.Invoke(mapped);
        return true;
    }

    private async Task SaveAsync(CloudConfig cloud, CancellationToken cancellationToken)
    {
        try { await cache.SaveAsync(cloud, clock.GetUtcNow(), cancellationToken); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException
                                  or System.Security.Cryptography.CryptographicException)
        {
            logger.LogWarning(e, "Nie udało się zapisać lokalnej kopii konfiguracji – bez sieci agent nie wystartuje z tą wersją.");
        }
    }

    private void Use(CloudAgentConfig config, string origin)
    {
        current = config;
        logger.LogInformation("Konfiguracja {Origin}: agent {Agent}{Role}, czujników {Count}.", origin, config.Agent.Name,
            config.Agent.CanSendMail ? $" (nadawca, priorytet {config.Agent.SenderPriority})" : "", config.Config.Sensors.Count);
        foreach (var warning in config.Warnings) logger.LogWarning("Konfiguracja: {Warning}", warning);
    }
}
