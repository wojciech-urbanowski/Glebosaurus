using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Glebosaurus.Web;

/// <summary>
/// Hasło do panelu. Na dysku leży tylko skrót PBKDF2-SHA256 (600 tys. iteracji, losowa sól) w postaci
/// <c>pbkdf2-sha256$iteracje$sól$skrót</c> (Base64), w osobnym pliku obok konfiguracji – tworzy go <c>glebosaurus web-password</c>.
/// </summary>
public static class PanelPassword
{
    public const int MinLength = 12;
    public const int Iterations = 600_000;

    private const string Scheme = "pbkdf2-sha256";
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    /// <summary>Górna granica iteracji przy weryfikacji – uszkodzony plik nie może zamienić logowania w kilkuminutowe liczenie.</summary>
    private const int MaxIterations = 10_000_000;

    /// <returns>Opis problemu z hasłem albo null, gdy hasło się nadaje.</returns>
    public static string? Problem(string password) =>
        password.Length < MinLength ? $"Hasło musi mieć co najmniej {MinLength} znaków."
        : string.IsNullOrWhiteSpace(password) ? "Hasło nie może składać się z samych spacji."
        : null;

    public static string Hash(string password) => Hash(password, Iterations);

    /// <summary>Z inną liczbą iteracji – tylko dla testów (weryfikacja bierze liczbę iteracji z zapisu).</summary>
    internal static string Hash(string password, int iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        return string.Create(CultureInfo.InvariantCulture, $"{Scheme}${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}");
    }

    /// <summary>Sprawdza hasło ze skrótem w stałym czasie. Uszkodzony albo obcy zapis skrótu = odmowa (nigdy wyjątek).</summary>
    public static bool Verify(string password, string stored)
    {
        var parts = stored.Trim().Split('$');
        if (parts.Length != 4 || parts[0] != Scheme) return false;
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations) || iterations is < 1 or > MaxIterations) return false;
        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException) { return false; }
        if (salt.Length == 0 || expected.Length == 0) return false;

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <returns>Skrót z pliku albo null, gdy pliku nie ma (hasło nieustawione).</returns>
    public static string? Read(string path) => File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8).Trim() : null;

    public static void Write(string path, string hash) => PrivateFile.Write(path, Encoding.UTF8.GetBytes(hash + "\n"));
}
