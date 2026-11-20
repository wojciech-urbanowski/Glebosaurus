using Glebosaurus.Core.Cloud;

namespace Glebosaurus.Tests;

/// <summary>Raspberry Pi w sygnale życia (temperatura procesora) i wybór urządzeń do sekcji „Urządzenia” raportu.</summary>
public sealed class AgentDeviceTests : IDisposable
{
    private static readonly DateTimeOffset ServerTime = new(2026, 11, 20, 6, 0, 0, TimeSpan.Zero);
    private readonly DirectoryInfo thermal = Directory.CreateTempSubdirectory("glebosaurus-thermal-");

    public void Dispose() => thermal.Delete(recursive: true);

    // ---- Opis i ostrzeżenia ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(74.9, "74,9 °C", null)]
    [InlineData(75.0, "75,0 °C", "Procesor gorący (75,0 °C) – przy ok. 80 °C zwalnia; zapewnij mu chłodzenie.")]
    [InlineData(-15.0, "-15,0 °C", null)]   // Pi nie ma akumulatora – zimno mu nie szkodzi
    public void Pi_WarnsOnlyWhenProcessorIsHot(double celsius, string text, string? warning)
    {
        var device = AgentDevice.Describe(new AgentStatus("0.2.6", "linux", "wyłączony") { CpuTemperatureCelsius = celsius });

        string[] expected = warning is null ? [] : [warning];
        Assert.Equal(expected, device.Warnings);
        Assert.Contains(new DeviceDetail("Temperatura", $"{text} (procesor)"), device.Details);
        Assert.Equal("Glebosaurus 0.2.6 · Linux", device.Program);
    }

    [Fact]
    public void Kind_PhoneOrRaspberryPi()
    {
        Assert.Equal(("telefon", "Raspberry Pi", "Windows"), (AgentDevice.Kind("android"), AgentDevice.Kind("linux"), AgentDevice.Kind("windows")));
    }

    // ---- Urządzenia do raportu -------------------------------------------------------------------------------------

    [Fact]
    public void ForReport_TakesPhonesAndPis_WithTheirLastContact_AndMarksSilentOnes()
    {
        var phone = new AgentStatus("0.2.3", "android", "ok") { BatteryPercent = 64, BatteryTemperatureCelsius = 18.5 };
        var pi = new AgentStatus("0.2.6", "linux") { CpuTemperatureCelsius = 47.2 };
        AgentView[] agents =
        [
            new("bestia5", true, 2, 20) { Status = new("0.2.6", "windows", "Shiny (WinRT)") },   // komputer – bez baterii i temperatury
            new("mi11", false, null, 60) { Status = phone },
            new("rasp3", true, 1, 0) { Status = pi },
            new("stary", false, null, 3 * 3600) { Status = phone },
            new("nowy", false, null, 30),                                                     // bez sygnału życia
            new("nigdy", false, null, null) { Status = phone }                                // bez synchronizacji
        ];

        var devices = AgentDevice.ForReport(agents, ServerTime, TimeSpan.FromMinutes(30));

        Assert.Equal(["mi11", "rasp3", "stary"], devices.Select(d => d.Name));
        Assert.Equal((phone, ServerTime.AddMinutes(-1), false), (devices[0].Status, devices[0].LastSeenAt, devices[0].Silent));
        Assert.Equal((pi, ServerTime, false), (devices[1].Status, devices[1].LastSeenAt, devices[1].Silent));
        Assert.Equal((ServerTime.AddHours(-3), true), (devices[2].LastSeenAt, devices[2].Silent));
    }

    // ---- Temperatura procesora na Linuksie ---------------------------------------------------------------------------

    private void Zone(int number, string? type, string? temp)
    {
        var zone = thermal.CreateSubdirectory($"thermal_zone{number}");
        if (type is not null) File.WriteAllText(Path.Combine(zone.FullName, "type"), type + "\n");
        if (temp is not null) File.WriteAllText(Path.Combine(zone.FullName, "temp"), temp + "\n");
    }

    [Fact]
    public void Thermal_ReadsRaspberryPiZone_InMillidegrees()
    {
        Zone(0, "cpu-thermal", "47236");

        Assert.Equal(47.2, LinuxThermal.CpuTemperatureCelsius(thermal.FullName));
    }

    [Fact]
    public void Thermal_PrefersProcessorZone_OverOtherZones()
    {
        Zone(0, "acpitz", "27800");
        Zone(1, "x86_pkg_temp", "55000");
        Zone(10, "iwlwifi_1", "40000");

        Assert.Equal(55.0, LinuxThermal.CpuTemperatureCelsius(thermal.FullName));
    }

    [Fact]
    public void Thermal_FallsBackToLowestZone_AndSkipsUnreadableValues()
    {
        Zone(2, "acpitz", "31000");
        Zone(1, "cpu-thermal", "nie liczba");    // strefa procesora bez wartości – zapasowo inna
        Zone(3, "acpitz", "29000");

        Assert.Equal(31.0, LinuxThermal.CpuTemperatureCelsius(thermal.FullName));
    }

    [Theory]
    [InlineData("200000")]     // poza zakresem
    [InlineData("-60000")]
    [InlineData("")]
    [InlineData(null)]          // brak pliku temp
    public void Thermal_NullWhenNoSensibleValue(string? temp)
    {
        Zone(0, "cpu-thermal", temp);

        Assert.Null(LinuxThermal.CpuTemperatureCelsius(thermal.FullName));
    }

    [Fact]
    public void Thermal_NullWithoutThermalZones() =>
        Assert.Null(LinuxThermal.CpuTemperatureCelsius(Path.Combine(thermal.FullName, "brak")));
}
