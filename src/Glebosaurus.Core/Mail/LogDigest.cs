using System.Text.RegularExpressions;

namespace Glebosaurus.Core.Mail;

/// <summary>Ostrzeżenie lub błąd z dziennika: godzina (HH:mm), poziom (WRN/ERR/CRT) i pierwszy wiersz treści bez nazwy kategorii.</summary>
internal sealed record LogEntry(string Time, string Level, string Message);

/// <summary>
/// Streszczenie dziennika z jednego dnia: liczba wpisów według poziomu i lista ostrzeżeń oraz błędów. Format wiersza
/// to ten z pliku dziennika agenta: <c>2026-11-13 14:15:08.818 [INF] Kategoria: treść</c>; wiersze, które tak nie
/// wyglądają (ślad stosu wyjątku), są ciągiem dalszym poprzedniego wpisu i nie liczą się osobno.
/// </summary>
internal sealed record LogDigest(int Entries, int Info, int Warnings, int Errors, IReadOnlyList<LogEntry> Notable)
{
    private const int MaxMessageChars = 220;

    private static readonly Regex EntryStart = new(
        @"^\d{4}-\d{2}-\d{2} (\d{2}:\d{2}):\d{2}\.\d{3} \[([A-Z]{3})\] (?:[\w.+]+: )?(.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static LogDigest Parse(string text)
    {
        int entries = 0, info = 0, warnings = 0, errors = 0;
        var notable = new List<LogEntry>();
        foreach (var raw in text.Split('\n'))
        {
            var match = EntryStart.Match(raw.TrimEnd('\r'));
            if (!match.Success) continue;

            entries++;
            var level = match.Groups[2].Value;
            switch (level)
            {
                case "WRN": warnings++; break;
                case "ERR" or "CRT": errors++; break;
                default: info++; break;
            }
            if (level is "WRN" or "ERR" or "CRT")
            {
                var message = match.Groups[3].Value.Trim();
                if (message.Length > MaxMessageChars) message = message[..(MaxMessageChars - 1)] + "…";
                notable.Add(new(match.Groups[1].Value, level, message));
            }
        }
        return new(entries, info, warnings, errors, notable);
    }
}
