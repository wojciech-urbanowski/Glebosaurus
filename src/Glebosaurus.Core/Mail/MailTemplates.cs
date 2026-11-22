using System.Globalization;
using System.Text;
using Glebosaurus.Core.Config;
using Glebosaurus.Core.Domain;
using static Glebosaurus.Core.Mail.MailLayout;
using static Glebosaurus.Core.Mail.MailText;

namespace Glebosaurus.Core.Mail;

/// <summary>
/// Treść maili po polsku, pisana z myślą o czytaniu na telefonie: tytuł zaczyna się od tego, co ważne (reszta jest
/// obcinana w powiadomieniu), HTML to jedna wąska kolumna z kartą na czujnik, a wersja tekstowa ma krótkie wiersze.
/// Czyste funkcje: te same dane dają ten sam tekst, więc łatwo je testować i podglądać.
/// </summary>
public static partial class MailTemplates
{
    public static MailMessage Alert(IReadOnlyList<AlertNotice> notices, IReadOnlyList<string> to, string monitorName, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (notices.Count == 0) throw new ArgumentException("Mail o alertach wymaga co najmniej jednego alertu.", nameof(notices));

        var single = notices.Count == 1 ? notices[0] : null;
        var names = notices.Select(n => n.Sensor.Name).Distinct().ToList();
        var subject = single is not null
            ? $"{(single.IsReminder ? "Przypomnienie: " : "")}{single.Sensor.Name} – {KindText(single.Alert.Kind)}"
            : $"{notices.Count} {Plural(notices.Count, "alert", "alerty", "alertów")}: {NameList(names)}";
        var title = single is not null
            ? Capitalize($"{(single.IsReminder ? "przypomnienie: " : "")}{KindText(single.Alert.Kind)}")
            : $"Alerty ({notices.Count})";
        var when = Local(now, zone);

        var text = new StringBuilder();
        text.AppendLine(title);
        text.AppendLine($"{monitorName} · {when}");
        var rows = new StringBuilder(Header(title, monitorName, when));
        foreach (var notice in notices)
        {
            text.AppendLine();
            text.AppendLine($"{notice.Sensor.Name} – {KindText(notice.Alert.Kind)}{(notice.IsReminder ? " (przypomnienie)" : "")}");
            text.AppendLine($"  {notice.Alert.Detail}");
            AppendReadingText(text, notice.Latest, now);

            var tone = AlertTone(notice.Alert.Kind);
            var badges = new List<(string Text, Tone Tone)> { (KindText(notice.Alert.Kind), tone) };
            if (notice.IsReminder) badges.Add(("przypomnienie", Grey));
            rows.Append(SensorCard(notice.Sensor, notice.Latest, AgeLine(notice.Latest, now), tone, badges, notice.Alert.Detail));
        }
        rows.Append(Footer());

        var preheader = single is not null
            ? $"{single.Sensor.Name}: {single.Alert.Detail}"
            : string.Join(" · ", notices.Select(n => $"{n.Sensor.Name}: {KindText(n.Alert.Kind)}"));
        return new(to, subject, WithSignature(text), Page(subject, preheader, rows.ToString()));
    }

