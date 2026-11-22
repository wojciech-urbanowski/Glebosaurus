using System.Text.RegularExpressions;
using Glebosaurus.Core.Cloud;
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

    private static MailMessage Report(SensorSnapshot[] snapshots, params DeviceSnapshot[] devices) =>
        MailTemplates.Report(snapshots, To, "Salon", Now, TimeZoneInfo.Utc, devices: devices);

    private static DeviceSnapshot Phone(string name, int? battery = 64, double? temperature = 18.5, bool? charging = false, bool silent = false) =>
        new(name, new AgentStatus("0.2.5", "android", "ok") { BatteryPercent = battery, BatteryTemperatureCelsius = temperature, Charging = charging, Network = "wifi" },
            silent ? Now.AddHours(-3) : Now.AddMinutes(-1), silent);

    private static DeviceSnapshot Pi(string name, double? cpu = 47.2) =>
        new(name, new AgentStatus("0.2.6", "linux", "wyłączony") { CpuTemperatureCelsius = cpu }, Now.AddSeconds(-20), false);

    private static string Lf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

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

    [Fact]
    public void Report_ShowsMutes_FromOwnerPage_WithoutChangingTheCardColour()
    {
        var muted = Snapshot("Fikus", 50);
        muted.Sensor.AlertsMutedUntil = Now.AddDays(2);
        var past = Snapshot("Zielistka", 60);
        past.Sensor.AlertsMutedUntil = Now.AddMinutes(-1);           // wyciszenie już minęło – bez etykiety

        var message = MailTemplates.Report([muted, past], To, "Salon", Now, TimeZoneInfo.Utc, alertsMutedUntil: Now.AddDays(5));

        Assert.Contains("Alerty mailowe wyciszone do 18.11.2026 07:30 – raporty przychodzą dalej.", message.TextBody);
        Assert.Contains("Fikus – OK, alerty wyciszone do 15.11 07:30", message.TextBody);
        Assert.Contains("Zielistka – OK\n", Lf(message.TextBody));
        Assert.Contains("alerty wyciszone do 15.11 07:30", message.HtmlBody);
        Assert.Equal("Poranny raport – wszystko w porządku", message.Subject);

        var quiet = MailTemplates.Report([muted], To, "Salon", Now, TimeZoneInfo.Utc, alertsMutedUntil: Now.AddMinutes(-5));
        Assert.DoesNotContain("Alerty mailowe wyciszone", quiet.TextBody);
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
        Assert.DoesNotContain("padding-right:6px", message.HtmlBody); // bez odczytu karta nie ma liczb
        Assert.Contains(">Now<", message.HtmlBody);                     // kafelek jest – z kreskami i etykietą
        Assert.Contains(">brak danych</span>", message.HtmlBody);
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

    // ---- urządzenia (telefon, Raspberry Pi) ------------------------------------------------------------------------

    [Fact]
    public void Report_DevicesFollowSensors_WithBatteryAndTemperature()
    {
        var message = Report([Snapshot("Monstera", 50)], Pi("rasp3"), Phone("mi11"));
        var text = Lf(message.TextBody);

        Assert.Equal("Poranny raport – wszystko w porządku", message.Subject);
        Assert.Contains("Monstera – OK", text);
        Assert.True(text.IndexOf("Monstera – OK", StringComparison.Ordinal) < text.IndexOf("\nUrządzenia\n", StringComparison.Ordinal));
        Assert.Contains("mi11 (telefon) – OK\n  bateria 64%, bez ładowarki\n  temp. akumulatora 18,5 °C\n  kontakt 1 min temu\n", text);
        Assert.Contains("rasp3 (Raspberry Pi) – OK\n  temp. procesora 47,2 °C\n  bez baterii (zasilacz)\n  kontakt przed chwilą\n", text);
        Assert.True(text.IndexOf("mi11 (", StringComparison.Ordinal) < text.IndexOf("rasp3 (", StringComparison.Ordinal));   // alfabetycznie

        var html = message.HtmlBody;
        Assert.True(html.IndexOf("Monstera", StringComparison.Ordinal) < html.IndexOf(">Urządzenia<", StringComparison.Ordinal));
        foreach (var part in new[] { ">Bateria<", ">64%<", ">Temperatura<", ">18,5 °C<", ">Ładowarka<", ">nie<", ">Temp. procesora<", ">47,2 °C<",
                     ">zasilacz<", "Telefon · Glebosaurus 0.2.5 · kontakt 1 min temu", "Raspberry Pi · Glebosaurus 0.2.6 · kontakt przed chwilą" })
            Assert.Contains(part, html);
        Assert.Equal(1 + 2, Regex.Count(html, "border-left:5px solid"));   // karta czujnika i dwie karty urządzeń (urządzenia bez kafelków)
    }

    [Fact]
    public void Report_DeviceProblemsCountAsRemarks_SilentFirst()
    {
        var message = Report([Snapshot("Monstera", 50)], Pi("rasp3"), Phone("mi11", battery: 22, temperature: -2), Phone("stary", silent: true));
        var text = Lf(message.TextBody);

        Assert.Equal("Poranny raport – uwagi: 2", message.Subject);
        Assert.Contains("Uwagi: 2\nstary, mi11\n", text);
        Assert.True(text.IndexOf("stary (", StringComparison.Ordinal) < text.IndexOf("mi11 (", StringComparison.Ordinal));
        Assert.True(text.IndexOf("mi11 (", StringComparison.Ordinal) < text.IndexOf("rasp3 (", StringComparison.Ordinal));
        Assert.Contains("stary (telefon) – milczy\n", text);
        Assert.Contains("  ostatni kontakt 3 h temu\n  (wartości z ostatniego kontaktu)\n", text);
        Assert.Contains("mi11 (telefon) – uwaga\n", text);
        Assert.Contains("  Akumulator zmarznięty (-2,0 °C) – w\n", text);
        Assert.Contains("  Słaba bateria (22 %) – podłącz telefon\n", text);

        var html = message.HtmlBody;
        Assert.Contains(">Nie łączy się z chmurą – wartości z ostatniego kontaktu.<", html);
        Assert.Contains("color:#74500a\">22%<", html);       // słaba bateria bez ładowarki – żółty
        Assert.Contains("color:#74500a\">-2,0 °C<", html);   // mróz – żółty
        Assert.Contains("border-left:5px solid #bcbcb5", html); // milczy – szary akcent
        Assert.Contains("border-left:5px solid #efc66f", html); // uwaga – żółty akcent
    }

    [Fact]
    public void Report_ThirstyPlantsStillLeadTheSubject_WhenADeviceHasAProblem()
    {
        var message = Report([Snapshot("Monstera", 18, AlertKind.NeedsWater)], Phone("mi11", temperature: 47));

        Assert.Equal("Poranny raport – do podlania: Monstera", message.Subject);
        Assert.Contains("Akumulator gorący (47,0 °C)", message.HtmlBody);
    }

    [Fact]
    public void Report_PiWithoutTemperature_ShowsNoData_AndIsNoRemark()
    {
        var message = Report([Snapshot("Monstera", 50)], Pi("rasp3", cpu: null));

        Assert.EndsWith("wszystko w porządku", message.Subject);
        Assert.Contains("rasp3 (Raspberry Pi) – brak danych\n  temp. procesora –\n", Lf(message.TextBody));
    }

    [Fact]
    public void Report_WithoutPhonesOrPis_HasNoDevicesSection()
    {
        var message = Report([Snapshot("Monstera", 50)]);

        Assert.DoesNotContain("Urządzenia", message.TextBody);
        Assert.DoesNotContain("Urządzenia", message.HtmlBody);
    }

    [Fact]
    public void Report_DeviceNamesAreEscaped_AndTextLinesStayShort()
    {
        var message = Report([Snapshot("Monstera", 50)], Phone("<b>tel</b>", battery: 5, temperature: 50), Pi("pi", cpu: 81.5));

        Assert.DoesNotContain("<b>tel", message.HtmlBody);
        Assert.Contains("&lt;b&gt;tel&lt;/b&gt;", message.HtmlBody);
        foreach (var line in message.TextBody.Split('\n'))
            Assert.True(line.TrimEnd('\r').Length <= 40, $"Za długi wiersz ({line.Length} znaków): {line}");
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
        Assert.Equal(3 * 3, Regex.Count(html, "padding-right:6px")); // 3 czujniki × 3 liczby na kartach
        Assert.Equal(3, Regex.Count(html, "border-left:5px solid")); // jedna karta na czujnik
        Assert.Equal(3, Regex.Count(html, "border-left:4px solid")); // i jeden kafelek w zestawieniu
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
    public void AgentSilentMail_IsNarrowShortAndEscaped()
    {
        var message = MailTemplates.AgentSilent("android<b>", Now.AddMinutes(-47), ["admin@example.com"], "Glebosaurus", Now, TimeZoneInfo.Utc);

        Assert.Equal("Agent android<b> milczy", message.Subject);
        Assert.True(message.Subject.Length <= 50);
        foreach (var line in message.TextBody.Split('\n'))
            Assert.True(line.TrimEnd('\r').Length <= 40, $"Za długi wiersz ({line.Length} znaków): {line}");
        Assert.DoesNotContain("android<b>", message.HtmlBody);
        Assert.Contains("android&lt;b&gt;", message.HtmlBody);
    }

    [Fact]
    public void GmailMessage_KeepsGivenMessageId()
    {
        var message = MailTemplates.Test(To, "Salon", Now, TimeZoneInfo.Utc) with { MessageId = "abc123@glebosaurus" };

        Assert.Equal("abc123@glebosaurus", GmailSender.BuildMessage("nadawca@gmail.com", "Glebosaurus", message).MessageId);
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
        foreach (var label in new[] { "podlej", "słaba bateria", "brak odczytów", ">OK<", ">Urządzenia<", ">milczy<", ">uwaga<", ">Temp. procesora<" })
            Assert.Contains(label, report);
    }

    // ---- zestawienie kafelkami (2026-11-22) -------------------------------------------------------------------------

    /// <summary>Kafelki: od pierwszego kafelka (akcent 4 px) do pierwszej karty czujnika (akcent 5 px).</summary>
    private static string TilesSection(string html)
    {
        var start = html.IndexOf("border-left:4px solid", StringComparison.Ordinal);
        var end = html.IndexOf("border-left:5px solid", StringComparison.Ordinal);
        Assert.True(start > 0 && end > start, "kafelki stoją przed kartami czujników");
        return html[start..end];
    }

    [Fact]
    public void Report_TilesComeRightAfterTheBanner_DriestFirst_WithTheCardsLabels()
    {
        var html = Report(Snapshot("Zielistka", 60), Snapshot("Monstera", 18, AlertKind.NeedsWater), Snapshot("Fikus", 50, AlertKind.LowBattery)).HtmlBody;
        var tiles = TilesSection(html);

        Assert.True(html.IndexOf("Do podlania: 1", StringComparison.Ordinal) < html.IndexOf("border-left:4px solid", StringComparison.Ordinal));
        Assert.True(tiles.IndexOf(">Mon<", StringComparison.Ordinal) < tiles.IndexOf(">Fik<", StringComparison.Ordinal));  // 18% → 50% → 60%
        Assert.True(tiles.IndexOf(">Fik<", StringComparison.Ordinal) < tiles.IndexOf(">Zie<", StringComparison.Ordinal));
        Assert.Contains("color:#8c2f20\">18%<", tiles);                         // wilgotność poniżej progu – czerwona
        Assert.Contains(">! podlej</span>", tiles);
        Assert.Contains(">! bateria</span>", tiles);
        Assert.Equal(2, Regex.Count(tiles, "</span></td></tr>"));             // etykiety tylko przy kłopotach („Zielistka” bez)…
        Assert.True(html.IndexOf(">Monstera<", StringComparison.Ordinal) < html.IndexOf(">Fikus<", StringComparison.Ordinal)); // …karty w kolejności ważności
        Assert.DoesNotContain("<svg", tiles);
    }

    [Fact]
    public void Report_TextVersionStartsWithTheTileSummary_ThreePerLine()
    {
        var text = Lf(Report(Snapshot("Zielistka", 60), Snapshot("Monstera", 18, AlertKind.NeedsWater), Snapshot("Fikus", 50), Snapshot("Bazylia", null)).TextBody);

        Assert.Contains("\nMon 18%  Fik 50%  Zie 60%\nBaz –\n", text);       // od najsuchszej, bez odczytu na końcu
        Assert.True(text.IndexOf("Baz –", StringComparison.Ordinal) < text.IndexOf("Monstera – podlej", StringComparison.Ordinal));
    }

    [Fact]
    public void Report_SilentSensorTileIsGrey_MutedIsMarked_FurtherLabelsAreCounted()
    {
        var silent = Snapshot("Trawa", 63, AlertKind.SensorSilent, AlertKind.LowBattery);
        var muted = Snapshot("Hortensje niskie", 98);
        muted.Sensor.AlertsMutedUntil = Now.AddDays(3);

        var tiles = TilesSection(Report(silent, muted).HtmlBody);

        Assert.Contains(">brak odczytów +1</span>", tiles);
        Assert.Contains("color:#6b6b66\">63%<", tiles);                         // stary odczyt – szara liczba
        Assert.Contains(">HNi<", tiles);
        Assert.Contains(">wyciszony</span>", tiles);
    }

    [Fact]
    public void Report_TileBatteryIsAmberWhenLow_DrawnWithoutSvg()
    {
        var low = Snapshot("Laurowiśnia szafka", 49);
        low = low with { Latest = low.Latest! with { BatteryPercent = 12 } };   // próg słabej baterii 18%

        var tiles = TilesSection(Report(low).HtmlBody);

        Assert.Contains(">LSz<", tiles);
        Assert.Contains("color:#74500a;font-weight:600\">", tiles);
        Assert.Contains("width:1px;height:5px;background:#74500a", tiles);      // 12% z 10 px
    }

    [Fact]
    public void Report_UsesTheGivenCodes_AndComputesThemWhenASensorIsMissing()
    {
        var trawnik = Snapshot("Trawnik", 50);

        var given = MailTemplates.Report([trawnik], To, "Salon", Now, TimeZoneInfo.Utc, sensorCodes: new Dictionary<string, string> { [trawnik.Sensor.Id] = "Trw" });
        var missing = MailTemplates.Report([trawnik], To, "Salon", Now, TimeZoneInfo.Utc, sensorCodes: new Dictionary<string, string> { ["inny"] = "Inn" });

        Assert.Contains(">Trw<", given.HtmlBody);
        Assert.Contains(">Tra<", missing.HtmlBody);
    }
}
