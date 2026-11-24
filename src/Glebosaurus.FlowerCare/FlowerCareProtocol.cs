using System.Buffers.Binary;
using System.Text;

namespace Glebosaurus.FlowerCare;

/// <summary>Jeden pomiar czujnika Flower Care (bieżący albo z historii).</summary>
/// <param name="TemperatureTenths">Temperatura w dziesiątych częściach °C – czujnik jest w główce nad ziemią (temperatura przy roślinie).</param>
/// <param name="LightLux">Natężenie światła w luksach.</param>
/// <param name="MoisturePercent">Wilgotność gleby w procentach.</param>
/// <param name="ConductivityMicroSiemens">Przewodność gleby („żyzność”) w µS/cm.</param>
public sealed record FlowerCareValues(int TemperatureTenths, int LightLux, int MoisturePercent, int ConductivityMicroSiemens)
{
    /// <summary>Zakresy, poza którymi wartość jest błędem odczytu, nie pomiarem (te same co w bazie chmury).</summary>
    public const int MinTemperatureTenths = -400, MaxTemperatureTenths = 1000, MaxLightLux = 200_000, MaxConductivity = 20_000;

    /// <summary>null – wartości mieszczą się w zakresach; inaczej opis pierwszej złej.</summary>
    public string? Problem() =>
        TemperatureTenths is < MinTemperatureTenths or > MaxTemperatureTenths ? $"temperatura {TemperatureTenths / 10m} °C poza zakresem"
        : LightLux is < 0 or > MaxLightLux ? $"światło {LightLux} lx poza zakresem"
        : MoisturePercent is < 0 or > 100 ? $"wilgotność {MoisturePercent}% poza zakresem"
        : ConductivityMicroSiemens is < 0 or > MaxConductivity ? $"przewodność {ConductivityMicroSiemens} µS/cm poza zakresem"
        : null;
}

/// <summary>Rekord z pamięci czujnika: czas w sekundach od startu czujnika (włożenia baterii) i pomiar.</summary>
public sealed record FlowerCareHistoryRecord(int Index, uint SecondsSinceBoot, FlowerCareValues Values, byte[] Raw);

/// <summary>
/// Protokół GATT czujnika Xiaomi / HHCC Flower Care (HHCCJCY01, „Mi Flora”). Bez parowania, klucza i szyfrowania – czujnik
/// rozpoznaje się po adresie MAC. Układ bajtów według opisów protokołu z projektów basnijholt/miflora i
/// vrachieru/xiaomi-flower-care-api (MIT) oraz wiki ChrisScheffler/miflora; własna implementacja (ARCHITEKTURA.md).
/// </summary>
public static class FlowerCareProtocol
{
    private const string Base = "-0000-1000-8000-00805f9b34fb";

    /// <summary>Usługa danych: polecenia, pomiar bieżący, bateria i firmware.</summary>
    public const string DataService = "00001204" + Base;

    /// <summary>Polecenia (zapis): <see cref="RealtimeModeCommand"/>, <see cref="BlinkCommand"/>.</summary>
    public const string CommandCharacteristic = "00001a00" + Base;

    /// <summary>Pomiar bieżący, 16 B (po <see cref="RealtimeModeCommand"/>).</summary>
    public const string RealtimeCharacteristic = "00001a01" + Base;

    /// <summary>Bateria (bajt 0, %) i wersja firmware (bajty 2–6, ASCII).</summary>
    public const string FirmwareCharacteristic = "00001a02" + Base;

    /// <summary>Usługa historii: sterowanie, rekordy, zegar czujnika.</summary>
    public const string HistoryService = "00001206" + Base;

    /// <summary>Sterowanie historią (zapis): <see cref="HistoryModeCommand"/>, <see cref="HistoryEntryCommand"/>.</summary>
    public const string HistoryControlCharacteristic = "00001a10" + Base;

    /// <summary>Po <see cref="HistoryModeCommand"/> liczba rekordów, po <see cref="HistoryEntryCommand"/> rekord (16 B).</summary>
    public const string HistoryDataCharacteristic = "00001a11" + Base;

    /// <summary>Zegar czujnika: sekundy od startu (uint32 LE).</summary>
    public const string DeviceTimeCharacteristic = "00001a12" + Base;

    /// <summary>Usługa w reklamach (MiBeacon) – po niej i po <see cref="ProductId"/> rozpoznaje się czujnik.</summary>
    public const string MiBeaconService = "0000fe95" + Base;

