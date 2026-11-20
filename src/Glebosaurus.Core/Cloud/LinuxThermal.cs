using System.Globalization;

namespace Glebosaurus.Core.Cloud;

/// <summary>
/// Temperatura procesora agenta na Linuksie (Raspberry Pi) do sygnału życia (<see cref="AgentStatus.CpuTemperatureCelsius"/>):
/// strefy cieplne jądra w <c>/sys/class/thermal/thermal_zone*/</c> – plik <c>type</c> z nazwą strefy i <c>temp</c> w
/// tysięcznych częściach stopnia. Na Raspberry Pi OS strefa procesora to <c>cpu-thermal</c>. Pliki czyta każdy użytkownik,
/// więc usługa nie potrzebuje uprawnień (w przeciwieństwie do <c>vcgencmd</c>).
/// </summary>
public static class LinuxThermal
{
    public const string DefaultRoot = "/sys/class/thermal";

    /// <summary>Nazwy stref procesora w kolejności pierwszeństwa (Raspberry Pi, inne płytki ARM, Intel).</summary>
    private static readonly string[] CpuZoneTypes = ["cpu-thermal", "cpu_thermal", "soc_thermal", "x86_pkg_temp"];

    /// <summary>Temperatura procesora albo null (brak stref, brak dostępu, wartość spoza −50…150 °C). Nigdy nie rzuca –
    /// wołana przy każdej synchronizacji, a jej brak nie może jej zatrzymać.</summary>
    /// <param name="root">Katalog stref cieplnych (testy podstawiają własny).</param>
    public static double? CpuTemperatureCelsius(string root = DefaultRoot)
    {
        try
        {
            if (!Directory.Exists(root)) return null;
            var zones = Directory.GetDirectories(root, "thermal_zone*")
                .Select(path => (Path: path, Number: ZoneNumber(path), Type: ReadText(Path.Combine(path, "type"))))
                .Where(z => z.Number >= 0)
                .OrderBy(z => Rank(z.Type))
                .ThenBy(z => z.Number);
            foreach (var zone in zones)
                if (ReadCelsius(Path.Combine(zone.Path, "temp")) is { } celsius) return celsius;
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Strefa procesora z listy – według kolejności na liście; inne strefy (np. <c>acpitz</c>) – po nich, od
    /// najniższego numeru.</summary>
    private static int Rank(string? type)
    {
        var index = type is null ? -1 : Array.IndexOf(CpuZoneTypes, type);
        return index >= 0 ? index : CpuZoneTypes.Length;
    }

    private static int ZoneNumber(string path) =>
        int.TryParse(Path.GetFileName(path)["thermal_zone".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number : -1;

    private static string? ReadText(string path)
    {
        try { return File.ReadAllText(path).Trim(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static double? ReadCelsius(string path) =>
        int.TryParse(ReadText(path), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var milli)
        && milli is >= -50_000 and <= 150_000
            ? Math.Round(milli / 1000.0, 1)
            : null;
}
