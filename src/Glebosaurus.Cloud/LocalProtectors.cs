using System.Security.Cryptography;
using Glebosaurus.Core.Cloud;

namespace Glebosaurus.Cloud;

/// <summary>Ochrona lokalnej kopii sekretów właściwa dla systemu (ARCHITEKTURA.md, zasada 1).</summary>
public static class LocalProtectors
{
    /// <summary>Windows: DPAPI bieżącego użytkownika; Linux/macOS: bez szyfrowania, plik z prawami 600 (zapisuje go
    /// <see cref="CloudConfigCache"/>). Android dostanie Keystore we własnym projekcie (faza 2.2).</summary>
    public static ILocalProtector ForThisPlatform() =>
        OperatingSystem.IsWindows() ? new DpapiProtector() : new OwnerOnlyFileProtector();
}

/// <summary>DPAPI z zakresem bieżącego użytkownika i własną entropią – odczytać może tylko to samo konto na tym komputerze
/// (usługa działa jako zalogowany użytkownik, więc to samo konto co przy zapisie).</summary>
public sealed class DpapiProtector : ILocalProtector
{
    private static readonly byte[] Entropy = "Glebosaurus.LocalSecrets.v1"u8.ToArray();

    public string Description => "DPAPI (bieżący użytkownik)";

    public byte[] Protect(byte[] data) => OperatingSystem.IsWindows()
        ? ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser)
        : throw new PlatformNotSupportedException("DPAPI działa tylko na Windows.");

    public byte[] Unprotect(byte[] data)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI działa tylko na Windows.");
        try { return ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser); }
        catch (CryptographicException e) { throw new InvalidDataException("DPAPI nie odszyfrowało danych (inne konto albo komputer?).", e); }
    }
}

/// <summary>Bez szyfrowania – ochroną są prawa pliku 600 (tylko właściciel) nadawane przy zapisie.</summary>
public sealed class OwnerOnlyFileProtector : ILocalProtector
{
    public string Description => "plik tylko dla właściciela (600)";
    public byte[] Protect(byte[] data) => data;
    public byte[] Unprotect(byte[] data) => data;
}
