using System.Globalization;
using System.Text;
using Glebosaurus.Core.Charts;
using Glebosaurus.Core.Config;
using Glebosaurus.Core.Domain;
using static Glebosaurus.Core.Mail.MailLayout;

namespace Glebosaurus.Core.Mail;

/// <summary>
/// Wykres ostatnich 3 dób w mailu, zbudowany z samego HTML – bez SVG (Gmail go nie pokazuje) i bez obrazków rastrowych.
/// Tabela, w której każda kolumna to przedział czasu (domyślnie 4 godz., w czasie lokalnym, jak w panelu). W kolumnie:
/// niebieska kreska na wysokości średniej wilgotności (skala po lewej, zawsze 0–100%) – na wierzchu, bo jest najważniejsza – a pod nią blady
/// pomarańczowy pasek od najniższej do najwyższej temperatury z przedziału (skala po prawej). Elementy stoją na właściwej
/// wysokości dzięki <c>valign=bottom</c>, dopełnieniu komórki od dołu i ułożonym jeden pod drugim blokom o zadanej wysokości –
/// Gmail nie obsługuje pozycjonowania ani ujemnych marginesów, ale wysokości, tła i dopełnienia tak. Różowe tło kolumny =
/// średnia wilgotność poniżej progu podlania. Pusta kolumna = brak odczytów.
/// </summary>
internal static class MailChart
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(72);

    /// <summary>
    /// Długości przedziałów od najdrobniejszej (4 godz. = 6 kolumn na dobę; do 2026-11-22 – 2 godz., zgrubniej na życzenie
    /// użytkownika, gdy raport dostał kafelki z zestawieniem). Raport bierze pierwszą, przy której cały mail mieści się w
    /// <see cref="HtmlBudgetBytes"/> (przy wielu czujnikach kolumny są szersze).
    /// </summary>
    public static readonly IReadOnlyList<int> BucketLadder = [240, 360, 480, 720];

    /// <summary>Gmail przycina wiadomości, których HTML przekracza ok. 102 KB („Wyświetl całą wiadomość”) – zostawiamy zapas.</summary>
    public const int HtmlBudgetBytes = 90_000;

    public const string MoistureColor = "#2a78d6";
    public const string TemperatureColor = "#eb6834";

    /// <summary>
    /// Krycie paska temperatury. Kolor jest wyliczany z góry dla białego i dla różowego tła (wygląda jak przezroczystość, a nie
    /// zależy od obsługi <c>rgba</c> i jest krótszy w zapisie).
    /// </summary>
    private const double TemperatureAlpha = 0.35;

    public static readonly string TemperatureOnWhite = Blend(TemperatureColor, "#ffffff", TemperatureAlpha);
    public static readonly string TemperatureOnPink = Blend(TemperatureColor, Red.Soft, TemperatureAlpha);

    /// <summary>Oś wilgotności zawsze od 0 do 100% (życzenie użytkownika z 2026-11-15) – jak na wykresach w panelu.</summary>
    private static readonly (double Lo, double Hi) MoistureAxis = (0, 100);

    private const int PlotHeight = 64;
    private const int Mark = 3;
    private const int MinRange = 2;
    private const int LabelWidth = 30;
    private const string AxisColor = "#bcbcb5";

    private static readonly string[] Weekdays = ["niedz.", "pon.", "wt.", "śr.", "czw.", "pt.", "sob."];
    private const string SparkBlocks = "▁▂▃▄▅▆▇█";

    private sealed record Column(long LocalStart, double? Moisture, double? TemperatureMin, double? TemperatureMax);

    /// <summary>Kolumny okna 3 dób: ostatnia zawiera „teraz”, granice jak w <see cref="ReadingSeriesBuilder.LocalBucketStart"/>.</summary>
    private static List<Column> Columns(IReadOnlyList<StoredReading> history, DateTimeOffset now, TimeZoneInfo zone, int bucketMinutes)
    {
        var bucketTicks = TimeSpan.FromMinutes(bucketMinutes).Ticks;
        var count = (int)(Window.Ticks / bucketTicks);
        var last = ReadingSeriesBuilder.LocalBucketStart(now, bucketMinutes, zone);
        var first = last - (count - 1) * bucketTicks;
        var groups = history
            .Where(r => r.At <= now)
            .GroupBy(r => ReadingSeriesBuilder.LocalBucketStart(r.At, bucketMinutes, zone))
            .Where(g => g.Key >= first && g.Key <= last)
            .ToDictionary(g => g.Key, g => g.ToList());
        return Enumerable.Range(0, count).Select(i =>
        {
            var key = first + i * bucketTicks;
            if (!groups.TryGetValue(key, out var readings)) return new Column(key, null, null, null);
            var moisture = readings.Where(r => r.MoisturePercent is not null).Select(r => (double)r.MoisturePercent!.Value).ToList();
            var temperature = readings.Where(r => r.SoilTemperatureCelsius is not null).Select(r => (double)r.SoilTemperatureCelsius!.Value).ToList();
            return new Column(key,
                moisture.Count > 0 ? moisture.Average() : null,
                temperature.Count > 0 ? temperature.Min() : null,
                temperature.Count > 0 ? temperature.Max() : null);
        }).ToList();
    }

    /// <summary>Zakres osi: od najmniejszej do największej wartości, rozszerzony do minimalnej rozpiętości i zaokrąglony do całości.</summary>
    private static (double Lo, double Hi) Domain(IEnumerable<double> values, double minSpan)
    {
        var list = values.ToList();
        double min = list.Min(), max = list.Max();
        if (max - min < minSpan)
        {
            var center = (min + max) / 2;
            min = center - minSpan / 2;
            max = center + minSpan / 2;
        }
        return (Math.Floor(min), Math.Ceiling(max));
    }

    /// <summary>Wysokość nad dnem wykresu (px) dla wartości w zakresie osi, przy wykresie wysokim na <paramref name="height"/> px.</summary>
    private static int Y(double value, (double Lo, double Hi) domain, int height) =>
        (int)Math.Round((value - domain.Lo) / Math.Max(1e-9, domain.Hi - domain.Lo) * height);

    /// <summary>Wykres do karty czujnika; null, gdy w oknie nie ma żadnej wartości.</summary>
    public static string? Html(SensorConfig sensor, IReadOnlyList<StoredReading> history, DateTimeOffset now, TimeZoneInfo zone, int bucketMinutes)
    {
        var columns = Columns(history, now, zone, bucketMinutes);
        var moistureValues = columns.Where(c => c.Moisture is not null).Select(c => c.Moisture!.Value).ToList();
        var temperatureValues = columns.Where(c => c.TemperatureMin is not null).SelectMany(c => new[] { c.TemperatureMin!.Value, c.TemperatureMax!.Value }).ToList();
        if (moistureValues.Count == 0 && temperatureValues.Count == 0) return null;
        var moisture = MoistureAxis;
        var temperature = temperatureValues.Count > 0 ? Domain(temperatureValues, 2) : default;
        var perDay = 1440 / bucketMinutes;

        var html = new StringBuilder();
        html.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"1\" style=\"table-layout:fixed;margin-top:10px;font-size:0;line-height:0\"><tr>");
        html.Append(Labels(moistureValues.Count > 0 ? $"{moisture.Hi:0}%" : "", moistureValues.Count > 0 ? $"{moisture.Lo:0}%" : "", "right"));
        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            var newDay = i > 0 && new DateTime(column.LocalStart).Date != new DateTime(columns[i - 1].LocalStart).Date;
            var dry = column.Moisture is { } m && m < sensor.WateringBelowPercent;
            (int Bottom, int Top)? moistureMark = column.Moisture is { } mv && moistureValues.Count > 0
                ? (Y(mv, moisture, PlotHeight - Mark), Y(mv, moisture, PlotHeight - Mark) + Mark)
                : null;
            (int Bottom, int Top)? temperatureRange = column.TemperatureMin is { } lo && column.TemperatureMax is { } hi && temperatureValues.Count > 0
                ? Range(Y(lo, temperature, PlotHeight), Y(hi, temperature, PlotHeight))
                : null;
            html.Append(Cell(moistureMark, temperatureRange, newDay, dry));
        }
        html.Append(Labels(temperatureValues.Count > 0 ? Degrees(temperature.Hi) : "", temperatureValues.Count > 0 ? Degrees(temperature.Lo) : "", "left"));
        html.Append("</tr><tr><td></td>");

        // Podpisy dni pod kolumnami; ich górna ramka to zarazem linia dna wykresu.
        var start = 0;
        while (start < columns.Count)
        {
            var day = new DateTime(columns[start].LocalStart).Date;
            var end = start;
            while (end + 1 < columns.Count && new DateTime(columns[end + 1].LocalStart).Date == day) end++;
            var span = end - start + 1;
            var label = span * 2 >= perDay ? (perDay >= 6 && span >= 4 ? $"{Weekdays[(int)day.DayOfWeek]} {day:d.MM}" : $"{day:d.MM}") : "";
            html.Append($"<td colspan=\"{span}\" style=\"border-top:1px solid {AxisColor};{(start > 0 ? $"border-left:1px solid {AxisColor};" : "")}padding:2px 0 0 3px;font-size:11px;line-height:13px;color:{MutedColor};white-space:nowrap;overflow:hidden\">{Enc(label)}</td>");
            start = end + 1;
        }
        html.Append("<td></td></tr></table>");
        return html.ToString();
    }

    /// <summary>Pasek temperatury co najmniej <see cref="MinRange"/> px wysoki (przy stałej temperaturze byłby niewidoczny).</summary>
    private static (int Bottom, int Top) Range(int bottom, int top)
    {
        if (top - bottom >= MinRange) return (bottom, top);
        var center = (bottom + top) / 2.0;
        var low = (int)Math.Clamp(Math.Round(center - MinRange / 2.0), 0, PlotHeight - MinRange);
        return (low, low + MinRange);
    }

    /// <summary>
    /// Jedna kolumna jako bloki ułożone od góry do dołu: tam, gdzie jest kreska wilgotności – niebieski (na wierzchu), gdzie
    /// pasek temperatury – blady pomarańcz, między nimi przerwa; pusta przestrzeń pod najniższym blokiem to dopełnienie komórki.
    /// </summary>
    private static string Cell((int Bottom, int Top)? moisture, (int Bottom, int Top)? temperature, bool newDay, bool dry)
    {
        var style = new StringBuilder();
        if (newDay) style.Append($"border-left:1px solid {BorderColor};");
        if (dry) style.Append($"background:{Red.Soft};");
        if (moisture is null && temperature is null) return style.Length == 0 ? "<td></td>" : $"<td style=\"{style.ToString().TrimEnd(';')}\"></td>";

        var temperatureColor = dry ? TemperatureOnPink : TemperatureOnWhite;
        string? ColorAt(int y) =>
            moisture is { } m && y >= m.Bottom && y < m.Top ? MoistureColor
            : temperature is { } t && y >= t.Bottom && y < t.Top ? temperatureColor
            : null;

        var edges = new SortedSet<int>();
        foreach (var (bottom, top) in new[] { moisture, temperature }.OfType<(int Bottom, int Top)>())
        {
            edges.Add(bottom);
            edges.Add(top);
        }
        // Odcinki od dołu: [krawędź, następna krawędź) w jednym kolorze; sąsiednie w tym samym kolorze łączymy.
        var segments = new List<(int Height, string? Color)>();
        var list = edges.ToList();
        for (var k = 0; k + 1 < list.Count; k++)
        {
            var color = ColorAt(list[k]);
            var height = list[k + 1] - list[k];
            if (segments.Count > 0 && segments[^1].Color == color) segments[^1] = (segments[^1].Height + height, color);
            else segments.Add((height, color));
        }
        var padding = list[0];
        var blocks = new StringBuilder();
        for (var k = segments.Count - 1; k >= 0; k--)
        {
            var (height, color) = segments[k];
            blocks.Append(color is null ? $"<div style=\"height:{height}px\"></div>" : $"<div style=\"height:{height}px;background:{color}\"></div>");
        }
        if (padding > 0) style.Append($"padding-bottom:{padding}px");
        var css = style.ToString().TrimEnd(';');
        return css.Length == 0 ? $"<td valign=\"bottom\">{blocks}</td>" : $"<td valign=\"bottom\" style=\"{css}\">{blocks}</td>";
    }

    private static string Labels(string top, string bottom, string align) =>
        $"<td valign=\"top\" style=\"width:{LabelWidth}px;font-size:11px;line-height:12px;color:{MutedColor};text-align:{align};padding-{(align == "right" ? "right" : "left")}:4px\">" +
        $"<div>{Enc(top)}</div><div style=\"height:{PlotHeight - 24}px\"></div><div>{Enc(bottom)}</div></td>";

    private static string Degrees(double value) => value.ToString("0", CultureInfo.InvariantCulture).Replace('-', '−') + "°";

    /// <summary>Kolor <paramref name="foreground"/> o kryciu <paramref name="alpha"/> położony na <paramref name="background"/> (#rrggbb).</summary>
    private static string Blend(string foreground, string background, double alpha)
    {
        int Channel(string hex, int index) => int.Parse(hex.AsSpan(1 + 2 * index, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var channels = Enumerable.Range(0, 3).Select(i =>
            (int)Math.Round(alpha * Channel(foreground, i) + (1 - alpha) * Channel(background, i)));
        return "#" + string.Concat(channels.Select(c => c.ToString("x2", CultureInfo.InvariantCulture)));
    }

    /// <summary>Objaśnienie wykresów – raz na raport, nad kartami (każda karta z osobna nie powtarza go, żeby mail był lżejszy).</summary>
    public static string Legend(int bucketMinutes, bool anyDry)
    {
        var hours = (bucketMinutes / 60.0).ToString("0.#", CultureInfo.InvariantCulture).Replace('.', ',');
        var moistureKey = $"<span style=\"display:inline-block;width:14px;height:0;border-top:{Mark}px solid {MoistureColor};vertical-align:middle\"></span>";
        var temperatureKey = $"<span style=\"display:inline-block;width:8px;height:12px;background:{TemperatureOnWhite};vertical-align:middle\"></span>";
        var dry = anyDry
            ? $" · <span style=\"display:inline-block;width:10px;height:10px;background:{Red.Soft};vertical-align:middle\"></span> poniżej progu podlania"
            : "";
        return $"<tr><td style=\"padding:0 2px 10px;font-size:13px;line-height:1.6;color:{MutedColor}\">Wykresy: ostatnie 3 doby, przedziały po {hours} godz. · " +
               $"{moistureKey} wilgotność, średnia (skala z lewej) · {temperatureKey} temperatura, od najniższej do najwyższej (z prawej){dry}</td></tr>";
    }

    /// <summary>Czy w oknie jest choć jedna kolumna z wilgotnością poniżej progu (wtedy objaśnienie mówi o różowym tle).</summary>
    public static bool AnyDry(SensorConfig sensor, IReadOnlyList<StoredReading> history, DateTimeOffset now, TimeZoneInfo zone, int bucketMinutes) =>
        Columns(history, now, zone, bucketMinutes).Any(c => c.Moisture is { } m && m < sensor.WateringBelowPercent);

    /// <summary>
    /// Wersja tekstowa: wilgotność z 3 dób jako 12 znaków (średnie z 6 godz.) i zmiana od pierwszej do ostatniej, np.
    /// „  3 doby: ▄▄▄▄▄▄▄▃▃▃▃▃ 45→30%” (skala 0–100%, jak na wykresie); null, gdy brak wartości.
    /// </summary>
    public static string? TextLine(IReadOnlyList<StoredReading> history, DateTimeOffset now, TimeZoneInfo zone)
    {
        var values = Columns(history, now, zone, 360).Select(c => c.Moisture).ToList();
        var present = values.Where(v => v is not null).Select(v => v!.Value).ToList();
        if (present.Count == 0) return null;
        // Ta sama skala co na wykresie: ▁ = 0%, █ = 100%.
        var (lo, hi) = MoistureAxis;
        var spark = string.Concat(values.Select(v => v is { } x
            ? SparkBlocks[Math.Clamp((int)Math.Round((x - lo) / Math.Max(1, hi - lo) * (SparkBlocks.Length - 1)), 0, SparkBlocks.Length - 1)]
            : '·'));
        return $"  3 doby: {spark} {Math.Round(present[0]):0}→{Math.Round(present[^1]):0}%";
    }
}
