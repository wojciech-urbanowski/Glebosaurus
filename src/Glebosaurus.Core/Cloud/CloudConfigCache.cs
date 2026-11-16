using System.Text.Json;

namespace Glebosaurus.Core.Cloud;

/// <summary>
/// Ochrona lokalnej kopii sekretów (klucze Tuya w konfiguracji, token agenta): Windows – DPAPI bieżącego użytkownika, Linux –
/// plik tylko dla właściciela (600), Android – Keystore (faza 2.2). Implementacje: <c>Glebosaurus.Cloud.LocalProtectors</c>.
/// </summary>
public interface ILocalProtector
{
    /// <summary>Krótki opis do dziennika, np. „DPAPI (bieżący użytkownik)”.</summary>
    string Description { get; }

    byte[] Protect(byte[] data);

    /// <exception cref="InvalidDataException">Nie da się odszyfrować (inny użytkownik, inny komputer, uszkodzony plik).</exception>
    byte[] Unprotect(byte[] data);
}

/// <summary>Ostatnia dobra konfiguracja z chmury z chwilą zapisu.</summary>
public sealed record CachedCloudConfig(DateTimeOffset SavedAt, CloudConfig Config);

/// <summary>
/// Ostatnia dobra konfiguracja z chmury na dysku agenta (ARCHITEKTURA.md, zasada 1): bez sieci agent startuje z niej i
/// czyta czujniki do końca dzierżaw. Zawiera klucze Tuya, więc jest chroniona (<see cref="ProtectedFile"/>).
/// </summary>
public sealed class CloudConfigCache(string path, ILocalProtector protector)
{
    private const string Header = "GLBC1\n";

    public string Path { get; } = System.IO.Path.GetFullPath(path);

    public Task SaveAsync(CloudConfig config, DateTimeOffset savedAt, CancellationToken cancellationToken) =>
        ProtectedFile.WriteAsync(Path, Header,
            JsonSerializer.SerializeToUtf8Bytes(new CachedCloudConfig(savedAt, config), CloudJsonContext.Default.CachedCloudConfig),
            protector, cancellationToken);

    /// <returns>null, gdy kopii nie ma.</returns>
    /// <exception cref="InvalidDataException">Plik uszkodzony albo z innego konta/komputera.</exception>
    public async Task<CachedCloudConfig?> LoadAsync(CancellationToken cancellationToken)
    {
        if (await ProtectedFile.ReadAsync(Path, Header, protector, cancellationToken) is not { } json) return null;
        try
        {
            return JsonSerializer.Deserialize(json, CloudJsonContext.Default.CachedCloudConfig)
                ?? throw new InvalidDataException($"{Path}: pusta kopia konfiguracji.");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{Path}: uszkodzona kopia konfiguracji ({e.Message}).", e);
        }
    }

    public override string ToString() => $"CloudConfigCache {{ {Path}, {protector.Description} }}";
}
