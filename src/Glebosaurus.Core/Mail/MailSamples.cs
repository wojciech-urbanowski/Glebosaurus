using Glebosaurus.Core.Cloud;
using Glebosaurus.Core.Config;
using Glebosaurus.Core.Domain;

namespace Glebosaurus.Core.Mail;

/// <summary>
/// Przykładowe wiadomości ze zmyślonymi czujnikami: pozwalają obejrzeć wygląd maili (też na telefonie) bez czekania na
/// prawdziwy alert. Tytuły i nazwa monitora są oznaczone jako przykład, żeby nikt nie wziął ich za prawdziwe.
/// </summary>
public static class MailSamples
{
    public const string SubjectPrefix = "[PRZYKŁAD] ";

    /// <summary>Wiadomość próbna, alert o dwóch czujnikach i raport z czujnikami w różnym stanie.</summary>
    public static IReadOnlyList<MailMessage> Build(IReadOnlyList<string> to, string monitorName, DateTimeOffset now, TimeZoneInfo zone)
    {
        var name = $"{monitorName} (przykład)";

        var monstera = new SensorConfig { Id = "monstera", Name = "Monstera w salonie" };
        var fikus = new SensorConfig { Id = "fikus", Name = "Fikus" };
        // Paproć ma czujnik Xiaomi Flower Care – w karcie także światło i żyzność.
        var papro = new SensorConfig { Id = "paprot", Name = "Paproć w kuchni", Model = SensorModel.FlowerCare, Mac = "5C:85:7E:00:00:01" };
        var cisy = new SensorConfig { Id = "cisy", Name = "Cisy" };

        var dry = new StoredReading(monstera.Id, now.AddMinutes(-4), 18, 21.4m, 63, 2);
        var lowBattery = new StoredReading(fikus.Id, now.AddMinutes(-9), 46, 22.8m, 14, 1);
        var fine = new StoredReading(papro.Id, now.AddMinutes(-2), 64, 23.7m, 88, null) { LightLux = 12_480, ConductivityMicroSiemens = 1140 };
        var stale = new StoredReading(cisy.Id, now.AddHours(-7), 52, 19.5m, 71, 2);

        AlertRecord Alert(SensorConfig sensor, AlertKind kind, string detail) => new(0, sensor.Id, kind, detail, now.AddHours(-1), null, null);

        var notices = new List<AlertNotice>
        {
            new(monstera, Alert(monstera, AlertKind.NeedsWater, "wilgotność gleby 18% (próg 25%)"), dry, false),
            new(fikus, Alert(fikus, AlertKind.LowBattery, "bateria czujnika 14% (próg 18% lub mniej)"), lowBattery, false)
        };
        // Historia do wykresów: Paproć podlana ok. 40 h temu, Monstera wysycha (od ok. 35 h poniżej progu), Fikus stały,
        // Cisy na dworze (chłodniej, większe wahania) i bez odczytów od 7 h.
        var snapshots = new List<SensorSnapshot>
        {
            new(papro, fine, []) { History = History(papro.Id, now, zone, h => h > 40 ? 30 + (h - 40) * 0.24 : 72 - (40 - h) * 0.2, 22.5, 1.2) },
            new(monstera, dry, [AlertKind.NeedsWater]) { History = History(monstera.Id, now, zone, h => 18 + h * 0.2, 21.5, 1.5) },
            new(fikus, lowBattery, [AlertKind.LowBattery]) { History = History(fikus.Id, now, zone, h => 46 + 1.5 * Math.Sin(h / 5), 23, 1) },
            new(cisy, stale, [AlertKind.SensorSilent]) { History = History(cisy.Id, now, zone, h => 52 + h * 0.05, 8, 3, stopHoursAgo: 7) }
        };

        return
        [
            Mark(MailTemplates.Test(to, name, now, zone)),
            Mark(MailTemplates.Alert(notices, to, name, now, zone)),
            Mark(MailTemplates.Report(snapshots, to, name, now, zone, devices: Devices(now)))
        ];
    }

