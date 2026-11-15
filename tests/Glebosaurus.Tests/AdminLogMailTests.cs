using System.Text;
using System.Text.RegularExpressions;
using Glebosaurus.Core.Mail;

namespace Glebosaurus.Tests;

public class AdminLogMailTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 0, 0, 30, TimeSpan.Zero);
    private static readonly DateOnly Day = new(2026, 11, 12);
    private static readonly string[] Admin = ["admin@example.com"];

    private const string TypicalLog =
        "2026-11-12 00:00:01.120 [INF] Glebosaurus: Monitor „bestia5” startuje.\n" +
        "2026-11-12 00:15:03.002 [INF] Glebosaurus: Odczyt czujników: Cisy.\n" +
        "2026-11-12 07:00:00.310 [WRN] Glebosaurus: Fikus: nie znaleziono w reklamach BLE.\n" +
        "2026-11-12 07:30:10.010 [ERR] Glebosaurus: Cykl monitora zakończył się błędem; kolejna próba za chwilę.\n" +
        "System.IO.IOException: Unable to read data from the transport connection.\r\n" +
        "   at MailKit.Net.Smtp.SmtpClient.SendAsync(FormatOptions options, MimeMessage message)\n" +
        "2026-11-12 12:00:00.100 [WRN] Glebosaurus: Cisy: błąd odczytu: Failed to connect: Unreachable\n";

    private static MailMessage Build(string log = TypicalLog, string monitor = "bestia5") =>
        MailTemplates.AdminLog(log, Day, Admin, monitor, Now, TimeZoneInfo.Utc);

    private static string Flat(string text) => Regex.Replace(text, @"\s+", " ");

    private static string Line(int minute, string level, string message) =>
        $"2026-11-12 10:{minute:D2}:00.000 [{level}] Glebosaurus: {message}\n";

    // ---- tytuł i streszczenie -------------------------------------------------------------------------------------

    [Theory]
    [InlineData("errors", "bestia5: log z 12.11 – błędy: 1, ostrzeżenia: 2")]
    [InlineData("warnings", "bestia5: log z 12.11 – ostrzeżenia: 1")]
    [InlineData("clean", "bestia5: log z 12.11 – bez uwag")]
    public void SubjectNamesTheAgentTheDayAndWhatWentWrong(string kind, string expected)
    {
        var log = kind switch
        {
            "errors" => TypicalLog,
            "warnings" => Line(1, "INF", "ok") + Line(2, "WRN", "uwaga"),
            _ => Line(1, "INF", "ok") + Line(2, "INF", "też ok")
        };

        Assert.Equal(expected, Build(log).Subject);
    }

    [Fact]
    public void CountsEntriesByLevelAndTreatsStackTraceLinesAsPartOfTheEntry()
    {
        var message = Build();

        var text = Flat(message.TextBody);
        Assert.Contains("Dzień 12.11.2026 · 5 wpisów", text);           // 5 wpisów; 2 wiersze śladu stosu nie są osobnymi wpisami
        Assert.Contains("Informacje: 2 Ostrzeżenia: 2 Błędy: 1", text);   // trzy krótkie wiersze jeden pod drugim
        Assert.StartsWith("Błędy: 1", Flat(message.TextBody).Split("13.11.2026 00:00 ")[1]);   // pierwsze zdanie: streszczenie dnia
    }

    [Fact]
    public void ListsWarningsAndErrorsWithTimeAndLevelButWithoutTheCategory()
    {
        var text = Flat(Build().TextBody);

        Assert.Contains("07:00 ostrzeżenie Fikus: nie znaleziono w reklamach BLE.", text);
        Assert.Contains("07:30 błąd Cykl monitora zakończył się błędem", text);
        Assert.DoesNotContain("Glebosaurus:", text);                    // nazwa kategorii dziennika to szum w mailu
        Assert.DoesNotContain("MailKit.Net.Smtp", text);                // ślad stosu jest tylko w załączniku
    }

    [Fact]
    public void LongListIsCutToTheLatestEntriesWithANoteAboutTheRest()
    {
        var log = string.Concat(Enumerable.Range(0, 15).Select(i => Line(i, "WRN", $"problem {i:D2}")));

        var message = Build(log);

        Assert.Contains("problem 14", message.TextBody);
        Assert.DoesNotContain("problem 02", message.TextBody);
        Assert.Contains("problem 03", message.TextBody);
        Assert.Contains("…i 3 wcześniejszych", Flat(message.TextBody));
        Assert.Contains("…i 3 wcześniejszych", message.HtmlBody);
    }

    [Fact]
    public void QuietDayAndEmptyLogAreSaidPlainly()
    {
        var quiet = Build(Line(1, "INF", "ok"));
        Assert.Contains("Bez błędów i ostrzeżeń", quiet.HtmlBody);
        Assert.Contains("W tym dniu nie było ostrzeżeń ani błędów.", Flat(quiet.TextBody));

        var empty = Build("");
        Assert.Contains("Dziennik jest pusty", empty.HtmlBody);
        Assert.EndsWith("bez uwag", empty.Subject);
    }

    // ---- załącznik ------------------------------------------------------------------------------------------------

    [Fact]
    public void WholeLogGoesAsATextAttachmentNamedAfterTheAgentAndDay()
    {
        var message = Build();

        var attachment = Assert.Single(message.Attachments!);
        Assert.Equal("glebosaurus-bestia5-20261112.log", attachment.FileName);
        Assert.StartsWith("text/plain", attachment.ContentType);
        Assert.Equal(TypicalLog, Encoding.UTF8.GetString(attachment.Content));   // cały dziennik, ze śladem stosu
        Assert.Contains("Pełny dziennik jest w załączniku", Flat(message.TextBody));
    }

    [Fact]
    public void AttachmentFileNameIsSafeEvenForAnAwkwardAgentName()
    {
        var message = Build(monitor: "Salon – Pi (1. piętro)");

        var name = Assert.Single(message.Attachments!).FileName;
        Assert.Matches("^[A-Za-z0-9_.-]+$", name);
        Assert.Equal("glebosaurus-Salon-Pi-1-pietro-20261112.log", name);   // polskie litery bez ogonków
        Assert.StartsWith("Salon – Pi (1. piętro): log z 12.11", message.Subject);   // w tytule nazwa zostaje pełna

        Assert.Equal("glebosaurus-agent-20261112.log", Assert.Single(Build(monitor: "–––").Attachments!).FileName);
    }

    [Fact]
    public void OversizedLogKeepsOnlyTheTailAndSaysSo()
    {
        var lines = Enumerable.Range(0, 50_100).Select(i => $"{i:D5}|" + new string('x', 93) + "\n");
        var log = string.Concat(lines);
        Assert.True(log.Length > MailTemplates.MaxLogAttachmentChars);

        var message = Build(log);

        var content = Encoding.UTF8.GetString(Assert.Single(message.Attachments!).Content);
        Assert.StartsWith("# obcięto początek dziennika", content);
        Assert.True(content.Length <= MailTemplates.MaxLogAttachmentChars + 100);
        Assert.EndsWith("50099|" + new string('x', 93) + "\n", content);
        Assert.DoesNotContain("00000|", content);
        Assert.Contains("końcówka", Flat(message.TextBody));
    }

    // ---- wygląd: telefon, kwadratowo, pastelowo, wersja ------------------------------------------------------------

    [Fact]
    public void TextHasShortLinesEvenForLongMessages()
    {
        var longMessage = "Błąd: " + string.Join(' ', Enumerable.Repeat("bardzo-długi-komunikat", 8)) + @" C:\Bardzo\Długa\Ścieżka\Do\Pliku\Który\Się\Nie\Mieści\Na\Wąskim\Ekranie.log";
        var message = Build(Line(1, "ERR", longMessage) + TypicalLog);

        foreach (var line in message.TextBody.Split('\n'))
            Assert.True(line.TrimEnd('\r').Length <= 40, $"Za długi wiersz ({line.Length} znaków): {line}");
    }

    [Fact]
    public void HtmlIsNarrowSquarePastelAndEscaped()
    {
        var message = Build(Line(1, "ERR", "<script>alert('x')</script> & spółka") + Line(2, "WRN", "ostrzeżenie"));
        var html = message.HtmlBody;

        Assert.Contains("name=\"viewport\"", html);
        Assert.Contains("max-width:480px", html);
        Assert.Contains("overflow-wrap:anywhere", html);                 // długie komunikaty zawijają się, nie wypychają ekranu
        Assert.DoesNotContain("border-radius", html);
        Assert.DoesNotContain("color:#fff", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;alert(&#39;x&#39;)&lt;/script&gt; &amp; spółka", html);
        Assert.Contains("background:#f6d2cc", html);                     // pastelowy pasek „błędy”
    }

    [Fact]
    public void EveryMailShowsTheGlebosaurusVersionInTheFooter()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+", Glebosaurus.Core.AppInfo.Version);
        var version = Glebosaurus.Core.AppInfo.Version;

        var messages = MailSamples.Build(Admin, "Salon", Now, TimeZoneInfo.Utc).Append(MailSamples.AdminLog(Admin, "Salon", Now, TimeZoneInfo.Utc)).Append(Build());

        Assert.Equal(5, messages.Count());
        foreach (var message in messages)
        {
            Assert.Contains($"Glebosaurus {version}", message.HtmlBody);
            Assert.EndsWith($"Glebosaurus {version}", message.TextBody);
        }
    }

    [Fact]
    public void SampleAdminLogIsMarkedAsAnExampleAndHasAnAttachment()
    {
        var sample = MailSamples.AdminLog(Admin, "Salon", Now, TimeZoneInfo.Utc);

        Assert.StartsWith(MailSamples.SubjectPrefix, sample.Subject);
        Assert.Contains("Salon (przykład)", sample.Subject);
        Assert.Contains("log z 12.11", sample.Subject);                  // wczorajszy dzień względem „teraz”
        Assert.Single(sample.Attachments!);
        Assert.Contains("błędy: 1", sample.Subject);
    }
}