    /// <param name="scheduledAt">Zaplanowana pora raportu; od niej zależy „poranny/wieczorny” (raport nadrabiany po godzinach jest wciąż tym samym raportem).</param>
    /// <param name="devices">Telefony i Raspberry Pi do sekcji „Urządzenia” pod czujnikami (<see cref="Cloud.AgentDevice.ForReport"/>);
    /// urządzenie, które milczy albo ma ostrzeżenie, liczy się do „Uwag” w pasku i tytule.</param>
    /// <param name="sensorCodes">Kody czujników na kafelkach (id → kod, <see cref="SensorCodes"/>) liczone ze wszystkich
    /// czujników w chmurze, jak na stronie; null albo brak któregoś czujnika z raportu – kody z samych czujników raportu.</param>
    /// <remarks>
    /// Pod paskiem podsumowania – zestawienie kafelkami (kod, wilgotność, bateria, temperatura, pierwsza etykieta), po
    /// trzy w rzędzie, od najsuchszej gleby (do podlania teraz albo wkrótce – na początku; bez odczytu na końcu; jak na
    /// pulpicie strony, <c>web/tile-order.js</c>); niżej karty w kolejności ważności.
    /// Czujniki z historią (<see cref="SensorSnapshot.History"/>) dostają wykres ostatnich 3 dób. Kolumny wykresu są tak
    /// wąskie, jak pozwala budżet wielkości maila (<see cref="MailChart.HtmlBudgetBytes"/>): przy wielu czujnikach przedziały
    /// rosną albo wykres się skraca do 2 dób i do ostatniej doby (<see cref="MailChart.Ladder"/>; kafelki też zabierają
    /// budżet), a gdy nawet najlżejszy się nie mieści, raport idzie bez wykresów (kafelki i karty zostają).
    /// </remarks>
    public static MailMessage Report(
        IReadOnlyList<SensorSnapshot> snapshots, IReadOnlyList<string> to, string monitorName, DateTimeOffset now, TimeZoneInfo zone,
        DateTimeOffset? scheduledAt = null, IReadOnlyList<DeviceSnapshot>? devices = null, DateTimeOffset? alertsMutedUntil = null,
        IReadOnlyDictionary<string, string>? sensorCodes = null)
    {
        devices ??= [];
        if (sensorCodes is null || snapshots.Any(s => !sensorCodes.ContainsKey(s.Sensor.Id)))
            sensorCodes = SensorCodes.For(snapshots.Select(s => (s.Sensor.Id, s.Sensor.Name)));
        if (snapshots.Any(s => s.History.Count > 0))
        {
            foreach (var step in MailChart.Ladder)
            {
                var message = BuildReport(snapshots, devices, to, monitorName, now, zone, scheduledAt, step, alertsMutedUntil, sensorCodes);
                if (Encoding.UTF8.GetByteCount(message.HtmlBody) <= MailChart.HtmlBudgetBytes) return message;
            }
        }
        return BuildReport(snapshots, devices, to, monitorName, now, zone, scheduledAt, null, alertsMutedUntil, sensorCodes);
    }

