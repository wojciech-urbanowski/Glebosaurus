using System.Text;
using System.Text.RegularExpressions;
using Glebosaurus.Core.Domain;
using Glebosaurus.Core.Mail;

namespace Glebosaurus.Tests;

/// <summary>
/// Wykres ostatnich 3 dób w raporcie mailowym (prototyp z 2026-11-14, poprawki użytkownika tego samego dnia: przedziały po
/// 2 godz., temperatura jako blady zakres od najniższej do najwyższej): sam HTML (bez SVG i obrazków), elementy na właściwej
/// wysokości, puste kolumny przy braku odczytów, różowe tło poniżej progu, mieszczenie się w limicie Gmaila i linijka znaków
/// w wersji tekstowej.
/// </summary>
public class MailChartTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 19, 0, 0, TimeSpan.Zero);
    private static readonly string[] To = ["ktos@example.com"];
    private const string Moisture = "background:#2a78d6";
    private const string TemperatureOnWhite = "background:#f8cab8";   // pomarańcz #eb6834 o kryciu 35% na białym
    private const string TemperatureOnPink = "background:#f2ad97";    // to samo na różowym tle „poniżej progu”
    private const string PinkBackground = "background:#f6d2cc";
    private const int Columns = 36;                                     // 3 doby po 2 godz.

    /// <summary>Temperatura zmienia się co odczyt o 2 °C (18 / 20 / 22 / 20 …) – w każdym przedziale 2 godz. jest pełny zakres 18–22.</summary>
    private static decimal Cycle(double hoursAgo) => TemperatureCycle[(int)Math.Round(hoursAgo * 4) % 4];

    private static readonly decimal[] TemperatureCycle = [18m, 20m, 22m, 20m];

    /// <summary>
    /// Czujnik z odczytami co 15 min z ostatnich 75 h (do <paramref name="stopHoursAgo"/> temu); wartości według godzin wstecz.
    /// Domyślna temperatura to <see cref="Cycle"/> – jej pasek zajmuje całą wysokość, więc widać go obok kreski wilgotności.
    /// </summary>
    private static SensorSnapshot Sensor(string name, Func<double, int?> moisture, Func<double, decimal?>? temperature = null, double stopHoursAgo = 0)
    {
        var sensor = Fixtures.Sensor(name.ToLowerInvariant(), name, Guid.NewGuid().ToString("N")[..16]);
        var history = new List<StoredReading>();
        for (var minutes = (int)(stopHoursAgo * 60); minutes <= 75 * 60; minutes += 15)
        {
            var hoursAgo = minutes / 60.0;
            history.Add(new(sensor.Id, Now.AddMinutes(-minutes), moisture(hoursAgo), temperature is null ? Cycle(hoursAgo) : temperature(hoursAgo), 80, 2));
        }
        var latest = history.MinBy(r => Now - r.At);
        var alerts = latest?.MoisturePercent < sensor.WateringBelowPercent ? new[] { AlertKind.NeedsWater } : [];
        return new(sensor, latest, alerts) { History = history };
    }

    private static MailMessage Report(params SensorSnapshot[] snapshots) => MailTemplates.Report(snapshots, To, "Salon", Now, TimeZoneInfo.Utc);

    private static int ChartCount(string html) => Regex.Count(html, "cellspacing=\"1\"");

    /// <summary>Komórki kolumn z danymi: zawartość (bloki od góry do dołu) i styl komórki.</summary>
    private static List<(string Css, string Blocks)> DataCells(string html) =>
        Regex.Matches(html, "<td valign=\"bottom\"(?: style=\"(?<css>[^\"]*)\")?>(?<blocks>.*?)</td>")
            .Select(m => (m.Groups["css"].Value, m.Groups["blocks"].Value)).ToList();

    [Fact]
    public void ReportDrawsOneChartPerSensorWithHistoryAndOneLegend()
    {
        var withoutHistory = Sensor("Bez historii", _ => 50) with { History = [] };
        var html = Report(Sensor("Monstera", h => 30 + (int)h / 10), Sensor("Fikus", _ => 50), withoutHistory).HtmlBody;

        Assert.Equal(2, ChartCount(html));
        Assert.Single(Regex.Matches(html, "Wykresy: ostatnie 3 doby, przedziały po 2 godz\\."));
        Assert.Equal(2 * Columns, DataCells(html).Count);                     // 12 kolumn na dobę, wszystkie z danymi
        Assert.All(DataCells(html), cell => Assert.Contains(Moisture, cell.Blocks));
        Assert.All(DataCells(html), cell => Assert.Contains(TemperatureOnWhite, cell.Blocks));
        Assert.DoesNotContain("<svg", html);
        Assert.DoesNotContain("<img", html);
        Assert.DoesNotContain("position:", html);                              // Gmail nie obsługuje pozycjonowania
        Assert.DoesNotContain("rgba(", html);                                  // „przezroczystość” wyliczona z góry
    }

    [Fact]
    public void ReportWithoutHistoryHasNoChart()
    {
        var html = Report(Sensor("Monstera", _ => 40) with { History = [] }).HtmlBody;

        Assert.Equal(0, ChartCount(html));
        Assert.DoesNotContain("Wykresy:", html);
    }

    [Fact]
    public void TemperatureIsAPaleRangeFromLowestToHighestUnderTheMoistureMark()
    {
        var html = Report(Sensor("Zmienna", _ => 50, h => Cycle(h))).HtmlBody;

        Assert.Contains(">22°<", html);
        Assert.Contains(">18°<", html);
        foreach (var (_, blocks) in DataCells(html))
        {
            var heights = Regex.Matches(blocks, "height:(\\d+)px;(background:#[0-9a-f]{6})").Select(m => (Height: int.Parse(m.Groups[1].Value), Color: m.Groups[2].Value)).ToList();
            Assert.Equal(3, heights.Single(b => b.Color == Moisture).Height);           // kreska wilgotności 3 px, na wierzchu
            Assert.Equal(64 - 3, heights.Where(b => b.Color == TemperatureOnWhite).Sum(b => b.Height)); // pasek przez całą skalę, minus kreska
        }
    }

    [Fact]
    public void ConstantTemperatureStillShowsAThinBar()
    {
        var html = Report(Sensor("Stała", _ => 100, _ => 20m)).HtmlBody;            // kreska wilgotności na górze, temperatura w środku skali

        Assert.All(DataCells(html), cell => Assert.Contains($"height:2px;{TemperatureOnWhite}", cell.Blocks));
    }

    [Fact]
    public void ColumnsBelowTheWateringThresholdArePinkAndKeepTheTemperatureVisible()
    {
        var dry = Report(Sensor("Sucha", _ => 18)).HtmlBody;                   // próg podlania 25%
        var wet = Report(Sensor("Mokra", _ => 60)).HtmlBody;

        Assert.Equal(Columns, DataCells(dry).Count(c => c.Css.Contains(PinkBackground, StringComparison.Ordinal)));
        Assert.Contains(TemperatureOnPink, dry);                               // na różowym tle ciemniejszy odcień
        Assert.Contains("poniżej progu podlania", dry);
        Assert.DoesNotContain(DataCells(wet), c => c.Css.Contains(PinkBackground, StringComparison.Ordinal));
        Assert.DoesNotContain(TemperatureOnPink, wet);
        Assert.DoesNotContain("poniżej progu podlania", wet);
    }

    [Fact]
    public void MissingReadingsLeaveEmptyColumns()
    {
        // Odczyty kończą się 12 h przed 19:00 (o 07:00) → puste przedziały 08–10, 10–12, …, 18–20.
        var html = Report(Sensor("Cisy", _ => 50, stopHoursAgo: 12)).HtmlBody;

        Assert.Equal(Columns - 6, DataCells(html).Count);
        Assert.Equal(6 + 2, Regex.Count(html, "<td></td>"));                  // + puste rogi pod osiami
    }

    [Fact]
    public void MoistureMarksRiseWithTheValueAndAxesShowTheRange()
    {
        // Wilgotność rośnie od ok. 20% (75 h temu) do 60% (teraz); bez temperatury, więc w kolumnie jest tylko kreska.
        var html = Report(Sensor("Rośnie", h => (int)Math.Round(60 - h * 40 / 75), _ => null)).HtmlBody;

        var heights = DataCells(html)
            .Select(c => Regex.Match(c.Css, @"padding-bottom:(\d+)px") is { Success: true } p ? int.Parse(p.Groups[1].Value) : 0)
            .ToList();
        Assert.Equal(Columns, heights.Count);
        Assert.Equal(heights.Order(), heights);                                // kolejne kolumny nie niżej od poprzednich
        Assert.True(heights[0] <= 5, $"pierwsza kolumna na wysokości {heights[0]} px");
        Assert.True(heights[^1] >= 55, $"ostatnia kolumna na wysokości {heights[^1]} px");
        Assert.Contains(">60%<", html);
        Assert.DoesNotContain($"{TemperatureOnWhite}\"", html);                 // pasków temperatury brak (klucz w objaśnieniu zostaje)
    }

    [Fact]
    public void ManySensorsGetWiderColumnsToStayUnderGmailClipping()
    {
        var four = Report(Enumerable.Range(1, 4).Select(i => Sensor($"Roślina {i}", h => 40 + (int)(h % 20), h => 20 + (decimal)(h % 5))).ToArray()).HtmlBody;
        var twenty = Report(Enumerable.Range(1, 20).Select(i => Sensor($"Roślina {i}", h => 40 + (int)(h % 20), h => 20 + (decimal)(h % 5))).ToArray()).HtmlBody;

        Assert.Contains("przedziały po 2 godz.", four);
        Assert.Equal(20, ChartCount(twenty));
        Assert.DoesNotContain("przedziały po 2 godz.", twenty);
        Assert.True(Encoding.UTF8.GetByteCount(twenty) <= 90_000, $"raport z 20 czujnikami ma {Encoding.UTF8.GetByteCount(twenty)} B");
    }

    [Fact]
    public void TooManySensorsForAnyChartFallBackToTheReportWithoutCharts()
    {
        var html = Report(Enumerable.Range(1, 60).Select(i => Sensor($"Roślina {i}", h => 40 + (int)(h % 20))).ToArray()).HtmlBody;

        Assert.Equal(0, ChartCount(html));
        Assert.Equal(60, Regex.Count(html, "background:#fff;border:1px solid"));    // karty są, tylko bez wykresów
    }

    [Fact]
    public void PlainTextHasAShortSparklineOfMoisture()
    {
        var text = Report(Sensor("Rośnie", h => (int)Math.Round(60 - h * 40 / 75)), Sensor("Stała", h => 46 + (int)(h % 2))).TextBody;

        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.StartsWith("  3 doby: ", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.True(l.Length <= 40, $"za długi wiersz: {l}"));
        Assert.Matches(@"^  3 doby: ▁.*█ 2\d→60%$", lines[0]);                // rośnie od najniższego do najwyższego słupka
        Assert.DoesNotContain('█', lines[1]);                                  // wahania o 1 p.p. nie wyglądają jak skoki
        Assert.DoesNotContain('▁', lines[1]);
    }

    [Fact]
    public void SamplesForThePreviewIncludeChartsAndFitTheBudget()
    {
        var report = MailSamples.Build(To, "Salon", Now, TimeZoneInfo.Utc).Single(m => m.Subject.Contains("raport", StringComparison.Ordinal));
        var large = MailSamples.LargeReport(To, "Salon", Now, TimeZoneInfo.Utc);

        Assert.Equal(4, ChartCount(report.HtmlBody));
        Assert.Contains("przedziały po 2 godz.", report.HtmlBody);
        Assert.Equal(20, ChartCount(large.HtmlBody));
        Assert.True(Encoding.UTF8.GetByteCount(large.HtmlBody) <= 90_000);
        Assert.StartsWith(MailSamples.SubjectPrefix, large.Subject);
    }
}