    /// <summary>
    /// Raport w czasie wyciszeń ze strony w chmurze: urlop (wszystkie alerty mailowe wyciszone na 5 dni) i jeden czujnik
    /// wyciszony na 2 dni – do obejrzenia notki i szarej etykiety.
    /// </summary>
    public static MailMessage MutedReport(IReadOnlyList<string> to, string monitorName, DateTimeOffset now, TimeZoneInfo zone)
    {
        var fikus = new SensorConfig { Id = "fikus", Name = "Fikus", AlertsMutedUntil = now.AddDays(2) };
        var papro = new SensorConfig { Id = "paprot", Name = "Paproć w kuchni" };
        var snapshots = new List<SensorSnapshot>
        {
            new(papro, new StoredReading(papro.Id, now.AddMinutes(-2), 64, 23.7m, 88, 2), [])
                { History = History(papro.Id, now, zone, h => 70 - h * 0.1, 22.5, 1.2) },
            new(fikus, new StoredReading(fikus.Id, now.AddMinutes(-9), 46, 22.8m, 14, 1), [AlertKind.LowBattery])
                { History = History(fikus.Id, now, zone, h => 46 + 1.5 * Math.Sin(h / 5), 23, 1) }
        };
        return Mark(MailTemplates.Report(snapshots, to, $"{monitorName} (przykład, wyciszenia)", now, zone, devices: [],
            alertsMutedUntil: now.AddDays(5)));
    }

    /// <summary>Zmyślone urządzenia do sekcji „Urządzenia”: telefon w mrozie ze słabą baterią, Raspberry Pi w normie i
    /// milczący drugi telefon.</summary>
    private static IReadOnlyList<DeviceSnapshot> Devices(DateTimeOffset now) =>
    [
        new("telefon-ogród", new AgentStatus(AppInfo.Version, "android", "ok")
        {
            BatteryPercent = 22, Charging = false, BatteryTemperatureCelsius = -2, ThermalStatus = "none", Network = "wifi", WifiDbm = -64
        }, now.AddMinutes(-1), Silent: false),
        new("pi-strych", new AgentStatus(AppInfo.Version, "linux", "bez Bluetooth") { CpuTemperatureCelsius = 47.2 }, now.AddSeconds(-20), Silent: false),
        new("telefon-salon", new AgentStatus(AppInfo.Version, "android", "ok")
        {
            BatteryPercent = 64, Charging = true, BatteryTemperatureCelsius = 24.5, ThermalStatus = "none", Network = "wifi", WifiDbm = -52
        }, now.AddHours(-3), Silent: true)
    ];

