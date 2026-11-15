using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using static Glebosaurus.Core.Mail.MailLayout;
using static Glebosaurus.Core.Mail.MailText;

namespace Glebosaurus.Core.Mail;

public static partial class MailTemplates
{
    /// <summary>Największy załącznik z dziennikiem (w znakach); dłuższy jest obcinany od początku, żeby mail mieścił się w limicie Gmaila.</summary>
    public const int MaxLogAttachmentChars = 5_000_000;

    /// <summary>Ile ostatnich ostrzeżeń i błędów pokazać w treści maila (reszta jest w załączniku).</summary>
    private const int MaxListedEntries = 12;

    /// <summary>
    /// Mail dla administratora z dziennikiem agenta z całego dnia <paramref name="day"/>: w treści streszczenie (liczby
    /// wpisów, ostatnie ostrzeżenia i błędy, czytelne na telefonie), a pełny dziennik jako załącznik tekstowy.
    /// </summary>
    public static MailMessage AdminLog(string logText, DateOnly day, IReadOnlyList<string> to, string monitorName, DateTimeOffset now, TimeZoneInfo zone)
    {
        var digest = LogDigest.Parse(logText);
        var dayText = day.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
        var when = Local(now, zone);
        const string title = "Log techniczny";

        var summary = digest.Errors > 0
            ? $"błędy: {digest.Errors}" + (digest.Warnings > 0 ? $", ostrzeżenia: {digest.Warnings}" : "")
            : digest.Warnings > 0 ? $"ostrzeżenia: {digest.Warnings}" : "bez uwag";
        var subject = $"{monitorName}: log z {day.ToString("dd.MM", CultureInfo.InvariantCulture)} – {summary}";

        var (headline, tone) = digest.Entries == 0 ? ("Dziennik jest pusty", Grey)
            : digest.Errors > 0 ? ($"Błędy: {digest.Errors}", Red)
            : digest.Warnings > 0 ? ($"Ostrzeżenia: {digest.Warnings}", Amber)
            : ("Bez błędów i ostrzeżeń", Green);
        var detail = $"Dzień {dayText} · {digest.Entries} wpisów";

        var shown = digest.Notable.Skip(Math.Max(0, digest.Notable.Count - MaxListedEntries)).ToList();
        var hidden = digest.Notable.Count - shown.Count;
        var (attachmentText, truncated) = CapForAttachment(logText);

        var text = new StringBuilder();
        text.AppendLine(title);
        text.AppendLine($"{monitorName} · {when}");
        text.AppendLine();
        text.AppendLine(headline);
        text.AppendLine(detail);
        text.AppendLine();
        text.AppendLine($"Informacje: {digest.Info}");
        text.AppendLine($"Ostrzeżenia: {digest.Warnings}");
        text.AppendLine($"Błędy: {digest.Errors}");

        var rows = new StringBuilder(Header(title, monitorName, when));
        rows.Append(Banner(headline, detail, tone));
        rows.Append(Counters(("Informacje", digest.Info, null), ("Ostrzeżenia", digest.Warnings, Amber), ("Błędy", digest.Errors, Red)));

        text.AppendLine();
        if (shown.Count > 0)
        {
            text.AppendLine("Ostatnie ostrzeżenia i błędy:");
            rows.Append(Section("Ostatnie ostrzeżenia i błędy"));
            foreach (var entry in shown)
            {
                var level = LevelText(entry.Level);
                text.AppendLine($"{entry.Time} {level}");
                foreach (var line in Wrap(entry.Message, 38, "  ")) text.AppendLine(line);
                rows.Append(LogEntryRow(entry.Time, level, entry.Message, entry.Level == "WRN" ? Amber : Red));
            }
            if (hidden > 0)
            {
                var note = $"…i {hidden} wcześniejszych – wszystko jest w załączniku.";
                foreach (var line in Wrap(note, 38)) text.AppendLine(line);
                rows.Append(Note(note));
            }
        }
        else
        {
            const string quiet = "W tym dniu nie było ostrzeżeń ani błędów.";
            foreach (var line in Wrap(quiet, 38)) text.AppendLine(line);
            rows.Append(Note(quiet));
        }

        text.AppendLine();
        var attachmentNote = truncated
            ? "Dziennik był za duży – w załączniku jest jego końcówka."
            : "Pełny dziennik jest w załączniku.";
        foreach (var line in Wrap(attachmentNote, 38)) text.AppendLine(line);
        rows.Append(Note(attachmentNote));
        rows.Append(Footer());

        var preheader = $"{Capitalize(summary)} · {digest.Entries} wpisów" + (shown.Count > 0 ? $" · ostatni: {shown[^1].Message}" : "");
        var attachment = new MailAttachment($"glebosaurus-{FileSlug(monitorName)}-{day.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log", Encoding.UTF8.GetBytes(attachmentText));
        return new(to, subject, WithSignature(text), Page(subject, preheader, rows.ToString()), [attachment]);
    }

    private static string LevelText(string level) => level switch
    {
        "WRN" => "ostrzeżenie",
        "CRT" => "błąd krytyczny",
        _ => "błąd"
    };

    /// <summary>Przy dzienniku dłuższym niż <see cref="MaxLogAttachmentChars"/> zostawia końcówkę (od pełnego wiersza) z adnotacją na początku.</summary>
    private static (string Text, bool Truncated) CapForAttachment(string logText)
    {
        if (logText.Length <= MaxLogAttachmentChars) return (logText, false);
        var tail = logText[^MaxLogAttachmentChars..];
        var firstBreak = tail.IndexOf('\n', StringComparison.Ordinal);
        if (firstBreak >= 0) tail = tail[(firstBreak + 1)..];
        return ($"# obcięto początek dziennika (miał {logText.Length} znaków)\n{tail}", true);
    }

    /// <summary>
    /// Nazwa agenta bezpieczna w nazwie pliku załącznika: tylko litery ASCII, cyfry, „_” i „-”. Polskie litery są
    /// zamieniane na odpowiedniki bez ogonków („Łódź” → „Lodz”), a pozostałe znaki na „-”.
    /// </summary>
    private static string FileSlug(string name)
    {
        const string polish = "ąćęłńóśźżĄĆĘŁŃÓŚŹŻ";
        const string ascii = "acelnoszzACELNOSZZ";
        var chars = name
            .Select(c => polish.IndexOf(c, StringComparison.Ordinal) is var i and >= 0 ? ascii[i] : c)
            .Select(c => (c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_') ? c : '-')
            .ToArray();
        var slug = Regex.Replace(new string(chars), "-{2,}", "-").Trim('-');
        return slug.Length == 0 ? "agent" : slug;
    }
}
