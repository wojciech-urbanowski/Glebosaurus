using System.Security.Cryptography;

namespace Glebosaurus.Core.Updates;

/// <summary>
/// Plik <c>SHA256SUMS</c> wydania w formacie <c>sha256sum</c>: w każdym wierszu 64 znaki szesnastkowe, spacja, spacja albo
/// gwiazdka, nazwa pliku. Chroni przed uszkodzonym lub uciętym pobraniem (nie przed przejęciem konta na GitHubie).
/// </summary>
public static class Sha256Sums
{
    /// <summary>Nazwa pliku → skrót (małe litery).</summary>
    /// <exception cref="InvalidDataException">Wiersz nie pasuje do formatu albo ta sama nazwa występuje dwa razy.</exception>
    public static IReadOnlyDictionary<string, string> Parse(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Trim().Length == 0) continue;
            if (line.Length < 67 || !IsHex(line.AsSpan(0, 64)) || line[64] != ' ' || line[65] is not (' ' or '*'))
                throw new InvalidDataException($"SHA256SUMS: nieczytelny wiersz „{line}”.");
            var name = line[66..];
            if (!result.TryAdd(name, line[..64].ToLowerInvariant()))
                throw new InvalidDataException($"SHA256SUMS: plik {name} występuje więcej niż raz.");
        }
        return result;
    }

    public static string Compute(Stream content) => Convert.ToHexStringLower(SHA256.HashData(content));

    public static string Compute(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    /// <exception cref="InvalidDataException">Brak pliku na liście albo skrót się nie zgadza.</exception>
    public static void Verify(IReadOnlyDictionary<string, string> sums, string fileName, string actualHash)
    {
        if (!sums.TryGetValue(fileName, out var expected))
            throw new InvalidDataException($"SHA256SUMS nie zawiera pliku {fileName}.");
        if (!string.Equals(expected, actualHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Skrót SHA-256 pliku {fileName} się nie zgadza (oczekiwano {expected}, jest {actualHash.ToLowerInvariant()}).");
    }

    private static bool IsHex(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
            if (!char.IsAsciiHexDigit(c)) return false;
        return true;
    }
}
