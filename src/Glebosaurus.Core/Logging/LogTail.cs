using System.Text.RegularExpressions;

namespace Glebosaurus.Core.Logging;

/// <summary>Jeden wpis dziennika do podglądu: godzina, poziom (INF/WRN/ERR…), kategoria i treść razem z ciągiem dalszym (np. śladem stosu).</summary>
public sealed record LogLine(string Time, string Level, string Category, string Message);

/// <summary>
/// Końcówka pliku dziennika agenta do podglądu w panelu WWW. Wiersz pliku ma postać
/// <c>2026-11-13 14:15:08.818 [INF] Kategoria: treść</c>; wiersze, które tak nie wyglądają (ślad stosu wyjątku), dokleja do
/// poprzedniego wpisu.
/// </summary>
public static partial class LogTail
{
    /// <summary>Najdłuższa treść jednego wpisu (ślad stosu bywa bardzo długi).</summary>
    public const int MaxMessageChars = 4000;

    /// <param name="warningsOnly">Tylko ostrzeżenia i błędy (WRN, ERR, CRT).</param>
    /// <param name="contains">Tylko wpisy, których treść zawiera ten tekst (np. nazwę czujnika); null = wszystkie.</param>
    /// <returns>Do <paramref name="count"/> ostatnich wpisów, od najstarszego.</returns>
    public static IReadOnlyList<LogLine> Parse(string text, int count, bool warningsOnly, string? contains = null)
    {
        var entries = new List<LogLine>();
        string? time = null, level = null, category = null;
        var message = new System.Text.StringBuilder();

        void Flush()
        {
            if (time is null) return;
            var body = message.ToString().TrimEnd();
            if ((!warningsOnly || level is "WRN" or "ERR" or "CRT") && (contains is null || body.Contains(contains, StringComparison.Ordinal)))
            {
                if (body.Length > MaxMessageChars) body = body[..(MaxMessageChars - 1)] + "…";
                entries.Add(new(time, level!, category!, body));
            }
            time = null;
            message.Clear();
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var match = EntryStart().Match(line);
            if (match.Success)
            {
                Flush();
                time = match.Groups["time"].Value;
                level = match.Groups["level"].Value;
                category = match.Groups["category"].Value;
                message.Append(match.Groups["message"].Value);
            }
            else if (time is not null && line.Length > 0 && message.Length <= MaxMessageChars)
            {
                message.Append('\n').Append(line);
            }
        }
        Flush();
        return entries.Count > count ? entries[^count..] : entries;
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2} (?<time>\d{2}:\d{2}:\d{2})\.\d{3} \[(?<level>[A-Z]{3})\] (?:(?<category>[\w.+]+): )?(?<message>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex EntryStart();
}