    /// <summary>Przykładowy mail z dziennikiem dla administratora (zmyślony dziennik z wczorajszego dnia, z ostrzeżeniami i błędem).</summary>
    public static MailMessage AdminLog(IReadOnlyList<string> to, string monitorName, DateTimeOffset now, TimeZoneInfo zone)
    {
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime).AddDays(-1);
        var d = day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var log = string.Join('\n',
            $"{d} 00:00:01.120 [INF] Glebosaurus: Monitor „{monitorName}” startuje: 4 czujników, odczyt co 15 min, raporty: 07:00, 19:00.",
            $"{d} 00:00:02.004 [INF] Glebosaurus: Odczyt czujników: Monstera w salonie, Fikus, Paproć w kuchni, Cisy.",
            $"{d} 00:00:21.350 [INF] Glebosaurus: Monstera w salonie: wilgotność 18%, temperatura 21.4 °C, bateria 63%.",
            $"{d} 00:00:34.871 [WRN] Glebosaurus: Cisy: błąd odczytu: Failed to connect: Unreachable",
            $"{d} 00:15:03.002 [INF] Glebosaurus: Odczyt czujników: Cisy.",
            $"{d} 00:15:20.441 [INF] Glebosaurus: Cisy: wilgotność 52%, temperatura 19.5 °C, bateria 71%.",
            $"{d} 07:00:00.310 [WRN] Glebosaurus: Fikus: nie znaleziono w reklamach BLE (poza zasięgiem lub czujnik śpi).",
            $"{d} 07:30:10.010 [ERR] Glebosaurus: Cykl monitora zakończył się błędem; kolejna próba za chwilę.",
            "System.IO.IOException: Unable to read data from the transport connection.",
            "   at MailKit.Net.Smtp.SmtpClient.SendAsync(FormatOptions options, MimeMessage message)",
            $"{d} 12:00:00.100 [WRN] Glebosaurus: Alert „wilgotność gleby 18% (próg 25%)” czeka na wysyłkę, ale poczta nie jest skonfigurowana.",
            $"{d} 23:59:58.870 [INF] Glebosaurus: Monitor zatrzymany.") + "\n";
        return Mark(MailTemplates.AdminLog(log, day, to, $"{monitorName} (przykład)", now, zone));
    }

    /// <summary>Przykładowy mail „agent milczy” – zmyślony agent, cisza od 47 minut.</summary>
    public static MailMessage AgentSilent(IReadOnlyList<string> to, string monitorName, DateTimeOffset now, TimeZoneInfo zone) =>
        Mark(MailTemplates.AgentSilent("telefon-salon", now.AddMinutes(-47), to, $"{monitorName} (przykład)", now, zone));

    /// <summary>
    /// Raport z 16 zmyślonymi czujnikami i pełną historią – do obejrzenia, jak wykresy zachowują się przy wielu czujnikach
    /// (krótszy wykres – 2 doby albo ostatnia doba – lub szersze przedziały, żeby mail z kafelkami zmieścił się w limicie
    /// Gmaila; od ok. 23 czujników raport idzie bez wykresów). Tylko podgląd (<c>mail-preview</c>).
    /// </summary>
    public static MailMessage LargeReport(IReadOnlyList<string> to, string monitorName, DateTimeOffset now, TimeZoneInfo zone)
    {
        string[] names =
        [
            "Monstera", "Fikus", "Paproć", "Cisy", "Zioła", "Bazylia", "Lawenda", "Storczyk", "Kaktus", "Oliwka",
            "Cyprysek", "Zielistka", "Sansewieria", "Skrzydłokwiat", "Begonia", "Pelargonia"
        ];
        var snapshots = names.Select((name, i) =>
        {
            var sensor = new SensorConfig { Id = $"s{i + 1}", Name = name };
            var watered = 15 + 9 * (i % 7);                 // ile godzin temu podlana
            var rate = 0.15 + 0.05 * (i % 4);               // ile punktów procentowych ubywa na godzinę
            var outdoor = i % 3 == 0;
            var history = History(sensor.Id, now, zone, h => Math.Clamp(h < watered ? 70 - (watered - h) * rate : 30 + (h - watered) * rate * 0.5, 5, 95),
                outdoor ? 8 : 22, outdoor ? 3 : 1.2);
            var latest = history.MaxBy(r => r.At);
            var alerts = latest?.MoisturePercent < sensor.WateringBelowPercent ? new[] { AlertKind.NeedsWater } : [];
            return new SensorSnapshot(sensor, latest, alerts) { History = history };
        }).ToList();
        return Mark(MailTemplates.Report(snapshots, to, $"{monitorName} (przykład, 16 czujników)", now, zone));
    }

    /// <summary>Zmyślone odczyty co 15 min z ostatnich 75 godzin: wilgotność według liczby godzin wstecz, temperatura z rytmem dobowym.</summary>
    private static IReadOnlyList<StoredReading> History(
        string sensorId, DateTimeOffset now, TimeZoneInfo zone, Func<double, double> moisture, double meanTemperature, double swing,
        double stopHoursAgo = 0)
    {
        var readings = new List<StoredReading>();
        for (var minutes = 0; minutes <= 75 * 60; minutes += 15)
        {
            var hoursAgo = minutes / 60.0;
            if (hoursAgo < stopHoursAgo) continue;
            var at = now.AddMinutes(-minutes);
            var local = TimeZoneInfo.ConvertTime(at, zone);
            var temperature = meanTemperature + swing * Math.Sin(2 * Math.PI * (local.Hour + local.Minute / 60.0 - 9) / 24);
            var value = moisture(hoursAgo) + 0.6 * Math.Sin(minutes * 1.7);
            readings.Add(new(sensorId, at, (int)Math.Round(value), Math.Round((decimal)temperature, 1), 80, 2));
        }
        return readings;
    }

    private static MailMessage Mark(MailMessage message) => message with { Subject = SubjectPrefix + message.Subject };
}
