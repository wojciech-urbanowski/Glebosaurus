using System.Text.RegularExpressions;
using Glebosaurus.Core.Config;
using Glebosaurus.Core.Domain;
using Glebosaurus.Core.Mail;
using Glebosaurus.Mail;
using MimeKit;

namespace Glebosaurus.Tests;

public class MailTemplatesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 7, 30, 0, TimeSpan.Zero);
    private static readonly string[] To = ["ktos@example.com"];

    private static SensorSnapshot Snapshot(string name, int? moisture, params AlertKind[] alerts)
    {
        var sensor = Fixtures.Sensor(name.ToLowerInvariant(), name, Guid.NewGuid().ToString("N")[..16]);
        return new(sensor, moisture is null ? null : Fixtures.Reading(sensor.Id, Now.AddMinutes(-3), moisture: moisture, temperature: 23.7m, battery: 33), alerts);
    }

    private static AlertNotice Notice(string name, int moisture = 18, bool reminder = false, AlertKind kind = AlertKind.NeedsWater)
    {
        var snapshot = Snapshot(name, moisture, kind);
        var alert = new AlertRecord(1, snapshot.Sensor.Id, kind, $"wilgotność gleby {moisture}% (próg 25%)", Now.AddHours(-1), null, null);
        return new(snapshot.Sensor, alert, snapshot.Latest, reminder);
    }

    private static MailMessage Report(params SensorSnapshot[] snapshots) =>
        MailTemplates.Report(snapshots, To, "Salon", Now, TimeZoneInfo.Utc);

    // ---- raport ---------------------------------------------------------------------------------------------------

    [Fact]
    public void ReportListsProblemsFirstAndFormatsValuesInPolish()
    {
        var message = Report(Snapshot("Zielistka", 60), Snapshot("Monstera", 18, AlertKind.NeedsWater), Snapshot("Fikus", 50, AlertKind.LowBattery));

        Assert.Equal("Poranny raport – do podlania: Monstera", message.Subject);
        Assert.True(message.TextBody.IndexOf("Monstera –", StringComparison.Ordinal) < message.TextBody.IndexOf("Fikus –", StringComparison.Ordinal));
        Assert.True(message.TextBody.IndexOf("Fikus –", StringComparison.Ordinal) < message.TextBody.IndexOf("Zielistka –", StringComparison.Ordinal));
        Assert.Contains("podlej", message.TextBody);
        Assert.Contains("słaba bateria", message.TextBody);
        Assert.Contains("23,7 °C", message.TextBody);
        Assert.Contains("3 min temu", message.TextBody);
        Assert.Contains("13.11.2026 07:30", message.TextBody);
    }

    [Theory]
    [InlineData(false, false, "wszystko w porządku")]
    [InlineData(false, true, "uwagi: 1")]
    [InlineData(true, false, "do podlania: Monstera")]
    public void ReportSubjectSummarisesTheState(bool thirsty, bool batteryOnly, string expected)
    {
        var snapshot = thirsty ? Snapshot("Monstera", 18, AlertKind.NeedsWater)
            : batteryOnly ? Snapshot("Monstera", 50, AlertKind.LowBattery)
            : Snapshot("Monstera", 50);

        Assert.EndsWith(expected, Report(snapshot).Subject);
    }

    [Fact]
    public void ReportSubjectShortensLongListsOfThirstyPlants()
    {
        var message = Report(Snapshot("A", 10, AlertKind.NeedsWater), Snapshot("B", 10, AlertKind.NeedsWater),
            Snapshot("C", 10, AlertKind.NeedsWater), Snapshot("D", 10, AlertKind.NeedsWater));

        Assert.Equal("Poranny raport – do podlania: A, B +2", message.Subject);
    }

    [Theory]
    [InlineData(2, "Nocny")]
    [InlineData(7, "Poranny")]
    [InlineData(12, "Południowy")]
    [InlineData(15, "Popołudniowy")]
    [InlineData(19, "Wieczorny")]
    [InlineData(23, "Wieczorny")]
    public void ReportTitleDependsOnTheScheduledTimeOfDay(int hour, string expected)
    {
        var at = new DateTimeOffset(2026, 11, 13, hour, 0, 0, TimeSpan.Zero);

        var message = MailTemplates.Report([Snapshot("Monstera", 50)], To, "Salon", at, TimeZoneInfo.Utc);

        Assert.StartsWith($"{expected} raport", message.Subject);
    }

    [Fact]
    public void LateReportKeepsTheNameOfItsScheduledTime()
    {
        var scheduled = new DateTimeOffset(2026, 11, 13, 10, 30, 0, TimeSpan.Zero);
        var sentLater = scheduled.AddHours(2.5);

        var message = MailTemplates.Report([Snapshot("Monstera", 50)], To, "Salon", sentLater, TimeZoneInfo.Utc, scheduled);

        Assert.StartsWith("Poranny raport", message.Subject);
    }

    [Fact]
    public void SensorWithoutAnyReadingIsShownAsMissingNotAsZero()
    {
        var message = Report(Snapshot("Nowy", null));

        Assert.Contains("odczyt: brak", message.TextBody);
        Assert.DoesNotContain("0%", message.TextBody);
        Assert.Contains("brak danych", message.TextBody);      // a nie zielone „OK”
        Assert.Contains("Brak odczytu", message.HtmlBody);
        Assert.DoesNotContain("<td width=", message.HtmlBody); // bez odczytu nie ma kafelków z liczbami
    }

    [Fact]
    public void HtmlEscapesSensorNamesMonitorNamesAndDetails()
    {
        var hostile = Snapshot("<b>Hack & Co</b>", 40);
        var message = MailTemplates.Report([hostile], To, "<i>Salon</i>", Now, TimeZoneInfo.Utc);

        Assert.Contains("&lt;b&gt;Hack &amp; Co&lt;/b&gt;", message.HtmlBody);
        Assert.DoesNotContain("<b>Hack", message.HtmlBody);
        Assert.DoesNotContain("<i>Salon", message.HtmlBody);

        var alert = new AlertRecord(1, hostile.Sensor.Id, AlertKind.NeedsWater, "<script>x</script>", Now, null, null);
        var html = MailTemplates.Alert([new(hostile.Sensor, alert, hostile.Latest, false)], To, "Salon", Now, TimeZoneInfo.Utc).HtmlBody;
        Assert.DoesNotContain("<script>", html);
    }

    // ---- wygląd na telefonie --------------------------------------------------------------------------------------

    [Fact]
    public void HtmlIsOneNarrowColumnWithOneCardAndThreeTilesPerSensor()
    {
        var message = Report(Snapshot("Zielistka", 60), Snapshot("Monstera", 18, AlertKind.NeedsWater), Snapshot("Fikus", 50, AlertKind.LowBattery));
        var html = message.HtmlBody;

        Assert.Contains("name=\"viewport\"", html);
        Assert.Contains("max-width:480px", html);
        Assert.DoesNotContain("<th", html);                    // żadnej tabeli z nagłówkami kolumn
        Assert.DoesNotContain("min-width", html);              // nic, co wymusiłoby poziome przewijanie
        Assert.DoesNotMatch(@"(?<![-\w])width:\s*\d{3,}px", html); // żadnej sztywnej szerokości w pikselach (max-width jest dozwolone)
        Assert.Equal(3 * 3, Regex.Count(html, "<td width=\"")); // 3 czujniki × 3 liczby
        Assert.Equal(3, Regex.Count(html, "background:#fff;border:1px solid")); // jedna karta na czujnik
    }

    [Fact]
    public void HtmlShowsMoistureBarColouredByThresholds()
    {
        var html = Report(Snapshot("Sucha", 18), Snapshot("Mokra", 60)).HtmlBody;

        Assert.Contains("width:18%;height:10px;font-size:0;line-height:0;background:#e59a8c", html); // poniżej progu 25% – pastelowa czerwień
        Assert.Contains("width:60%;height:10px;font-size:0;line-height:0;background:#94cf9f", html); // powyżej 35% – pastelowa zieleń
    }

    [Fact]
    public void ElementsAreSquareAndPastelNotRoundedAndSaturated()
    {
        var report = Report(Snapshot("Sucha", 18, AlertKind.NeedsWater), Snapshot("Bateria", 50, AlertKind.LowBattery), Snapshot("Mokra", 60)).HtmlBody;
        var alert = MailTemplates.Alert([Notice("Monstera"), Notice("Fikus", 50, reminder: true, kind: AlertKind.LowBattery)], To, "Salon", Now, TimeZoneInfo.Utc).HtmlBody;
        var sample = MailSamples.Build(To, "Salon", Now, TimeZoneInfo.Utc).Select(m => m.HtmlBody);

        foreach (var html in new[] { report, alert }.Concat(sample))
        {
            Assert.DoesNotContain("border-radius", html);    // kwadratowe rogi: karty, plakietki, paski
            Assert.DoesNotContain("color:#fff", html);       // biały tekst oznaczałby nasycone tło
        }
        Assert.Contains("background:#f6d2cc", report);       // pastelowy pasek „do podlania”
        Assert.Contains("background:#f8e6bd", report);       // pastelowa plakietka „słaba bateria”
        Assert.Contains("background:#d3ebd7", report);       // pastelowa plakietka „OK”
    }

    [Fact]
    public void PreheaderPutsMoistureOfEveryPlantInTheInboxPreview()
    {
        var message = Report(Snapshot("Zielistka", 60), Snapshot("Monstera", 18, AlertKind.NeedsWater));

        var preheader = Regex.Match(message.HtmlBody, "display:none[^>]*>([^<]*)<").Groups[1].Value;

        Assert.StartsWith("Monstera 18% · Zielistka 60%", preheader);
        Assert.True(message.HtmlBody.IndexOf("display:none", StringComparison.Ordinal) < message.HtmlBody.IndexOf("GLEBOSAURUS", StringComparison.Ordinal));
    }

    [Fact]
    public void PlainTextHasShortLinesThatFitANarrowScreen()
    {
        var report = Report(Snapshot("Zielistka", 60), Snapshot("Monstera", 18, AlertKind.NeedsWater), Snapshot("Fikus", 50, AlertKind.LowBattery));
        var alert = MailTemplates.Alert([Notice("Monstera"), Notice("Fikus", 12)], To, "Salon", Now, TimeZoneInfo.Utc);

        foreach (var line in report.TextBody.Split('\n').Concat(alert.TextBody.Split('\n')))
            Assert.True(line.TrimEnd('\r').Length <= 40, $"Za długi wiersz ({line.Length} znaków): {line}");
    }

    [Fact]
    public void SubjectsAreShortEnoughForALockScreenNotification()
    {
        var subjects = new[]
        {
            Report(Snapshot("Monstera w salonie", 18, AlertKind.NeedsWater), Snapshot("Fikus", 50)).Subject,
            MailTemplates.Alert([Notice("Monstera w salonie")], To, "Salon", Now, TimeZoneInfo.Utc).Subject,
            MailTemplates.Alert([Notice("Monstera", reminder: true)], To, "Salon", Now, TimeZoneInfo.Utc).Subject
        };

        foreach (var subject in subjects)
        {
            Assert.True(subject.Length <= 50, $"Za długi tytuł ({subject.Length} znaków): {subject}");
            Assert.DoesNotContain("Glebosaurus", subject); // nazwa nadawcy i tak jest nad tytułem
        }
    }

    // ---- alerty ---------------------------------------------------------------------------------------------------

    [Fact]
    public void SingleAlertSubjectNamesSensorAndProblem()
    {
        var message = MailTemplates.Alert([Notice("Monstera")], To, "Salon", Now, TimeZoneInfo.Utc);
        var reminder = MailTemplates.Alert([Notice("Monstera", reminder: true)], To, "Salon", Now, TimeZoneInfo.Utc);

        Assert.Equal("Monstera – czas podlać", message.Subject);
        Assert.Contains("wilgotność gleby 18% (próg 25%)", message.TextBody);
        Assert.Contains("wilgotność gleby 18% (próg 25%)", message.HtmlBody);
        Assert.Equal("Przypomnienie: Monstera – czas podlać", reminder.Subject);
        Assert.Contains("przypomnienie", reminder.HtmlBody);
    }

    [Theory]
    [InlineData(2, "2 alerty: A, B")]
    [InlineData(4, "4 alerty: A, B +2")]
    [InlineData(5, "5 alertów: A, B +3")]
    public void SeveralAlertsAreCountedAndNamedInTheSubject(int count, string expected)
    {
        var notices = Enumerable.Range(0, count).Select(i => Notice(((char)('A' + i)).ToString())).ToList();

        Assert.Equal(expected, MailTemplates.Alert(notices, To, "Salon", Now, TimeZoneInfo.Utc).Subject);
    }

    [Fact]
    public void AlertCardShowsTheLatestReadingNextToTheProblem()
    {
        var message = MailTemplates.Alert([Notice("Fikus", 50, kind: AlertKind.LowBattery)], To, "Salon", Now, TimeZoneInfo.Utc);

        Assert.Equal("Fikus – słaba bateria czujnika", message.Subject);
        Assert.Contains("bateria 33%, odczyt 3 min temu", message.TextBody);
        Assert.Contains("23,7 °C", message.HtmlBody);
    }

    [Fact]
    public void AlertWithoutAnyNoticeIsAProgrammingError() =>
        Assert.Throws<ArgumentException>(() => MailTemplates.Alert([], To, "Salon", Now, TimeZoneInfo.Utc));

    // ---- wiadomość próbna i przykłady -----------------------------------------------------------------------------

    [Fact]
    public void GmailMessageHasBothBodiesAndAllRecipients()
    {
        var message = MailTemplates.Test(["a@example.com", "b@example.com"], "Salon", Now, TimeZoneInfo.Utc);

        var mime = GmailSender.BuildMessage("nadawca@gmail.com", "Glebosaurus", message);

        Assert.Equal(["a@example.com", "b@example.com"], mime.To.Mailboxes.Select(m => m.Address));
        Assert.Equal("nadawca@gmail.com", mime.From.Mailboxes.Single().Address);
        Assert.False(string.IsNullOrWhiteSpace(mime.TextBody));
        Assert.False(string.IsNullOrWhiteSpace(mime.HtmlBody));
        Assert.Equal("Wiadomość próbna", mime.Subject);
    }

    [Fact]
    public void HtmlSurvivesMimeRoundTripWithPolishCharacters()
    {
        var message = MailTemplates.Report([Snapshot("Paproć żółwia", 40)], ["a@example.com"], "Łódź", Now, TimeZoneInfo.Utc);

        using var stream = new MemoryStream();
        var ct = TestContext.Current.CancellationToken;
        GmailSender.BuildMessage("nadawca@gmail.com", "Glebosaurus", message).WriteTo(stream, ct);
        stream.Position = 0;
        var parsed = MimeMessage.Load(stream, ct);

        Assert.Contains("Paproć żółwia", parsed.HtmlBody);
        Assert.Contains("Łódź", parsed.TextBody);
    }

    [Fact]
    public void AttachmentSurvivesMimeRoundTripWithItsNameAndPolishText()
    {
        var log = "2026-11-12 10:00:00.000 [WRN] Glebosaurus: Paproć żółwia: zażółć gęślą jaźń\n";
        var message = MailTemplates.AdminLog(log, new DateOnly(2026, 11, 12), ["admin@example.com"], "Łódź", Now, TimeZoneInfo.Utc);

        using var stream = new MemoryStream();
        var ct = TestContext.Current.CancellationToken;
        GmailSender.BuildMessage("nadawca@gmail.com", "Glebosaurus", message).WriteTo(stream, ct);
        stream.Position = 0;
        var parsed = MimeMessage.Load(stream, ct);

        var part = Assert.IsAssignableFrom<MimePart>(Assert.Single(parsed.Attachments));   // MimeKit zwraca tekst jako TextPart
        Assert.Equal("glebosaurus-Lodz-20261112.log", part.FileName);   // „Łódź” → „Lodz”, bezpieczna nazwa pliku
        using var content = new MemoryStream();
        Assert.NotNull(part.Content);
        part.Content.DecodeTo(content, ct);
        Assert.Equal(log, System.Text.Encoding.UTF8.GetString(content.ToArray()));
        Assert.Contains("Paproć żółwia", parsed.HtmlBody);                // treść maila (HTML i tekst) działa obok załącznika
        Assert.Contains("log z 12.11", parsed.Subject);
    }

    [Fact]
    public void SampleMessagesAreClearlyMarkedAndCoverEveryState()
    {
        var samples = MailSamples.Build(To, "Salon", Now, TimeZoneInfo.Utc);

        Assert.Equal(3, samples.Count);
        Assert.All(samples, m => Assert.StartsWith(MailSamples.SubjectPrefix, m.Subject));
        Assert.All(samples, m => Assert.Contains("Salon (przykład)", m.TextBody));
        var report = samples[2].HtmlBody;
        foreach (var label in new[] { "podlej", "słaba bateria", "brak odczytów", ">OK<" })
            Assert.Contains(label, report);
    }
}
