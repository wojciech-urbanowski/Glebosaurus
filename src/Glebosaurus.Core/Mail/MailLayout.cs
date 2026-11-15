using System.Text;
using Glebosaurus.Core.Config;
using Glebosaurus.Core.Domain;

namespace Glebosaurus.Core.Mail;

/// <summary>
/// Pastelowy zestaw jednego stanu: <paramref name="Soft"/> to jasne tło (plakietki, pasek podsumowania),
/// <paramref name="Mid"/> – średni odcień na paski i ramki, <paramref name="Ink"/> – ciemny tekst czytelny na obu.
/// Tekst na pastelu jest ciemny (kontrast ≥ 5:1), nigdy biały na nasyconym tle.
/// </summary>
internal sealed record Tone(string Soft, string Mid, string Ink);

/// <summary>
/// Klocki HTML maili projektowane pod wąski ekran telefonu: jedna kolumna o szerokości do <see cref="MaxWidth"/> px,
/// karta na czujnik zamiast tabeli z wieloma kolumnami, duże liczby i odstępy, kwadratowe elementy (bez zaokrągleń) w
/// pastelowych kolorach, style wyłącznie inline (klienci poczty wycinają arkusze i nie zawsze obsługują media queries,
/// więc układ nie może od nich zależeć).
/// </summary>
internal static class MailLayout
{
    public const int MaxWidth = 480;

    public static readonly Tone Red = new("#f6d2cc", "#e59a8c", "#8c2f20");
    public static readonly Tone Amber = new("#f8e6bd", "#efc66f", "#74500a");
    public static readonly Tone Green = new("#d3ebd7", "#94cf9f", "#2b6a3c");
    public static readonly Tone Grey = new("#e4e4df", "#bcbcb5", "#55554f");

    private const string PageBackground = "#f4f3ee";
    internal const string TextColor = "#2b2b28";
    internal const string MutedColor = "#6b6b66";
    internal const string BorderColor = "#e3e3df";
    private const string TrackColor = "#ebeae5";

    private const string Font = "-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif";

    /// <summary>Znaki niewidoczne po tekście podglądu, żeby skrzynka nie dociągała do niego początku właściwej treści.</summary>
    private static readonly string PreheaderFiller = string.Concat(Enumerable.Repeat("&#8204;&nbsp;", 40));

    /// <summary>Koduje tylko znaki specjalne HTML. Polskie litery zostają jak są (poczta idzie w UTF-8); <c>WebUtility.HtmlEncode</c> zamieniałby np. „ó” i „°” na encje liczbowe.</summary>
    public static string Enc(string text) => text
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("'", "&#39;", StringComparison.Ordinal);

    /// <param name="preheader">Tekst widoczny na liście wiadomości obok tytułu (na telefonie to często jedyne, co się czyta).</param>
    public static string Page(string title, string preheader, string rows) =>
        "<!doctype html><html lang=\"pl\"><head><meta charset=\"utf-8\">" +
        "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
        $"<title>{Enc(title)}</title></head>" +
        $"<body style=\"margin:0;padding:0;background:{PageBackground};-webkit-text-size-adjust:100%\">" +
        $"<div style=\"display:none;max-height:0;overflow:hidden;opacity:0;color:transparent;mso-hide:all\">{Enc(preheader)}{PreheaderFiller}</div>" +
        $"<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"background:{PageBackground}\"><tr>" +
        "<td align=\"center\" style=\"padding:12px 10px\">" +
        $"<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"max-width:{MaxWidth}px;font-family:{Font};font-size:16px;line-height:1.4;color:{TextColor}\">" +
        rows + "</table></td></tr></table></body></html>";

    public static string Header(string title, string monitorName, string when) =>
        "<tr><td style=\"padding:4px 2px 14px\">" +
        $"<div style=\"font-size:12px;font-weight:700;letter-spacing:1.5px;color:{Green.Ink}\">GLEBOSAURUS</div>" +
        $"<div style=\"font-size:24px;font-weight:700;line-height:1.2;padding-top:2px\">{Enc(title)}</div>" +
        $"<div style=\"font-size:14px;color:{MutedColor};padding-top:4px\">{Enc(monitorName)} · {Enc(when)}</div></td></tr>";

    /// <summary>Pastelowy pasek z jednym zdaniem podsumowania, widoczny bez przewijania.</summary>
    public static string Banner(string headline, string? detail, Tone tone) =>
        $"<tr><td style=\"padding:0 0 12px\"><div style=\"background:{tone.Soft};border-left:6px solid {tone.Mid};color:{tone.Ink};padding:12px 14px\">" +
        $"<div style=\"font-size:18px;font-weight:700\">{Enc(headline)}</div>" +
        (detail is null ? "" : $"<div style=\"font-size:14px;padding-top:2px\">{Enc(detail)}</div>") +
        "</div></td></tr>";

    public static string Paragraph(string text) =>
        $"<tr><td style=\"padding:0 2px 12px;font-size:16px\">{Enc(text)}</td></tr>";

    /// <summary>Stopka każdego maila: że jest automatyczny i z jakiej wersji Glebosaurusa pochodzi.</summary>
    public static string Footer() =>
        $"<tr><td style=\"padding:6px 2px 0;font-size:12px;color:{MutedColor}\">{Enc(MailText.FooterLine)}</td></tr>";

    /// <summary>Śródtytuł sekcji.</summary>
    public static string Section(string title) =>
        $"<tr><td style=\"padding:6px 2px 8px;font-size:16px;font-weight:700\">{Enc(title)}</td></tr>";

