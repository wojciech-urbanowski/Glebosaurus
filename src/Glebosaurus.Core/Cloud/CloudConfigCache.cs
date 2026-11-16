using System.Text.Json;

namespace Glebosaurus.Core.Cloud;

/// <summary>
/// Ochrona lokalnej kopii sekretów (klucze Tuya w konfiguracji): Windows – DPAPI bieżącego użytkownika, Linux – plik
/// tylko dla właściciela (600), Android – Keystore (faza 2.2). Implementacje: <c>Glebosaurus.Cloud.LocalProtectors</c>.
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
/// czyta czujniki do końca dzierżaw. Zawiera klucze Tuya, więc jest chroniona (<see cref="ILocalProtector"/>), a na
/// systemach uniksowych plik ma prawa 600. Zapis atomowy (plik tymczasowy + zamiana).
/// </summary>
public sealed class CloudConfigCache(string path, ILocalProtector protector)
{
    private static readonly byte[] Magic = "GLBC1\n"u8.ToArray();

    public string Path { get; } = System.IO.Path.GetFullPath(path);

    public async Task SaveAsync(CloudConfig config, DateTimeOffset savedAt, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new CachedCloudConfig(savedAt, config), CloudJsonContext.Default.CachedCloudConfig);
        var content = (byte[])[.. Magic, .. protector.Protect(json)];
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temporary = $"{Path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, options))
            {
                await stream.WriteAsync(content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(true);
            }
            File.Move(temporary, Path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <returns>null, gdy kopii nie ma.</returns>
    /// <exception cref="InvalidDataException">Plik uszkodzony albo z innego konta/komputera.</exception>
    public async Task<CachedCloudConfig?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(Path)) return null;
        var content = await File.ReadAllBytesAsync(Path, cancellationToken);
        if (content.Length <= Magic.Length || !content.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException($"{Path}: to nie jest zapisana konfiguracja Glebosaurusa.");
        byte[] json;
        try { json = protector.Unprotect(content[Magic.Length..]); }
        catch (Exception e) when (e is not InvalidDataException and not OperationCanceledException)
        {
            throw new InvalidDataException($"{Path}: nie da się odczytać kopii konfiguracji ({protector.Description}) – inny użytkownik albo komputer?", e);
        }
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
