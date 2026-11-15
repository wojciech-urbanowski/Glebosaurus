using System.Globalization;

namespace Glebosaurus.Core.Mail;

/// <summary>Formatowanie wartości i zwrotów po polsku, wspólne dla wersji tekstowej i HTML maili.</summary>
internal static class MailText
{
    public static string Percent(int? value) => value is { } v ? $"{v}%" : "–";

    public static string Temperature(decimal? value) =>
        value is { } v ? v.ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',') + " °C" : "–";

    /// <summary>„3 min temu” – przy braku odczytu „brak”.</summary>
    public static string Age(DateTimeOffset now, DateTimeOffset at)
    {
        var span = now - at;
        if (span < TimeSpan.FromMinutes(1)) return "przed chwilą";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} min temu";
        if (span < TimeSpan.FromHours(48)) return $"{(int)span.TotalHours} h temu";
        return $"{(int)span.TotalDays} dni temu";
    }

    public static string Local(DateTimeOffset moment, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(moment, zone).ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);

    public static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>„Monstera, Fikus +2” – krótka lista nazw do tytułu maila, który na telefonie jest obcinany.</summary>
    public static string NameList(IReadOnlyList<string> names, int max = 2) =>
        names.Count <= max
            ? string.Join(", ", names)
            : string.Join(", ", names.Take(max)) + $" +{names.Count - max}";

    /// <summary>Odmiana dla liczb od 2 w górę: 2–4 „few”, reszta „many” (z wyjątkiem 12–14).</summary>
    public static string Plural(int count, string one, string few, string many) =>
        count == 1 ? one : count % 10 is >= 2 and <= 4 && count % 100 is not (>= 12 and <= 14) ? few : many;

    /// <summary>Stopka wersji HTML: kto wysłał i w jakiej wersji.</summary>
    public static string FooterLine => $"Wiadomość automatyczna · Glebosaurus {AppInfo.Version}";

    /// <summary>Ostatni wiersz wersji tekstowej (krótki, mieści się na wąskim ekranie).</summary>
    public static string SignatureLine => $"Glebosaurus {AppInfo.Version}";

    /// <summary>
    /// Zawija tekst do <paramref name="width"/> znaków (łącznie z wcięciem), żeby wersja tekstowa nie rozjeżdżała się na
    /// wąskim ekranie. Zbyt długie słowa (ścieżki, komunikaty) są łamane w miejscu.
    /// </summary>
    public static IEnumerable<string> Wrap(string text, int width, string indent = "")
    {
        var room = Math.Max(8, width - indent.Length);
        var line = new System.Text.StringBuilder();
        foreach (var rawWord in text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var word = rawWord;
            while (word.Length > room)
            {
                if (line.Length > 0) { yield return indent + line; line.Clear(); }
                yield return indent + word[..room];
                word = word[room..];
            }
            if (line.Length > 0 && line.Length + 1 + word.Length > room)
            {
                yield return indent + line;
                line.Clear();
            }
            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }
        if (line.Length > 0) yield return indent + line;
    }

    /// <summary>Pora dnia do tytułu raportu, np. „Poranny” dla 07:00 i „Wieczorny” dla 19:00.</summary>
    public static string DayPart(DateTimeOffset moment, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(moment, zone).Hour switch
    {
        < 4 => "Nocny",
        < 11 => "Poranny",
        < 14 => "Południowy",
        < 18 => "Popołudniowy",
        _ => "Wieczorny"
    };
}
