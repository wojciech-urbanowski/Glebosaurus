using Glebosaurus.Core.Config;
using Glebosaurus.Core.Domain;
using Glebosaurus.Core.Mail;
using Glebosaurus.Core.Monitoring;
using Glebosaurus.Data;
using Microsoft.Extensions.Logging;

namespace Glebosaurus.Tests;

public class MonitorEngineTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private sealed class Harness : IDisposable
    {
        private readonly TempStore db = new();
        public AgentConfig Config { get; }
        public FakeSensorReader Reader { get; } = new();
        public FakeMail Mail { get; } = new();
        public FakeLogArchive Logs { get; } = new();
        public CapturingLogger Log { get; } = new();
        public ManualTimeProvider Clock { get; }
        public MonitorEngine Engine { get; }
        public SqliteMonitorStore Store => db.Store;

        public Harness(AgentConfig? config = null, DateTimeOffset? start = null)
        {
            Config = config ?? Fixtures.Config();
            Clock = new ManualTimeProvider(start ?? Fixtures.T0);
            Engine = Engines.Create(Config, db.Store, Reader, Mail, Clock, Logs, Log);
        }

        public async Task CycleAfter(TimeSpan delay)
        {
            Clock.Advance(delay);
            await Engine.CycleAsync(Ct);
        }

        public void Dispose() => db.Dispose();
    }

    [Fact]
    public async Task DueSensorIsReadAndStoredThenLeftAloneUntilItsIntervalPasses()
    {
        using var h = new Harness();

        await h.Engine.CycleAsync(Ct);
        Assert.Single(h.Reader.Calls);
        var stored = await h.Store.GetLatestAsync("s1", Ct);
        Assert.NotNull(stored);
        Assert.Equal(50, stored.MoisturePercent);
        Assert.Equal(Fixtures.T0, stored.At);

        await h.CycleAfter(TimeSpan.FromMinutes(5));
        Assert.Single(h.Reader.Calls); // za wcześnie

        await h.CycleAfter(TimeSpan.FromMinutes(11)); // 16 min od odczytu
        Assert.Equal(2, h.Reader.Calls.Count);
    }

    [Fact]
    public async Task EarlierReadingsFromTheSensorAreStoredWithTheirOwnTimesAndNewestAtReceiveTime()
    {
        using var h = new Harness();
        h.Reader.Script = sensor => Fixtures.Ok(sensor, 50, 80, 22.5m,
            Fixtures.Soil(Fixtures.T0 - TimeSpan.FromHours(2), 40), Fixtures.Soil(Fixtures.T0 - TimeSpan.FromHours(1), 30));

        await h.Engine.CycleAsync(Ct);

        var stored = await h.Store.GetRecentAsync("s1", 10, Ct);
        Assert.Equal([Fixtures.T0, Fixtures.T0 - TimeSpan.FromHours(1), Fixtures.T0 - TimeSpan.FromHours(2)], stored.Select(r => r.At));
        Assert.Equal([50, 30, 40], stored.Select(r => r.MoisturePercent));
        Assert.Equal(50, (await h.Store.GetLatestAsync("s1", Ct))!.MoisturePercent); // „stan teraz” to najnowszy, nie najstarszy rekord
    }

    [Fact]
    public async Task EarlierReadingFromTheFutureMeansWrongSensorClockAndIsSkipped()
    {
        using var h = new Harness();
        h.Reader.Script = sensor => Fixtures.Ok(sensor, 50, 80, 22.5m,
            Fixtures.Soil(Fixtures.T0 - TimeSpan.FromHours(1), 40), Fixtures.Soil(Fixtures.T0 + TimeSpan.FromHours(3), 99));

        await h.Engine.CycleAsync(Ct);

        var stored = await h.Store.GetRecentAsync("s1", 10, Ct);
        Assert.Equal([50, 40], stored.Select(r => r.MoisturePercent));
    }

    [Fact]
    public async Task DryBacklogFollowedByWetReadingDoesNotOpenAWateringAlert()
    {
        // Regresja z 2026-11-13: monitor zapisał najstarszy rekord historii (sucho: 18%) jako „teraz”, choć czujnik już siedział
        // w ziemi (79%). Stare suche pomiary + mokry najnowszy nie mogą dawać alertu „czas podlać”.
        using var h = new Harness();
        h.Reader.Script = sensor => Fixtures.Ok(sensor, 79, 80, 16.0m,
            Fixtures.Soil(Fixtures.T0 - TimeSpan.FromHours(3), 19), Fixtures.Soil(Fixtures.T0 - TimeSpan.FromHours(2), 18));

        await h.Engine.CycleAsync(Ct);

        Assert.Empty(await h.Store.GetOpenAlertsAsync(Ct));
    }

    [Fact]
    public async Task DryBacklogCountsTowardsConsecutiveDryReadings()
    {
        using var h = new Harness();
        h.Reader.Script = sensor => Fixtures.Ok(sensor, 12, 80, 22.5m, Fixtures.Soil(Fixtures.T0 - TimeSpan.FromHours(1), 15));

        await h.Engine.CycleAsync(Ct); // dwa suche pomiary z rzędu (próg 25%) już w pierwszym połączeniu

        Assert.Contains(await h.Store.GetOpenAlertsAsync(Ct), a => a.Kind == AlertKind.NeedsWater);
    }

    [Fact]
    public async Task SensorsWithTheirOwnIntervalAreReadIndependently()
    {
        var fast = Fixtures.Sensor("fast", "Szybki", "1111111111111111");
        fast.ReadIntervalMinutes = 1;
        var slow = Fixtures.Sensor("slow", "Wolny", "2222222222222222");
        using var h = new Harness(Fixtures.Config(fast, slow));

        await h.Engine.CycleAsync(Ct);
        Assert.Equal(["fast", "slow"], h.Reader.Calls[0].Order());

        await h.CycleAfter(TimeSpan.FromMinutes(2));
        Assert.Equal(["fast"], h.Reader.Calls[1]);
    }

    [Fact]
    public async Task NotFoundSensorIsRetriedWithGrowingDelay()
    {
        using var h = new Harness();
        h.Reader.Script = sensor => new(sensor, null, SensorReadFailure.NotFound);

        await h.Engine.CycleAsync(Ct);          // próba 1, następna za 1 min
        await h.Engine.CycleAsync(Ct);
        Assert.Single(h.Reader.Calls);

        await h.CycleAfter(TimeSpan.FromSeconds(61));   // próba 2, następna za 2 min
        Assert.Equal(2, h.Reader.Calls.Count);

        await h.CycleAfter(TimeSpan.FromSeconds(61));
        Assert.Equal(2, h.Reader.Calls.Count);

        await h.CycleAfter(TimeSpan.FromMinutes(1));    // minęły 2 min od próby 2
        Assert.Equal(3, h.Reader.Calls.Count);
        Assert.Null(await h.Store.GetLatestAsync("s1", Ct)); // nic nie zapisano
    }

    private static bool Logged(Harness h, LogLevel level, string fragment) =>
        h.Log.Entries.Any(e => e.Level == level && e.Message.Contains(fragment, StringComparison.Ordinal));

    [Fact]
    public async Task SuccessfulReadingLogsTheSignalStrength()
    {
        using var h = new Harness();
        h.Reader.Script = sensor => Fixtures.Ok(sensor, 50) with { SignalDbm = -81 };

        await h.Engine.CycleAsync(Ct);

        Assert.True(Logged(h, LogLevel.Information, "Monstera: wilgotność 50%"));
        Assert.True(Logged(h, LogLevel.Information, "sygnał -81 dBm."));
    }

    [Fact]
    public async Task UnknownSignalIsLoggedAsUnknownNotAsZero()
    {
        using var h = new Harness();

        await h.Engine.CycleAsync(Ct); // domyślna atrapa nie zna sygnału

        Assert.True(Logged(h, LogLevel.Information, "sygnał nieznany."));
    }

    [Fact]
    public async Task FailedReadAfterConnectingLogsTheSignalItHadBeforeFailing()
    {
        using var h = new Harness();
        h.Reader.Script = sensor => new(sensor, null, SensorReadFailure.Error, "Failed to connect: Unreachable") { SignalDbm = -94 };

        await h.Engine.CycleAsync(Ct);

        Assert.True(Logged(h, LogLevel.Warning, "Failed to connect: Unreachable (sygnał -94 dBm)"));
    }

    [Fact]
    public async Task NotFoundWarningSaysHowStrongAndHowLongAgoTheSensorWasLastHeard()
    {
        using var h = new Harness();
        await h.Engine.CycleAsync(Ct);                  // pierwszy odczyt bez sygnału
        h.Reader.Script = sensor => new(sensor, null, SensorReadFailure.Error, "Unreachable") { SignalDbm = -90 };
        await h.CycleAfter(Interval);                   // słyszany, ale się nie połączył: -90 dBm
        h.Reader.Script = sensor => new(sensor, null, SensorReadFailure.NotFound);

        await h.CycleAfter(TimeSpan.FromMinutes(10));   // próba po 1 min wykładniczego odstępu; 10 min od ostatniego sygnału

        Assert.True(Logged(h, LogLevel.Warning, "Monstera: nie znaleziono w reklamach BLE w oknie 60 s"));
        Assert.True(Logged(h, LogLevel.Warning, "ostatnio słyszany: -90 dBm, 10 min temu."));
    }

    [Fact]
    public async Task NotFoundWarningForASensorNeverHeardSaysSo()
    {
        using var h = new Harness();
        h.Reader.Script = sensor => new(sensor, null, SensorReadFailure.NotFound);

        await h.Engine.CycleAsync(Ct);

        Assert.True(Logged(h, LogLevel.Warning, "od startu monitora jeszcze nie słyszany."));
    }

    [Fact]
    public async Task LongSilenceIsReportedInHoursAndTheLatestSignalReplacesTheOlderOne()
    {
        using var h = new Harness();
        h.Reader.Script = sensor => Fixtures.Ok(sensor, 50) with { SignalDbm = -70 };
        await h.Engine.CycleAsync(Ct);
        h.Reader.Script = sensor => new(sensor, null, SensorReadFailure.NotFound);

        await h.CycleAfter(TimeSpan.FromHours(3));
        Assert.True(Logged(h, LogLevel.Warning, "ostatnio słyszany: -70 dBm, 3 h temu."));

        // Czujnik znów usłyszany z innym poziomem; kolejna porażka powołuje się na ten nowszy, nie na stary (−70).
        h.Reader.Script = sensor => Fixtures.Ok(sensor, 50) with { SignalDbm = -75 };
        await h.CycleAfter(Interval);
        h.Reader.Script = sensor => new(sensor, null, SensorReadFailure.NotFound);
        await h.CycleAfter(Interval);
        Assert.True(Logged(h, LogLevel.Warning, "ostatnio słyszany: -75 dBm, 15 min temu."));
    }

    [Fact]
    public async Task ReadingWithoutMeasurementsIsNotStored()
    {
        using var h = new Harness();
        h.Reader.Script = sensor => Fixtures.Ok(sensor, moisture: null, temperature: null);

        await h.Engine.CycleAsync(Ct);

        Assert.Null(await h.Store.GetLatestAsync("s1", Ct));
    }

    [Fact]
    public async Task TwoLowReadingsOpenAlertAndSendOneMailThenReminderAfterADay()
    {
        using var h = new Harness();
        h.Reader.Script = sensor => Fixtures.Ok(sensor, 18);

        await h.Engine.CycleAsync(Ct);                  // 1. niski odczyt - jeszcze bez alertu
        Assert.Empty(h.Mail.Sent);

        await h.CycleAfter(Interval);                   // 2. niski odczyt - alert i mail
        var mail = Assert.Single(h.Mail.Sent);
        Assert.Contains("Monstera", mail.Subject);
        Assert.Contains("czas podlać", mail.Subject);
        Assert.Equal(["ktos@example.com"], mail.To);
        var alert = Assert.Single(await h.Store.GetOpenAlertsAsync(Ct));
        Assert.Equal(AlertKind.NeedsWater, alert.Kind);
        Assert.NotNull(alert.LastNotifiedAt);

        await h.CycleAfter(Interval);                   // trwa, ale nie przypominamy co 15 minut
        Assert.Single(h.Mail.Sent);

        await h.CycleAfter(TimeSpan.FromHours(24));     // minęła doba - przypomnienie
        Assert.Equal(2, h.Mail.Sent.Count);
        Assert.StartsWith("Przypomnienie: Monstera", h.Mail.Sent[1].Subject);
    }

    [Fact]
    public async Task AlertStaysOpenInsideHysteresisAndClosesWithoutMailWhenWatered()
    {
        using var h = new Harness();
        var moisture = 18;
        h.Reader.Script = sensor => Fixtures.Ok(sensor, moisture);

        await h.Engine.CycleAsync(Ct);
        await h.CycleAfter(Interval);
        Assert.Single(await h.Store.GetOpenAlertsAsync(Ct));

        moisture = 30;                                  // powyżej progu 25, ale poniżej 35
        await h.CycleAfter(Interval);
        Assert.Single(await h.Store.GetOpenAlertsAsync(Ct));

        moisture = 60;                                  // podlane
        await h.CycleAfter(Interval);
        Assert.Empty(await h.Store.GetOpenAlertsAsync(Ct));
        Assert.Single(h.Mail.Sent);                     // zamknięcie alertu nie wysyła maila
    }

    [Fact]
    public async Task SeveralNewAlertsAreCombinedIntoOneMail()
    {
        var a = Fixtures.Sensor("a", "Paproć", "1111111111111111");
        var b = Fixtures.Sensor("b", "Fikus", "2222222222222222");
        using var h = new Harness(Fixtures.Config(a, b));
        h.Reader.Script = sensor => Fixtures.Ok(sensor, 10);

        await h.Engine.CycleAsync(Ct);
        await h.CycleAfter(Interval);

        var mail = Assert.Single(h.Mail.Sent);
        Assert.Contains("Paproć", mail.TextBody);
        Assert.Contains("Fikus", mail.TextBody);
        Assert.Contains("2 alerty", mail.Subject);
    }

    [Fact]
    public async Task FailedMailIsRetriedLaterAndTheAlertIsNotMarkedNotifiedMeanwhile()
    {
        using var h = new Harness();
        h.Reader.Script = sensor => Fixtures.Ok(sensor, 18);
        h.Mail.Failure = new MailTransportException("brak sieci", new IOException());

        await h.Engine.CycleAsync(Ct);
        await h.CycleAfter(Interval);                   // alert, wysyłka się nie udaje
        Assert.Equal(1, h.Mail.Attempts);
        Assert.Null(Assert.Single(await h.Store.GetOpenAlertsAsync(Ct)).LastNotifiedAt);

        await h.CycleAfter(TimeSpan.FromMinutes(1));    // zbyt wcześnie na ponowną próbę
        Assert.Equal(1, h.Mail.Attempts);

        h.Mail.Failure = null;
        await h.CycleAfter(MonitorEngine.MailRetryDelay);
        Assert.Single(h.Mail.Sent);
        Assert.NotNull(Assert.Single(await h.Store.GetOpenAlertsAsync(Ct)).LastNotifiedAt);
    }

    [Fact]
    public async Task SensorThatNeverAnswersEventuallyRaisesSilentAlert()
    {
        using var h = new Harness();
        h.Reader.Script = sensor => new(sensor, null, SensorReadFailure.Error, "brak odpowiedzi");

        await h.Engine.CycleAsync(Ct);                  // start monitora = punkt odniesienia dla czujnika bez odczytów
        for (var hour = 1; hour <= 5; hour++) await h.CycleAfter(TimeSpan.FromHours(1));
        Assert.Empty(await h.Store.GetOpenAlertsAsync(Ct)); // 5 h < progu 6 h

        await h.CycleAfter(TimeSpan.FromHours(2));      // 7 h od startu

        var alert = Assert.Single(await h.Store.GetOpenAlertsAsync(Ct));
        Assert.Equal(AlertKind.SensorSilent, alert.Kind);
        Assert.Contains("brak odczytów", Assert.Single(h.Mail.Sent).Subject);
    }

    [Fact]
    public async Task UnconfiguredMailLeavesAlertsOpenAndUnnotified()
    {
        var config = Fixtures.Config();
        config.Mail = null;
        using var h = new Harness(config);
        h.Reader.Script = sensor => Fixtures.Ok(sensor, 18);

        await h.Engine.CycleAsync(Ct);
        await h.CycleAfter(Interval);

        Assert.Empty(h.Mail.Sent);
        Assert.Null(Assert.Single(await h.Store.GetOpenAlertsAsync(Ct)).LastNotifiedAt);
    }

    [Fact]
    public async Task ReportIsSentOncePerScheduledTime()
    {
        var config = Fixtures.Config();
        config.Reports.Times = ["07:00"];
        using var h = new Harness(config, start: new DateTimeOffset(2026, 11, 13, 6, 30, 0, TimeSpan.Zero));

        await h.Engine.CycleAsync(Ct);
        Assert.Empty(h.Mail.Sent);                      // jeszcze przed 07:00

        await h.CycleAfter(TimeSpan.FromMinutes(31));   // 07:01
        var report = Assert.Single(h.Mail.Sent);
        Assert.Equal("Poranny raport – wszystko w porządku", report.Subject);
        Assert.Contains("Monstera", report.TextBody);

        await h.CycleAfter(TimeSpan.FromMinutes(1));
        Assert.Single(h.Mail.Sent);                     // drugi raz tego samego dnia nie

        await h.CycleAfter(TimeSpan.FromHours(24));     // następny dzień 07:02
        Assert.Equal(2, h.Mail.Sent.Count);
    }

    [Fact]
    public async Task MorningAndEveningReportsAreSentSeparatelyAndNamedAfterTheirTime()
    {
        var config = Fixtures.Config();
        config.Reports.Times = ["07:00", "19:00"];
        using var h = new Harness(config, start: new DateTimeOffset(2026, 11, 13, 6, 55, 0, TimeSpan.Zero));

        await h.Engine.CycleAsync(Ct);
        await h.CycleAfter(TimeSpan.FromMinutes(10));   // 07:05
        await h.CycleAfter(TimeSpan.FromHours(6));      // 13:05 - żaden raport nie czeka
        await h.CycleAfter(TimeSpan.FromHours(6));      // 19:05

        Assert.Equal(2, h.Mail.Sent.Count);
        Assert.StartsWith("Poranny raport", h.Mail.Sent[0].Subject);
        Assert.StartsWith("Wieczorny raport", h.Mail.Sent[1].Subject);
    }

    [Fact]
    public async Task ReportCaughtUpLateIsStillNamedAfterItsScheduledTime()
    {
        var config = Fixtures.Config();
        config.Reports.Times = ["10:30"];
        using var h = new Harness(config, start: new DateTimeOffset(2026, 11, 13, 13, 0, 0, TimeSpan.Zero)); // 2,5 h po terminie

        await h.Engine.CycleAsync(Ct);

        Assert.StartsWith("Poranny raport", Assert.Single(h.Mail.Sent).Subject);
    }

    [Fact]
    public async Task AlertsAndReportsGoToTheirOwnRecipientLists()
    {
        var config = Fixtures.Config();
        config.Mail!.AlertRecipients = ["alerty@example.com"];
        config.Mail.ReportRecipients = ["raporty@example.com", "drugi@example.com"];
        config.Reports.Times = ["07:00"];
        using var h = new Harness(config, start: new DateTimeOffset(2026, 11, 13, 6, 50, 0, TimeSpan.Zero));
        h.Reader.Script = sensor => Fixtures.Ok(sensor, 18);

        await h.Engine.CycleAsync(Ct);                  // 06:50, 1. niski odczyt
        await h.CycleAfter(Interval);                   // 07:05 - alert i raport

        var alert = Assert.Single(h.Mail.Sent, m => m.Subject.Contains("czas podlać"));
        var report = Assert.Single(h.Mail.Sent, m => m.Subject.Contains("raport"));
        Assert.Equal(["alerty@example.com"], alert.To);
        Assert.Equal(["raporty@example.com", "drugi@example.com"], report.To);
    }

    [Fact]
    public async Task ReportsFallBackToAlertRecipientsWhenNoneAreGiven()
    {
        var config = Fixtures.Config();
        config.Mail!.AlertRecipients = ["alerty@example.com"];
        config.Reports.Times = ["07:00"];
        using var h = new Harness(config, start: new DateTimeOffset(2026, 11, 13, 7, 5, 0, TimeSpan.Zero));

        await h.Engine.CycleAsync(Ct);

        Assert.Equal(["alerty@example.com"], Assert.Single(h.Mail.Sent).To);
    }

    // ---- log techniczny dla administratora ------------------------------------------------------------------------

    private static readonly DateOnly Yesterday = new(2026, 11, 12);   // Fixtures.T0 to 2026-11-13 06:00 UTC

    private static Harness WithAdmin(params DateOnly[] daysWithLogs)
    {
        var config = Fixtures.Config();
        config.Mail!.AdminRecipients = ["admin@example.com"];
        var h = new Harness(config);
        foreach (var day in daysWithLogs)
            h.Logs.Days[day] = $"{day:yyyy-MM-dd} 10:00:00.000 [INF] Glebosaurus: wpis z {day:yyyy-MM-dd}\n";
        return h;
    }

    [Fact]
    public async Task FirstCycleOfTheDayMailsYesterdaysLogToTheAdminOnly()
    {
        using var h = WithAdmin(Yesterday);

        await h.Engine.CycleAsync(Ct);

        var mail = Assert.Single(h.Mail.Sent);
        Assert.Equal(["admin@example.com"], mail.To);                   // nie do odbiorców alertów i raportów
        Assert.Contains("log z 12.11", mail.Subject);
        var attachment = Assert.Single(mail.Attachments!);
        Assert.Contains("wpis z 2026-11-12", System.Text.Encoding.UTF8.GetString(attachment.Content));
        Assert.Equal([Yesterday], h.Logs.Reads);
    }

    [Fact]
    public async Task AdminLogIsSentOncePerDayAndAgainOnTheNextDay()
    {
        var nextDay = new DateOnly(2026, 11, 13);
        using var h = WithAdmin(Yesterday, nextDay);

        await h.Engine.CycleAsync(Ct);
        await h.CycleAfter(TimeSpan.FromHours(1));
        await h.CycleAfter(TimeSpan.FromHours(10));
        Assert.Single(h.Mail.Sent);                                      // ten sam dzień – drugi raz nie
        Assert.Equal([Yesterday], h.Logs.Reads);                          // i bez ponownego czytania pliku w każdym cyklu

        await h.CycleAfter(TimeSpan.FromHours(14));                      // 2026-11-14 07:00 – pierwszy przebieg następnego dnia
        Assert.Equal(2, h.Mail.Sent.Count);
        Assert.Contains("log z 13.11", h.Mail.Sent[1].Subject);
    }

    [Fact]
    public async Task RestartedAgentDoesNotResendTheLogThatWasAlreadySent()
    {
        using var h = WithAdmin(Yesterday);
        await h.Engine.CycleAsync(Ct);
        Assert.Single(h.Mail.Sent);

        var restarted = Engines.Create(h.Config, h.Store, h.Reader, h.Mail, h.Clock, h.Logs);
        h.Clock.Advance(TimeSpan.FromHours(3));
        await restarted.CycleAsync(Ct);

        Assert.Single(h.Mail.Sent);
    }

    [Fact]
    public async Task AgentStartedLaterInTheDayStillSendsYesterdaysLogOnce()
    {
        using var h = WithAdmin(Yesterday);
        h.Clock.Advance(TimeSpan.FromHours(11));                         // start dopiero o 17:00

        await h.Engine.CycleAsync(Ct);

        Assert.Single(h.Mail.Sent);
        Assert.Contains("log z 12.11", h.Mail.Sent[0].Subject);
    }

    [Fact]
    public async Task NoLogFromYesterdayMeansNothingToSend()
    {
        using var h = WithAdmin();                                       // agent wczoraj nie działał

        await h.Engine.CycleAsync(Ct);
        await h.CycleAfter(TimeSpan.FromMinutes(1));

        Assert.Empty(h.Mail.Sent);
        Assert.Equal([Yesterday], h.Logs.Reads);                          // sprawdzone raz, nie co cykl
    }

    [Fact]
    public async Task AdminLogIsNotSentWhenNoAdminAddressIsConfigured()
    {
        using var h = new Harness();
        h.Logs.Days[Yesterday] = "2026-11-12 10:00:00.000 [INF] Glebosaurus: wpis\n";

        await h.Engine.CycleAsync(Ct);

        Assert.Empty(h.Mail.Sent);
        Assert.Empty(h.Logs.Reads);
    }

    [Fact]
    public async Task FailedAdminLogIsRetriedAfterTheDelayAndStillSentOnce()
    {
        using var h = WithAdmin(Yesterday);
        h.Mail.Failure = new MailTransportException("brak sieci", new IOException());

        await h.Engine.CycleAsync(Ct);
        Assert.Equal(1, h.Mail.Attempts);

        await h.CycleAfter(TimeSpan.FromMinutes(1));                     // za wcześnie na ponowną próbę
        Assert.Equal(1, h.Mail.Attempts);

        h.Mail.Failure = null;
        await h.CycleAfter(MonitorEngine.MailRetryDelay);
        await h.CycleAfter(TimeSpan.FromMinutes(1));

        Assert.Single(h.Mail.Sent);
    }

    [Fact]
    public async Task ProblemWithTheAdminLogDoesNotDelayAlerts()
    {
        using var h = WithAdmin(Yesterday);
        h.Reader.Script = sensor => Fixtures.Ok(sensor, 18);
        h.Mail.FailWhen = message => message.Attachments is { Count: > 0 } ? new MailTransportException("za duży załącznik", new IOException()) : null;

        await h.Engine.CycleAsync(Ct);                                   // log nie wychodzi
        await h.CycleAfter(Interval);                                    // a alert „czas podlać” tak, od razu

        var alert = Assert.Single(h.Mail.Sent);
        Assert.Contains("czas podlać", alert.Subject);
        Assert.Equal(["ktos@example.com"], alert.To);
    }

    [Fact]
    public async Task MissedReportBeyondCatchUpIsSkipped()
    {
        var config = Fixtures.Config();
        config.Reports.Times = ["07:00"];
        using var h = new Harness(config, start: new DateTimeOffset(2026, 11, 13, 11, 0, 0, TimeSpan.Zero));

        await h.Engine.CycleAsync(Ct);

        Assert.Empty(h.Mail.Sent);
    }

    [Fact]
    public async Task FailedReportIsRetriedAndStillSentOnce()
    {
        var config = Fixtures.Config();
        config.Reports.Times = ["07:00"];
        using var h = new Harness(config, start: new DateTimeOffset(2026, 11, 13, 7, 5, 0, TimeSpan.Zero));
        h.Mail.Failure = new MailTransportException("brak sieci", new IOException());

        await h.Engine.CycleAsync(Ct);
        Assert.Equal(1, h.Mail.Attempts);

        h.Mail.Failure = null;
        await h.CycleAfter(MonitorEngine.MailRetryDelay);
        await h.CycleAfter(TimeSpan.FromMinutes(1));

        Assert.Single(h.Mail.Sent);
    }
}
