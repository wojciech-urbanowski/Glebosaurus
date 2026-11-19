using Glebosaurus.Core.Cloud;
using Glebosaurus.Core.Logging;
using Glebosaurus.Core.Monitoring;
using Glebosaurus.Tuya;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Glebosaurus.Tests;

/// <summary>
/// Części agenta Android, które żyją w Core (ARCHITEKTURA.md, zasada 12): rejestracja kodem QR, ograniczenie startów skanu,
/// złożenie reklam BLE niezależne od biblioteki, ostatnie wpisy dziennika i tekst ekranu stanu.
/// </summary>
public sealed class AndroidAgentTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private const string Project = "https://abcdefghijklmnopqrst.supabase.co";

    private readonly TempStore db = new();

    public AndroidAgentTests() => db.Store.InitializeAsync(Ct).GetAwaiter().GetResult();

    public void Dispose() => db.Dispose();

    // ---- Kod QR rejestracji --------------------------------------------------------------------------------------------

    [Fact]
    public void EnrollmentLink_RoundTrips_AndHidesCode()
    {
        var link = EnrollmentLink.Create(Project + "/", "sb_publishable_AbC-12_x", "kod+z/znakami=");

        var uri = link.ToUri();
        var parsed = EnrollmentLink.Parse("  " + uri + "\n");

        Assert.StartsWith("glebosaurus://enroll?url=https%3A%2F%2Fabcdefghijklmnopqrst.supabase.co&key=", uri);
        Assert.Equal(link, parsed);
        Assert.Equal("kod+z/znakami=", parsed.Code);
        Assert.Equal(Project, parsed.ToCloudAccess().Url);
        Assert.DoesNotContain("kod", parsed.ToString());
    }

    [Theory]
    [InlineData("https://example.com/", "sb_publishable_x", "abc")]                        // nie projekt Supabase
    [InlineData("http://abcdefghijklmnopqrst.supabase.co", "sb_publishable_x", "abc")]     // bez HTTPS
    [InlineData(Project, "sb_secret_x", "abc")]                                            // klucz tajny na urządzeniu – nigdy
    [InlineData(Project, "sb_publishable_x", "a b")]
    [InlineData(Project, "sb_publishable_x", "")]
    public void EnrollmentLink_WithWrongData_IsRejected(string url, string key, string code)
    {
        Assert.Throws<InvalidDataException>(() => EnrollmentLink.Create(url, key, code));
    }

    [Theory]
    [InlineData("https://abcdefghijklmnopqrst.supabase.co")]
    [InlineData("glebosaurus://enroll?url=https%3A%2F%2Fabcdefghijklmnopqrst.supabase.co&key=sb_publishable_x")]
    [InlineData("WIFI:S:dom;T:WPA;P:haslo;;")]
    public void EnrollmentLink_ForeignOrIncompleteText_IsRejected(string text)
    {
        Assert.Throws<InvalidDataException>(() => EnrollmentLink.Parse(text));
    }

    // ---- Starty skanu ----------------------------------------------------------------------------------------------------

    [Fact]
    public void ScanStarts_FifthWithinWindow_Waits_ThenAllowed()
    {
        var clock = new ManualTimeProvider(Fixtures.T0);
        var limiter = new ScanStartLimiter(clock);

        for (var i = 0; i < ScanStartLimiter.DefaultMaxStarts; i++)
        {
            Assert.Equal(TimeSpan.Zero, limiter.Reserve());
            clock.Advance(TimeSpan.FromSeconds(2));
        }

        // 4 starty w 8 s: piąty dopiero 32 s po pierwszym (Android przy 6. w 30 s po cichu „głuchnie”).
        Assert.Equal(TimeSpan.FromSeconds(24), limiter.Reserve());
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(TimeSpan.Zero, limiter.Reserve());
    }

    [Fact]
    public async Task ScanStarts_WaitTurn_WaitsOutTheWindow()
    {
        var limiter = new ScanStartLimiter(TimeProvider.System, maxStarts: 1, window: TimeSpan.FromMilliseconds(300));
        await limiter.WaitTurnAsync(Ct);
        var started = TimeProvider.System.GetTimestamp();

        await limiter.WaitTurnAsync(Ct);

        Assert.True(TimeProvider.System.GetElapsedTime(started) >= TimeSpan.FromMilliseconds(250));
    }

    // ---- Reklamy BLE --------------------------------------------------------------------------------------------------

    private static readonly byte[] ServiceData = Convert.FromHexString("008A4D0C000BACE5BA");
    private static readonly byte[] Manufacturer = Convert.FromHexString("80030000010062540C6355E6F1F16AABE218B0BCE8A7");

    [Fact]
    public void Advertisement_AssemblesTuyaIdentity_FromSeparatePackets_WithOrWithoutPrefixes()
    {
        var device = new AdvertisedDevice("dc:23:51:cb:f7:7d");
        device.Add(Fixtures.T0, -80, null, true, [0x01, 0xA2, .. ServiceData], null);
        Assert.Null(device.Identity);
        device.Add(Fixtures.T0.AddSeconds(3), 127, "TY", false, null, [0xD0, 0x07, .. Manufacturer]);

        Assert.Equal("DC:23:51:CB:F7:7D", device.Address);
        Assert.Equal("7a84d137ac2085ad", device.Identity!.Uuid);
        Assert.True(device.IsConnectable);                  // odpowiedź na skan „niełączliwa” nie psuje łączliwej reklamy
        Assert.Equal((2, 1, -80), (device.Packets, device.Signal.Count, device.LastRssi));   // 127 = Android nie zna mocy
        Assert.Equal("TY", device.Name);
    }

    [Fact]
    public void Advertisement_MatchesByMac_OrByTuyaUuid_ButNeverNonConnectable()
    {
        var byMac = new AdvertisedDevice("DC:23:51:CC:03:C9");
        byMac.Add(Fixtures.T0, -70, null, true, null, null);
        var byUuid = new AdvertisedDevice("11:22:33:44:55:66");
        byUuid.Add(Fixtures.T0, -70, null, null, ServiceData, Manufacturer);
        var beacon = new AdvertisedDevice("DC:23:51:CC:03:C9");
        beacon.Add(Fixtures.T0, -70, null, false, null, null);

        Assert.True(byMac.Matches(new TuyaCredentials { Mac = "dc-23-51-cc-03-c9", Uuid = "inny" }));
        Assert.True(byUuid.Matches(new TuyaCredentials { Mac = "DC:23:51:CC:03:C9", Uuid = "7a84d137ac2085ad" }));
        Assert.False(byUuid.Matches(new TuyaCredentials { Mac = "DC:23:51:CC:03:C9", Uuid = "inny" }));
        Assert.False(beacon.Matches(new TuyaCredentials { Mac = "DC:23:51:CC:03:C9", Uuid = "inny" }));
    }

    // ---- Ostatnie wpisy dziennika -------------------------------------------------------------------------------------

    [Fact]
    public void RecentLog_KeepsNewestLines_FromInformationUp()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 18, 21, 5, 7, TimeSpan.Zero));
        using var provider = new RecentLogProvider(capacity: 2, clock: clock);
        var logger = provider.CreateLogger("Glebosaurus");

        logger.LogDebug("szczegół");
        logger.LogInformation("pierwszy");
        logger.LogWarning("drugi {Sensor}", "Cisy");
        logger.LogError(new TimeoutException("cisza"), "trzeci");

        Assert.Equal(["21:05:07 WRN drugi Cisy", "21:05:07 ERR trzeci (TimeoutException: cisza)"], provider.Lines());
    }

    // ---- Ekran stanu ----------------------------------------------------------------------------------------------------

    private async Task<MonitorStatus> StatusAsync(Func<CloudSyncState, CloudSyncState>? sync = null)
    {
        var clock = new ManualTimeProvider(Fixtures.T0);
        var agentConfig = Fixtures.Config(Fixtures.Sensor("s1", "Cisy"), Fixtures.Sensor("s2", "Zioła", uuid: "07aaad78f40b0a75"));
        var config = new CloudAgentConfig(agentConfig, TimeZoneInfo.Utc, 7, new("mi11", false, null), null, []);
        await db.Store.AddReadingAsync(Fixtures.Reading("s1", Fixtures.T0.AddMinutes(-12), moisture: 54, temperature: 12.3m, battery: 40), Ct);
        var state = CloudSyncState.NotStarted with
        {
            LastSuccessAt = Fixtures.T0.AddSeconds(-20), ConfigVersion = 7,
            View = new(Fixtures.T0, new("mi11", false, null),
                [new("s1", "mi11", 1500, new OtherHearing("bestia5", -81, 40)), new("s2", "bestia5", 600, null)],
                new("bestia5", 3, 240), [new("bestia5", true, 2, 30), new("mi11", false, null, 0)])
        };
        state = sync?.Invoke(state) ?? state;
        var collector = MonitorRuntimeState.NotStarted with
        {
            Sensors = new Dictionary<string, SensorRuntimeState>
            {
                ["s1"] = new(Fixtures.T0.AddMinutes(-12), SensorReadFailure.None, null, 0, null, -72, Fixtures.T0.AddMinutes(-12))
            }
        };
        var panel = new CloudPanel("abcdefghijklmnopqrst.supabase.co", () => config, db.Store, new FakeCloudApi(clock), () => collector,
            () => state, () => MailRuntimeState.Unused, null, () => { }, clock, NullLogger.Instance);
        return await panel.StatusAsync(new("Android", "", "", ""), Ct);
    }

    private static string Text(IReadOnlyList<ScreenSection> sections) =>
        string.Join("\n", sections.Select(s => $"[{s.Title}]\n{string.Join("\n", s.Lines)}"));

    [Fact]
    public async Task Screen_ShowsWhoReadsEachSensor_LatestReading_AndPhone()
    {
        var status = await StatusAsync();
        var phone = new AgentStatus("0.2.1", "android", "ok")
        {
            BatteryPercent = 100, Charging = true, BatteryTemperatureCelsius = 31.5, ThermalStatus = "none", Network = "wifi", WifiDbm = -55
        };

        var sections = AgentScreenText.Build(status, phone, TimeZoneInfo.Utc);
        var text = Text(sections);

        Assert.Contains("„mi11” w abcdefghijklmnopqrst.supabase.co – tylko zbiera odczyty", text);
        Assert.Contains("Synchronizacja: 05:59 (przed chwilą)", text);
        Assert.Contains("Maile wysyła: bestia5", text);
        Assert.Contains("[Telefon]\nBateria: 100 % · na ładowarce\nTemperatura: 31,5 °C (akumulator) · system: w normie\n" +
            "Sieć: Wi-Fi -55 dBm (dobry)\nBluetooth: ok\n", text);
        Assert.Contains("Cisy – czyta ten telefon (dzierżawa do 06:25)", text);
        Assert.Contains("   54 % · 12,3 °C · bateria 40 % (12 min temu)", text);
        Assert.Contains("słyszę -72 dBm (12 min temu); bestia5: -81 dBm", text);
        Assert.Contains("Zioła – czyta: bestia5", text);
        Assert.Contains("   brak odczytów", text);
        Assert.Contains("mi11 (ten telefon) – zbieracz, czujników: 1, kontakt przed chwilą", text);
        Assert.DoesNotContain(sections, s => s.Warning);
    }

    [Fact]
    public async Task Screen_WarnsFirst_WhenRevoked_OrPhoneHotAndLowOnBattery()
    {
        var status = await StatusAsync(s => s with { Revoked = true });
        var phone = new AgentStatus("0.2.1", "android") { BatteryPercent = 25, Charging = false, BatteryTemperatureCelsius = 46, ThermalStatus = "severe" };

        var sections = AgentScreenText.Build(status, phone, TimeZoneInfo.Utc);

        Assert.Equal("Agent odcięty", sections[0].Title);
        Assert.True(sections[0].Warning);
        var telefon = Assert.Single(sections, s => s.Title == "Telefon");
        Assert.True(telefon.Warning);
        Assert.Contains(telefon.Lines, l => l.StartsWith("Akumulator gorący (46,0 °C)", StringComparison.Ordinal));
        Assert.Contains("Telefon się przegrzewa (system: mocno ogranicza wydajność).", telefon.Lines);
        Assert.Contains("Słaba bateria (25 %) – podłącz telefon do ładowarki.", telefon.Lines);
    }

    // ---- Stan urządzenia (ekran telefonu i panel WWW innego agenta) --------------------------------------------------------

    [Fact]
    public void Device_DescribesOnlyWhatTheAgentReported()
    {
        var windows = AgentDevice.Describe(new AgentStatus("0.2.3", "windows", "Shiny (WinRT)"));
        Assert.Equal("Glebosaurus 0.2.3 · Windows", windows.Program);
        Assert.Equal([new DeviceDetail("Bluetooth", "Shiny (WinRT)")], windows.Details);
        Assert.Empty(windows.Warnings);

        // Telefon 0.2.2 wysyła siłę Wi-Fi bez rodzaju sieci i bez stanu cieplnego.
        var old = AgentDevice.Describe(new AgentStatus("0.2.2", "android", "ok") { BatteryPercent = 80, Charging = true, BatteryTemperatureCelsius = 30, WifiDbm = -66 });
        Assert.Equal("Glebosaurus 0.2.2 · Android", old.Program);
        Assert.Equal([new("Bateria", "80 % · na ładowarce"), new("Temperatura", "30,0 °C (akumulator)"), new DeviceDetail("Sieć", "Wi-Fi -66 dBm (średni)"),
            new("Bluetooth", "ok")], old.Details);
        Assert.Empty(old.Warnings);
    }

    [Fact]
    public void Device_WarnsWhenPhoneIsHot_LowOnBattery_OrOffWifi()
    {
        var cellular = AgentDevice.Describe(new AgentStatus("0.2.3", "android") { Network = "cellular", ThermalStatus = "light", BatteryTemperatureCelsius = 44.9 });
        Assert.Equal([new("Temperatura", "44,9 °C (akumulator) · system: ciepły"), new DeviceDetail("Sieć", "sieć komórkowa (bez Wi-Fi)")], cellular.Details);
        Assert.Equal(["Bez Wi-Fi – telefon wysyła dane przez sieć komórkową."], cellular.Warnings);

        var hot = AgentDevice.Describe(new AgentStatus("0.2.3", "android")
            { ThermalStatus = "moderate", Network = "wifi", WifiDbm = -75, BatteryPercent = 12, Charging = false });
        Assert.Equal(["Telefon się przegrzewa (system: ogranicza wydajność).", "Słaba bateria (12 %) – podłącz telefon do ładowarki."], hot.Warnings);
        Assert.Contains(new DeviceDetail("Sieć", "Wi-Fi -75 dBm (słaby)"), hot.Details);
    }

    [Theory]
    [InlineData(45.0, "Akumulator gorący (45,0 °C) – przenieś telefon w chłodniejsze miejsce.")]
    [InlineData(44.9, null)]
    [InlineData(0.0, null)]
    [InlineData(-0.1, "Akumulator zmarznięty (-0,1 °C) – w mrozie się nie ładuje, telefon może się wyłączyć.")]
    [InlineData(-12.5, "Akumulator zmarznięty (-12,5 °C) – w mrozie się nie ładuje, telefon może się wyłączyć.")]
    public void Device_WarnsAboutHeatAndFrost_ByBatteryTemperature(double celsius, string? warning)
    {
        var device = AgentDevice.Describe(new AgentStatus("0.2.4", "android") { BatteryTemperatureCelsius = celsius, Network = "wifi" });

        string[] expected = warning is null ? [] : [warning];
        Assert.Equal(expected, device.Warnings);
    }

    [Theory]
    [InlineData(80, false, false)]   // telefon na baterii – normalna rzecz
    [InlineData(30, false, false)]
    [InlineData(29, false, true)]
    [InlineData(29, null, true)]     // bez informacji o ładowarce – jak bez niej
    [InlineData(5, true, false)]     // na ładowarce słaba bateria się ładuje
    public void Device_WarnsAboutBattery_OnlyBelow30PercentWithoutCharger(int percent, bool? charging, bool warns)
    {
        var device = AgentDevice.Describe(new AgentStatus("0.2.4", "android") { BatteryPercent = percent, Charging = charging, Network = "wifi" });

        string[] expected = warns ? [$"Słaba bateria ({percent} %) – podłącz telefon do ładowarki."] : [];
        Assert.Equal(expected, device.Warnings);
    }

    [Fact]
    public void Device_ThermalStatusNames_FollowAndroidCodes()
    {
        Assert.Equal(["none", "light", "moderate", "severe", "critical", "emergency", "shutdown"],
            Enumerable.Range(0, 7).Select(AgentDevice.ThermalStatusName).ToList());
        Assert.Null(AgentDevice.ThermalStatusName(7));
        Assert.Null(AgentDevice.ThermalStatusName(-1));
        Assert.Equal(("dobry", "średni", "słaby"), (AgentDevice.WifiQuality(-60), AgentDevice.WifiQuality(-70), AgentDevice.WifiQuality(-71)));
    }

    [Fact]
    public async Task Screen_SyncFailures_ExplainThatReadingsWait()
    {
        var status = await StatusAsync(s => s with { ConsecutiveFailures = 3, LastError = "brak sieci", UnsentReadings = 12 });

        var text = Text(AgentScreenText.Build(status, null, TimeZoneInfo.Utc));

        Assert.Contains("Synchronizacja nieudana 3× z rzędu: brak sieci", text);
        Assert.Contains("Odczyty czekają w telefonie", text);
    }
}
