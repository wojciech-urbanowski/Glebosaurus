using System.Text;

namespace Glebosaurus.Core.Cloud;

/// <summary>
/// Plik z sekretem chronionym <see cref="ILocalProtector"/> (DPAPI / prawa 600): nagłówek, potem zaszyfrowana treść; zapis
/// atomowy (plik tymczasowy + zamiana), na systemach uniksowych od razu z prawami tylko dla właściciela.
/// </summary>
public static class ProtectedFile
{
    public static async Task WriteAsync(string path, string header, byte[] data, ILocalProtector protector, CancellationToken cancellationToken)
    {
        var content = (byte[])[.. Encoding.ASCII.GetBytes(header), .. protector.Protect(data)];
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = $"{fullPath}.{Guid.NewGuid():N}.tmp";
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
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <returns>null, gdy pliku nie ma.</returns>
    /// <exception cref="InvalidDataException">Obcy plik albo nie da się go odszyfrować (inne konto, inny komputer).</exception>
    public static async Task<byte[]?> ReadAsync(string path, string header, ILocalProtector protector, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) return null;
        var content = await File.ReadAllBytesAsync(fullPath, cancellationToken);
        var magic = Encoding.ASCII.GetBytes(header);
        if (content.Length <= magic.Length || !content.AsSpan(0, magic.Length).SequenceEqual(magic))
            throw new InvalidDataException($"{fullPath}: nieoczekiwana zawartość pliku.");
        try
        {
            return protector.Unprotect(content[magic.Length..]);
        }
        catch (Exception e) when (e is not InvalidDataException and not OperationCanceledException)
        {
            throw new InvalidDataException($"{fullPath}: nie da się odczytać ({protector.Description}) – inny użytkownik albo komputer?", e);
        }
    }
}

/// <summary>
/// Token agenta (z <c>glebosaurus enroll</c>) w pliku obok konfiguracji, chroniony jak kopia konfiguracji. Nigdy nie trafia
/// do dziennika ani na konsolę.
/// </summary>
public sealed class AgentTokenStore(string path, ILocalProtector protector)
{
    private const string Header = "GLBT1\n";

    public string Path { get; } = System.IO.Path.GetFullPath(path);

    public Task SaveAsync(string token, CancellationToken cancellationToken) =>
        ProtectedFile.WriteAsync(Path, Header, Encoding.UTF8.GetBytes(token), protector, cancellationToken);

    /// <returns>null, gdy agent nie jest jeszcze zarejestrowany.</returns>
    public async Task<string?> LoadAsync(CancellationToken cancellationToken) =>
        await ProtectedFile.ReadAsync(Path, Header, protector, cancellationToken) is { } bytes ? Encoding.UTF8.GetString(bytes) : null;

    public override string ToString() => $"AgentTokenStore {{ {Path}, {protector.Description} }}";
}
