namespace Glebosaurus.Web;

/// <summary>
/// Zapis plików z sekretami panelu (skrót hasła, klucz certyfikatu): przez plik tymczasowy i podmianę (bez półzapisanego
/// pliku po awarii), a na Linuksie z prawami 600 od chwili utworzenia. Na Windows plik dziedziczy uprawnienia katalogu
/// (profil użytkownika).
/// </summary>
internal static class PrivateFile
{
    public static void Write(string path, byte[] content)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            using (var stream = new FileStream(temporary, options)) stream.Write(content);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