    private static MailMessage BuildReport(
        IReadOnlyList<SensorSnapshot> snapshots, IReadOnlyList<DeviceSnapshot> devices, IReadOnlyList<string> to, string monitorName,
        DateTimeOffset now, TimeZoneInfo zone, DateTimeOffset? scheduledAt, ChartStep? chartStep, DateTimeOffset? alertsMutedUntil,
        IReadOnlyDictionary<string, string> sensorCodes)
    {
        var ordered = snapshots.OrderBy(Priority).ThenBy(s => s.Sensor.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var orderedDevices = devices.OrderBy(DevicePriority).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var thirsty = ordered.Where(s => s.OpenAlerts.Contains(AlertKind.NeedsWater)).Select(s => s.Sensor.Name).ToList();
        var problems = ordered.Where(s => s.OpenAlerts.Count > 0).Select(s => s.Sensor.Name)
            .Concat(orderedDevices.Where(HasProblem).Select(d => d.Name)).ToList();
        var title = $"{DayPart(scheduledAt ?? now, zone)} raport";
        var when = Local(now, zone);

        var (headline, detail, tone, summary) = Summarize(snapshots.Count, thirsty, problems);
        var subject = $"{title} – {summary}";

        var text = new StringBuilder();
        text.AppendLine(title);
        text.AppendLine($"{monitorName} · {when}");
        text.AppendLine();
        text.AppendLine(headline);
        if (detail is not null) text.AppendLine(detail);
        var rows = new StringBuilder(Header(title, monitorName, when));
        rows.Append(Banner(headline, detail, tone));
        // Urlop (strona w chmurze): alerty mailowe czekają, raporty idą – raport mówi, że cisza w skrzynce nie znaczy spokoju.
        if (alertsMutedUntil > now)
        {
            var mutedNote = $"Alerty mailowe wyciszone do {Local(alertsMutedUntil.Value, zone)} – raporty przychodzą dalej.";
            text.AppendLine(mutedNote);
            rows.Append(Paragraph(mutedNote));
        }
        var tiles = snapshots.OrderBy(s => s.Latest?.MoisturePercent ?? int.MaxValue)
            .ThenBy(s => s.Sensor.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(s => Tile(s, sensorCodes[s.Sensor.Id], now, zone)).ToList();
        if (tiles.Count > 0)
        {
            rows.Append(SensorTiles(tiles));
            text.AppendLine();
            foreach (var line in tiles.Chunk(3))
                text.AppendLine(string.Join("  ", line.Select(t => $"{t.Code} {Percent(t.Reading?.MoisturePercent)}")));
        }
        var charts = new Dictionary<SensorSnapshot, string?>(ReferenceEqualityComparer.Instance);
        if (chartStep is { } step)
        {
            foreach (var s in ordered) charts[s] = s.History.Count > 0 ? MailChart.Html(s.Sensor, s.History, now, zone, step) : null;
            if (charts.Values.Any(c => c is not null))
                rows.Append(MailChart.Legend(step, ordered.Any(s => charts[s] is not null && MailChart.AnyDry(s.Sensor, s.History, now, zone, step))));
        }
        foreach (var snapshot in ordered)
        {
            var (labels, accent) = Status(snapshot, now, zone);
            text.AppendLine();
            text.AppendLine($"{snapshot.Sensor.Name} – {string.Join(", ", labels.Select(l => l.Text))}");
            AppendReadingText(text, snapshot.Latest, now);
            if (snapshot.History.Count > 0 && MailChart.TextLine(snapshot.History, now, zone) is { } spark) text.AppendLine(spark);
            rows.Append(SensorCard(snapshot.Sensor, snapshot.Latest, AgeLine(snapshot.Latest, now), accent, labels, null,
                charts.GetValueOrDefault(snapshot)));
        }
        AppendDevices(text, rows, orderedDevices, now);
        rows.Append(Footer());

        var preheader = string.Join(" · ", ordered.Select(s => s.Latest?.MoisturePercent is { } m ? $"{s.Sensor.Name} {m}%" : $"{s.Sensor.Name} –"));
        return new(to, subject, WithSignature(text), Page(subject, preheader, rows.ToString()));
    }

    public static MailMessage Test(IReadOnlyList<string> to, string monitorName, DateTimeOffset now, TimeZoneInfo zone)
    {
        var when = Local(now, zone);
        var body = $"To jest wiadomość próbna z monitora „{monitorName}”, wysłana {when}. Jeśli ją widzisz, poczta jest skonfigurowana poprawnie.";
        var rows = Header("Wiadomość próbna", monitorName, when) + Paragraph(body) + Footer();
        var text = new StringBuilder().AppendLine("Wiadomość próbna").AppendLine($"{monitorName} · {when}").AppendLine().Append(body);
        return new(to, "Wiadomość próbna", WithSignature(text), Page("Wiadomość próbna", body, rows));
    }

    /// <summary>Wersja tekstowa kończy się podpisem z wersją Glebosaurusa (odpowiednik stopki HTML).</summary>
    private static string WithSignature(StringBuilder text) =>
        text.ToString().TrimEnd() + Environment.NewLine + Environment.NewLine + SignatureLine;

    /// <summary>Jedno zdanie o stanie wszystkiego: pasek w mailu (nagłówek, opcjonalna lista nazw, ton) i skrót do tytułu.</summary>
    private static (string Headline, string? Detail, Tone Tone, string Summary) Summarize(
        int sensorCount, IReadOnlyList<string> thirsty, IReadOnlyList<string> problems)
    {
        if (thirsty.Count > 0)
            return ($"Do podlania: {thirsty.Count}", string.Join(", ", thirsty), Red, $"do podlania: {NameList(thirsty)}");
        if (problems.Count > 0)
            return ($"Uwagi: {problems.Count}", string.Join(", ", problems), Amber, $"uwagi: {problems.Count}");
        return sensorCount == 0
            ? ("Brak czujników", null, Grey, "brak czujników")
            : ("Wszystko w porządku", null, Green, "wszystko w porządku");
    }

    public static string KindText(AlertKind kind) => kind switch
    {
        AlertKind.NeedsWater => "czas podlać",
        AlertKind.LowBattery => "słaba bateria czujnika",
        AlertKind.SensorSilent => "brak odczytów",
        _ => kind.ToString()
    };

    private static Tone AlertTone(AlertKind kind) => kind switch
    {
        AlertKind.NeedsWater => Red,
        AlertKind.LowBattery => Amber,
        _ => Grey
    };

    /// <summary>Plakietki stanu czujnika i ton akcentu karty (najpoważniejszy z otwartych alertów, zielony gdy ich nie ma).</summary>
    private static (List<(string Text, Tone Tone)> Labels, Tone Accent) Status(SensorSnapshot snapshot, DateTimeOffset now, TimeZoneInfo zone)
    {
        var labels = new List<(string Text, Tone Tone)>();
        if (snapshot.OpenAlerts.Contains(AlertKind.NeedsWater)) labels.Add(("podlej", Red));
        if (snapshot.OpenAlerts.Contains(AlertKind.SensorSilent)) labels.Add(("brak odczytów", Grey));
        if (snapshot.OpenAlerts.Contains(AlertKind.LowBattery)) labels.Add(("słaba bateria", Amber));
        if (labels.Count == 0) labels.Add(snapshot.Latest is null ? ("brak danych", Grey) : ("OK", Green));
        // Wyciszony czujnik (strona w chmurze): ostatnia, szara etykieta – kolor karty zostaje według stanu.
        if (snapshot.Sensor.AlertsMutedUntil is { } muted && muted > now)
            labels.Add(($"alerty wyciszone do {TimeZoneInfo.ConvertTime(muted, zone).ToString("dd.MM HH:mm", CultureInfo.InvariantCulture)}", Grey));
        return (labels, labels[0].Tone);
    }

    /// <summary>
    /// Kafelek zestawienia: akcent jak karta czujnika, etykiety tymi samymi słowami co plakietki na karcie (krócej), przy
    /// kilku – pierwsza i „+N”; „brak odczytów” szarzy wartości (to stary odczyt).
    /// </summary>
    private static SensorTile Tile(SensorSnapshot snapshot, string code, DateTimeOffset now, TimeZoneInfo zone)
    {
        var labels = new List<(string Text, Tone Tone)>();
        if (snapshot.OpenAlerts.Contains(AlertKind.NeedsWater)) labels.Add(("! podlej", Red));
        if (snapshot.OpenAlerts.Contains(AlertKind.SensorSilent)) labels.Add(("brak odczytów", Grey));
        if (snapshot.OpenAlerts.Contains(AlertKind.LowBattery)) labels.Add(("! bateria", Amber));
        if (labels.Count == 0 && snapshot.Latest is null) labels.Add(("brak danych", Grey));
        if (snapshot.Sensor.AlertsMuted(now)) labels.Add(("wyciszony", Grey));
        (string, Tone)? label = labels.Count == 0 ? null
            : (labels.Count > 1 ? $"{labels[0].Text} +{labels.Count - 1}" : labels[0].Text, labels[0].Tone);
        return new(code, snapshot.Sensor, snapshot.Latest, Status(snapshot, now, zone).Accent, label,
            Stale: snapshot.OpenAlerts.Contains(AlertKind.SensorSilent));
    }

    private static int Priority(SensorSnapshot s) =>
        s.OpenAlerts.Contains(AlertKind.NeedsWater) ? 0 : s.OpenAlerts.Contains(AlertKind.SensorSilent) ? 1 : s.OpenAlerts.Contains(AlertKind.LowBattery) ? 2 : 3;

    private static string AgeLine(StoredReading? reading, DateTimeOffset now) =>
        reading is null ? "Brak odczytu" : $"Odczyt: {Age(now, reading.At)}";

    /// <summary>Dwa krótkie wiersze (mieszczą się na wąskim ekranie bez zawijania): wartości i wiek odczytu.</summary>
    private static void AppendReadingText(StringBuilder text, StoredReading? reading, DateTimeOffset now)
    {
        if (reading is null)
        {
            text.AppendLine("  odczyt: brak");
            return;
        }
        text.AppendLine($"  wilgotność {Percent(reading.MoisturePercent)}, temp. {Temperature(reading.SoilTemperatureCelsius)}");
        text.AppendLine($"  bateria {Percent(reading.BatteryPercent)}, odczyt {Age(now, reading.At)}");
    }
}
