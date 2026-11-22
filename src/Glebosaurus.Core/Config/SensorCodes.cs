using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Glebosaurus.Core.Config;

/// <summary>
/// Krótkie kody czujników (kafelki pulpitu skróconego na stronie w chmurze i zestawienie w raporcie): trzy znaki z nazwy,
/// bez pola w bazie. Wielka litera = początek słowa nazwy (Laurowiśnia wejście → LWe, Zioła → Zio, Pomidor 2 → Po2). Kody
/// są różne bez względu na wielkość liter i polskie znaki; nowy czujnik albo zmiana nazwy przestawia kod tylko czujnikom o
/// tym samym kodzie podstawowym. To ten sam algorytm co <c>web/sensor-codes.js</c> – krok w krok, żeby „LWe” w mailu i na
/// stronie było tym samym czujnikiem (pilnuje <c>SensorCodesTests</c>: oba na tych samych nazwach). Kody liczy się ze
/// wszystkich czujników spoza archiwum, także wyłączonych – jak strona.
/// </summary>
public static partial class SensorCodes
{
    private const int Length = 3;
    private const int MaxSuffix = 2;      // liczba na końcu nazwy (Pomidor 2, Pomidor 12) zostaje na końcu kodu

    /// <summary>Kody wszystkich czujników → słownik id → kod.</summary>
    /// <remarks>
    /// Czujnik z niepowtarzalnym kodem podstawowym dostaje go zawsze. Przy powtórce kod podstawowy bierze czujnik o
    /// najmniejszym identyfikatorze (porządek porządkowy, jak porównanie napisów w JS), pozostali – pierwszy wolny kod
    /// zapasowy (też według identyfikatora).
    /// </remarks>
    public static IReadOnlyDictionary<string, string> For(IEnumerable<(string Id, string Name)> sensors)
    {
        var entries = sensors.Select(sensor =>
        {
            var parsed = Parse(sensor.Name);
            if (parsed.Chars.Count == 0) parsed = Parse(sensor.Id);       // nazwa bez liter i cyfr – kod z identyfikatora
            var primary = Display(parsed, parsed.Primary);
            return (sensor.Id, Parsed: parsed, Primary: primary, Key: Fold(primary));
        }).ToList();
        var counts = entries.GroupBy(e => e.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var codes = new Dictionary<string, string>(StringComparer.Ordinal);
        var taken = new HashSet<string>(StringComparer.Ordinal);
        void Assign(string id, string code)
        {
            codes[id] = code;
            taken.Add(Fold(code));
        }
        foreach (var entry in entries)
            if (counts[entry.Key] == 1) Assign(entry.Id, entry.Primary);
        var rest = entries.Where(e => !codes.ContainsKey(e.Id)).OrderBy(e => e.Id, StringComparer.Ordinal).ToList();
        foreach (var entry in rest)
            if (!taken.Contains(entry.Key)) Assign(entry.Id, entry.Primary);
        foreach (var entry in rest)
            if (!codes.ContainsKey(entry.Id)) Assign(entry.Id, FirstFree(entry.Parsed, taken));
        return codes;
    }

    private sealed record Parsed(List<(string Char, bool Initial)> Chars, List<Word> Words, List<int> Primary);

    private sealed record Word(int Start, int Length, bool Digits);

    /// <summary>Znaki nazwy (litery i cyfry, po jednym punkcie kodowym) z zaznaczonym początkiem słowa.</summary>
    private static Parsed Parse(string name)
    {
        var chars = new List<(string Char, bool Initial)>();
        var words = new List<Word>();
        foreach (Match word in WordPattern().Matches(name.Normalize(NormalizationForm.FormC)))
        {
            var start = chars.Count;
            foreach (var rune in word.Value.EnumerateRunes()) chars.Add((rune.ToString(), chars.Count == start));
            words.Add(new(start, chars.Count - start, DigitsPattern().IsMatch(word.Value)));
        }
        return new(chars, words, PrimaryPicks(chars.Count, words));
    }

    /// <summary>
    /// Kod podstawowy (pozycje znaków): jedno słowo – trzy pierwsze znaki; dwa słowa – pierwszy znak pierwszego i dwa
    /// pierwsze drugiego; więcej – inicjały. Liczba (1–2 cyfry) na końcu nazwy zostaje na końcu kodu. Krótkim słowom
    /// brakujące znaki dobiera najwcześniejsze nieużyte znaki nazwy.
    /// </summary>
    private static List<int> PrimaryPicks(int count, List<Word> words)
    {
        if (words.Count == 0) return [];
        var last = words[^1];
        var suffix = words.Count > 1 && last.Digits && last.Length <= MaxSuffix ? last : null;
        var bases = suffix is null ? words : words[..^1];
        var want = Length - (suffix?.Length ?? 0);
        List<int> picks = bases.Count switch
        {
            1 => [.. Range(bases[0].Start, Math.Min(want, bases[0].Length))],
            2 => [bases[0].Start, .. Range(bases[1].Start, Math.Min(want - 1, bases[1].Length))],
            _ => [.. bases.Take(want).Select(w => w.Start)]
        };
        var baseEnd = suffix?.Start ?? count;
        for (var i = 0; picks.Count < want && i < baseEnd; i++)
            if (!picks.Contains(i)) picks.Add(i);
        picks.Sort();
        if (suffix is not null) picks.AddRange(Range(suffix.Start, suffix.Length));
        return picks;
    }

    private static IEnumerable<int> Range(int start, int count) => Enumerable.Range(start, Math.Max(0, count));

    private static string Display(Parsed parsed, IEnumerable<int> picks) =>
        string.Concat(picks.Select(i => parsed.Chars[i]).Select(c => Case(c.Char, c.Initial)));

    /// <summary>Wielka litera na początku słowa, reszta małe; znak, którego wielka/mała postać ma inną długość (np. „ß”), zostaje bez zmian – tak samo w JS.</summary>
    private static string Case(string text, bool upper)
    {
        var changed = upper ? text.ToUpperInvariant() : text.ToLowerInvariant();
        return changed.Length == text.Length ? changed : text;
    }

    /// <summary>Postać do porównań: bez wielkości liter i znaków diakrytycznych (Ś = S, Ł = L).</summary>
    private static string Fold(string code)
    {
        var builder = new StringBuilder();
        foreach (var c in code.Normalize(NormalizationForm.FormD))
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark) continue;
            builder.Append(c is 'ł' or 'Ł' ? 'l' : c);
        }
        return builder.ToString().ToLowerInvariant();
    }

    /// <summary>Pierwszy wolny kod: podstawowy, potem pierwszy znak i dwa dalsze w kolejności, na końcu dwa pierwsze znaki i liczba.</summary>
    private static string FirstFree(Parsed parsed, HashSet<string> taken)
    {
        bool Free(string code) => !taken.Contains(Fold(code));
        var primary = Display(parsed, parsed.Primary);
        if (Free(primary)) return primary;
        var count = parsed.Chars.Count;
        for (var j = 1; j < count; j++)
            for (var k = j + 1; k < count; k++)
            {
                var code = Display(parsed, [0, j, k]);
                if (Free(code)) return code;
            }
        var head = Display(parsed, Range(0, Math.Min(2, count)));
        for (var n = 2; ; n++)
        {
            var code = head + n.ToString(CultureInfo.InvariantCulture);
            if (Free(code)) return code;
        }
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordPattern();

    [GeneratedRegex(@"^\p{N}+$")]
    private static partial Regex DigitsPattern();
}