    /// <summary>Drobna notatka pod listą (np. „…i 5 wcześniejszych”).</summary>
    public static string Note(string text) =>
        $"<tr><td style=\"padding:0 2px 10px;font-size:13px;color:{MutedColor}\">{Enc(text)}</td></tr>";

    /// <summary>Kilka liczników obok siebie w jednej karcie (np. informacje / ostrzeżenia / błędy); wartość większa od zera dostaje kolor tonu.</summary>
    public static string Counters(params (string Label, int Value, Tone? Tone)[] items)
    {
        var width = $"{100 / items.Length}%";
        var cells = string.Concat(items.Select(i =>
            Metric(width, i.Label, i.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), i.Tone is { } tone && i.Value > 0 ? tone.Ink : TextColor)));
        return $"<tr><td style=\"padding:0 0 10px\"><div style=\"background:#fff;border:1px solid {BorderColor};padding:12px 14px\">" +
               $"<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\"><tr>{cells}</tr></table></div></td></tr>";
    }

    /// <summary>Jeden wpis dziennika (ostrzeżenie lub błąd): godzina, poziom i treść, która zawija się na wąskim ekranie.</summary>
    public static string LogEntryRow(string time, string levelText, string message, Tone tone) =>
        $"<tr><td style=\"padding:0 0 6px\"><div style=\"background:#fff;border:1px solid {BorderColor};border-left:5px solid {tone.Mid};padding:8px 12px\">" +
        $"<div style=\"font-size:12px;color:{MutedColor}\">{Enc(time)} · {Enc(levelText)}</div>" +
        $"<div style=\"font-size:14px;word-break:break-word;overflow-wrap:anywhere\">{Enc(message)}</div></div></td></tr>";

    /// <summary>
    /// Karta jednego czujnika: nazwa, plakietki stanu, opcjonalny opis, trzy liczby (wilgotność, temperatura, bateria),
    /// pasek wilgotności i wiek odczytu.
    /// </summary>
    /// <param name="chart">Gotowy wykres z ostatnich dób (<see cref="MailChart.Html"/>) pod liczbami; null = bez wykresu.</param>
    public static string SensorCard(
        SensorConfig sensor, StoredReading? reading, string age, Tone accent,
        IEnumerable<(string Text, Tone Tone)> badges, string? detail, string? chart = null)
    {
        var html = new StringBuilder();
        html.Append("<tr><td style=\"padding:0 0 10px\">");
        html.Append($"<div style=\"background:#fff;border:1px solid {BorderColor};border-left:5px solid {accent.Mid};padding:12px 14px\">");
        html.Append($"<div style=\"font-size:18px;font-weight:700;line-height:1.25\">{Enc(sensor.Name)}</div>");

        var badgeHtml = string.Concat(badges.Select(b =>
            $"<span style=\"display:inline-block;background:{b.Tone.Soft};border:1px solid {b.Tone.Mid};color:{b.Tone.Ink};font-size:13px;font-weight:600;padding:2px 9px;margin:0 6px 4px 0\">{Enc(b.Text)}</span>"));
        if (badgeHtml.Length > 0) html.Append($"<div style=\"padding-top:6px\">{badgeHtml}</div>");
        if (detail is not null) html.Append($"<div style=\"font-size:15px;padding-top:2px\">{Enc(detail)}</div>");

        if (reading is not null)
        {
            var moistureTone = reading.MoisturePercent is { } m ? MoistureTone(sensor, m) : null;
            var batteryLow = reading.BatteryPercent is { } b && sensor.IsBatteryLow(b);
            html.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"margin-top:10px\"><tr>");
            html.Append(Metric("34%", "Wilgotność", MailText.Percent(reading.MoisturePercent), moistureTone?.Ink ?? TextColor));
            html.Append(Metric("33%", "Temp. gleby", MailText.Temperature(reading.SoilTemperatureCelsius), TextColor));
            html.Append(Metric("33%", "Bateria", MailText.Percent(reading.BatteryPercent), batteryLow ? Amber.Ink : TextColor));
            html.Append("</tr></table>");
            if (reading.MoisturePercent is { } level && moistureTone is not null) html.Append(Bar(level, moistureTone));
        }
        if (chart is not null) html.Append(chart);

        html.Append($"<div style=\"font-size:13px;color:{MutedColor};padding-top:8px\">{Enc(age)}</div>");
        html.Append("</div></td></tr>");
        return html.ToString();
    }

    /// <summary>Wilgotność względem progów czujnika: poniżej progu podlania czerwień, w strefie histerezy żółć, wyżej zieleń.</summary>
    public static Tone MoistureTone(SensorConfig sensor, int moisture) =>
        moisture < sensor.WateringBelowPercent ? Red : moisture < sensor.WateringClearAbovePercent ? Amber : Green;

    private static string Metric(string width, string label, string value, string color) =>
        $"<td width=\"{width}\" valign=\"top\" style=\"padding-right:6px\">" +
        $"<div style=\"font-size:12px;color:{MutedColor}\">{Enc(label)}</div>" +
        $"<div style=\"font-size:20px;font-weight:700;color:{color}\">{Enc(value)}</div></td>";

    private static string Bar(int percent, Tone tone) =>
        $"<div style=\"margin-top:8px;height:10px;background:{TrackColor}\">" +
        $"<div style=\"width:{Math.Clamp(percent, 0, 100)}%;height:10px;font-size:0;line-height:0;background:{tone.Mid}\">&nbsp;</div></div>";
}