    /// <summary>Model w nagłówku MiBeacon: 0x0098 – Flower Care / Mi Flora (HHCCJCY01).</summary>
    public const ushort ProductId = 0x0098;

    /// <summary>Przełącza czujnik w tryb odczytu bieżącego; bez tego <see cref="RealtimeCharacteristic"/> zwraca stały wzór.</summary>
    public static byte[] RealtimeModeCommand => [0xA0, 0x1F];

    /// <summary>Jedno mrugnięcie diody.</summary>
    public static byte[] BlinkCommand => [0xFD, 0xFF];

    /// <summary>Tryb historii; potem <see cref="HistoryDataCharacteristic"/> zwraca liczbę rekordów.</summary>
    public static byte[] HistoryModeCommand => [0xA0, 0x00, 0x00];

    /// <summary>Prośba o rekord historii o numerze <paramref name="index"/> (uint16 LE).</summary>
    public static byte[] HistoryEntryCommand(int index) =>
        index is >= 0 and <= ushort.MaxValue ? [0xA1, (byte)index, (byte)(index >> 8)] : throw new ArgumentOutOfRangeException(nameof(index));

    /// <summary>Wzór, który czujnik zwraca zamiast pomiaru, gdy nie jest w trybie odczytu.</summary>
    private static ReadOnlySpan<byte> NotInRealtimeMode => [0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x99, 0x88, 0x77, 0x66];

    /// <summary>
    /// Pomiar bieżący (16 B): temperatura int16 LE ×0,1 °C (0–1), bajt 2 nieznany, światło uint32 LE w lx (3–6), wilgotność
    /// u8 % (7), przewodność uint16 LE µS/cm (8–9), reszta nieznana.
    /// </summary>
    public static FlowerCareValues ParseRealtime(ReadOnlySpan<byte> data)
    {
        if (data.Length < 10) throw new InvalidDataException($"Pomiar bieżący ma {data.Length} B (oczekiwano 16).");
        if (data[..10].SequenceEqual(NotInRealtimeMode))
            throw new InvalidDataException("Czujnik nie przełączył się w tryb odczytu (zwrócił wzór AABBCCDDEEFF99887766).");
        return new(BinaryPrimitives.ReadInt16LittleEndian(data),
            (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data[3..]), int.MaxValue),
            data[7], BinaryPrimitives.ReadUInt16LittleEndian(data[8..]));
    }

    /// <summary>Bateria i firmware (7 B): bateria % (0), separator (1), wersja ASCII (2–6), np. „3.3.5”.</summary>
    public static (int BatteryPercent, string Firmware) ParseFirmware(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2) throw new InvalidDataException($"Charakterystyka baterii i firmware ma {data.Length} B (oczekiwano 7).");
        var firmware = Encoding.ASCII.GetString(data[2..]).TrimEnd('\0').Trim();
        return (data[0], firmware.All(c => c is >= ' ' and <= '~') ? firmware : Convert.ToHexString(data[2..]));
    }

    /// <summary>Liczba rekordów historii (uint16 LE na początku odpowiedzi po <see cref="HistoryModeCommand"/>).</summary>
    public static int ParseHistoryCount(ReadOnlySpan<byte> data) =>
        data.Length >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(data) : throw new InvalidDataException($"Liczba rekordów historii ma {data.Length} B.");

    /// <summary>Zegar czujnika: sekundy od startu (uint32 LE).</summary>
    public static uint ParseDeviceTime(ReadOnlySpan<byte> data) =>
        data.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(data) : throw new InvalidDataException($"Zegar czujnika ma {data.Length} B.");

    /// <summary>
    /// Rekord historii (16 B): czas uint32 LE w sekundach od startu czujnika (0–3), temperatura int16 LE ×0,1 °C (4–5),
    /// bajt 6 nieznany, światło uint32 LE w lx (7–10), wilgotność u8 % (11), przewodność uint16 LE µS/cm (12–13).
    /// </summary>
    public static FlowerCareHistoryRecord ParseHistoryRecord(int index, ReadOnlySpan<byte> data)
    {
        if (data.Length < 14) throw new InvalidDataException($"Rekord historii {index} ma {data.Length} B (oczekiwano 16).");
        var values = new FlowerCareValues(BinaryPrimitives.ReadInt16LittleEndian(data[4..]),
            (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data[7..]), int.MaxValue),
            data[11], BinaryPrimitives.ReadUInt16LittleEndian(data[12..]));
        return new(index, BinaryPrimitives.ReadUInt32LittleEndian(data), values, data.ToArray());
    }
}
