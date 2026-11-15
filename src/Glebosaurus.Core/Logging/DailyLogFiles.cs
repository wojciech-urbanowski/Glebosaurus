using System.Globalization;
using System.Text;
using Glebosaurus.Core.Monitoring;

namespace Glebosaurus.Core.Logging;

/// <summary>
/// Pliki dziennika agenta, po jednym na dzień czasu lokalnego (<c>glebosaurus-RRRRMMDD.log</c>). Jedno miejsce, które
/// zna nazewnictwo: zapisuje do nich dostawca dziennika, a silnik czyta z nich wczorajszy dziennik dla administratora i
/// stąd samo usuwanie starych plików (retencja) liczone po dacie z nazwy, nie po czasie modyfikacji. Usługa Windows
/// pisze w tym samym katalogu własne pliki (<c>service-RRRRMMDD.log</c>, <paramref name="name"/> = „service”).
/// </summary>
public sealed class DailyLogFiles(string directory, string name = DailyLogFiles.AgentName) : ILogArchive
{
    public const string AgentName = "glebosaurus";
    public const string ServiceName = "service";

    private readonly string prefix = name + "-";
    private const string Extension = ".log";
    private const string DateFormat = "yyyyMMdd";

    public string DirectoryPath => directory;

    public string PathFor(DateOnly day) =>
        Path.Combine(directory, prefix + day.ToString(DateFormat, CultureInfo.InvariantCulture) + Extension);

    /// <summary>Czyta cały plik z danego dnia (może być w tej chwili dopisywany przez dziennik); null, gdy go nie ma.</summary>
    public async Task<string?> ReadDayAsync(DateOnly day, CancellationToken cancellationToken)
    {
        var path = PathFor(day);
        if (!File.Exists(path)) return null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    /// <summary>
    /// Retencja: usuwa pliki starsze niż <paramref name="keepDays"/> dni względem <paramref name="today"/> (zostaje dzisiejszy
    /// i <paramref name="keepDays"/> poprzednich dni, więc przy 7 dniach plik sprzed siedmiu dni jeszcze jest, sprzed ośmiu już nie).
    /// </summary>
    public int ApplyRetention(DateOnly today, int keepDays) => PurgeBefore(today.AddDays(-keepDays));

    /// <summary>
    /// Usuwa pliki dziennika z dni wcześniejszych niż <paramref name="oldestKept"/> (ten dzień zostaje). Pliki o innych
    /// nazwach zostają nietknięte. Zwraca liczbę usuniętych plików.
    /// </summary>
    public int PurgeBefore(DateOnly oldestKept)
    {
        if (!Directory.Exists(directory)) return 0;
        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(directory, prefix + "*" + Extension))
        {
            if (DayOf(file) is not { } day || day >= oldestKept) continue;
            try
            {
                File.Delete(file);
                deleted++;
            }
            catch (IOException) { /* plik zajęty – usunie go następne czyszczenie */ }
            catch (UnauthorizedAccessException) { }
        }
        return deleted;
    }

    private DateOnly? DayOf(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        return fileName.Length == prefix.Length + DateFormat.Length
            && DateOnly.TryParseExact(fileName[prefix.Length..], DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day : null;
    }
}
