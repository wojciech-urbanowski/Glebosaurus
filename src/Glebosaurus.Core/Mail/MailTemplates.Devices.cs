using System.Text;
using Glebosaurus.Core.Cloud;
using static Glebosaurus.Core.Mail.MailLayout;
using static Glebosaurus.Core.Mail.MailText;

namespace Glebosaurus.Core.Mail;

public static partial class MailTemplates
{
    private const string DevicesTitle = "Urządzenia";

    /// <summary>
    /// Sekcja „Urządzenia” raportu (pod czujnikami): karta na telefon i Raspberry Pi z baterią i temperaturą z ostatniego
    /// sygnału życia. Ostrzeżenia to te same zdania co na ekranie telefonu i w panelu WWW (<see cref="AgentDevice"/>).
    /// </summary>
    private static void AppendDevices(StringBuilder text, StringBuilder rows, IReadOnlyList<DeviceSnapshot> devices, DateTimeOffset now)
    {
        if (devices.Count == 0) return;
        text.AppendLine();
        text.AppendLine(DevicesTitle);
        rows.Append(Section(DevicesTitle));
        foreach (var device in devices)
        {
            var view = Describe(device, now);
            text.AppendLine();
            text.AppendLine($"{device.Name} ({view.Kind}) – {view.Badge.Text}");
            foreach (var line in view.TextLines) text.AppendLine($"  {line}");
            foreach (var warning in view.Warnings)
            foreach (var line in Wrap(warning, 40, "  "))
                text.AppendLine(line);
            text.AppendLine($"  {view.Contact}");
            if (device.Silent) text.AppendLine("  (wartości z ostatniego kontaktu)");

            IReadOnlyList<string> notes = device.Silent ? ["Nie łączy się z chmurą – wartości z ostatniego kontaktu.", .. view.Warnings] : view.Warnings;
            var footer = $"{Capitalize(view.Kind)} · Glebosaurus {device.Status.Version} · {view.Contact}";
            rows.Append(DeviceCard(device.Name, view.Badge, view.Metrics, notes, footer));
        }
    }

    /// <summary>Urządzenia z problemem (milczy albo ostrzeżenie) – liczą się do „Uwag” w pasku i tytule raportu.</summary>
    private static bool HasProblem(DeviceSnapshot device) => device.Silent || AgentDevice.Warnings(device.Status).Count > 0;

    /// <summary>Kolejność w sekcji: milczące, z ostrzeżeniem, reszta.</summary>
    private static int DevicePriority(DeviceSnapshot device) =>
        device.Silent ? 0 : AgentDevice.Warnings(device.Status).Count > 0 ? 1 : 2;

    private sealed record DeviceView(
        string Kind, (string Text, Tone Tone) Badge, IReadOnlyList<DeviceMetric> Metrics, IReadOnlyList<string> TextLines,
        IReadOnlyList<string> Warnings, string Contact);

    private static DeviceView Describe(DeviceSnapshot device, DateTimeOffset now)
    {
        var status = device.Status;
        var warnings = AgentDevice.Warnings(status);
        List<DeviceMetric> metrics;
        List<string> lines;
        if (status.Platform == "android")
        {
            var lowBattery = status.BatteryPercent is { } percent && percent < AgentDevice.LowBatteryPercent && status.Charging != true;
            var badTemperature = status.BatteryTemperatureCelsius is { } celsius
                                 && (celsius >= AgentDevice.HotBatteryCelsius || celsius < AgentDevice.ColdBatteryCelsius);
            var charger = status.Charging switch { true => "tak", false => "nie", null => "–" };
            metrics =
            [
                new("Bateria", Percent(status.BatteryPercent), lowBattery),
                new("Temperatura", Celsius(status.BatteryTemperatureCelsius), badTemperature),
                new("Ładowarka", charger, false)
            ];
            var battery = $"bateria {Percent(status.BatteryPercent)}" + status.Charging switch
            {
                true => ", na ładowarce",
                false => ", bez ładowarki",
                null => ""
            };
            lines = [battery, $"temp. akumulatora {Celsius(status.BatteryTemperatureCelsius)}"];
        }
        else
        {
            var hot = status.CpuTemperatureCelsius is >= AgentDevice.HotCpuCelsius;
            metrics = [new("Temp. procesora", Celsius(status.CpuTemperatureCelsius), hot), new("Zasilanie", "zasilacz", false)];
            lines = [$"temp. procesora {Celsius(status.CpuTemperatureCelsius)}", "bez baterii (zasilacz)"];
        }

        var reported = status.BatteryPercent is not null || status.BatteryTemperatureCelsius is not null || status.CpuTemperatureCelsius is not null;
        (string, Tone) badge = device.Silent ? ("milczy", Grey)
            : warnings.Count > 0 ? ("uwaga", Amber)
            : reported ? ("OK", Green)
            : ("brak danych", Grey);
        var contact = $"{(device.Silent ? "ostatni kontakt" : "kontakt")} {Age(now, device.LastSeenAt)}";
        return new(AgentDevice.Kind(status.Platform), badge, metrics, lines, warnings, contact);
    }

    private static string Celsius(double? value) => value is { } v ? AgentDevice.Celsius(v) : "–";
}
