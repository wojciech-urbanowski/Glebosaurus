using System.Buffers.Binary;
using System.Globalization;

namespace Glebosaurus.FlowerCare;

/// <summary>
/// Reklama MiBeacon (dane usługi 0xFE95) – po niej rozpoznaje się czujnik Flower Care w nasłuchu. Nagłówek: kontrola ramki
/// (uint16 LE), model (uint16 LE), licznik ramek; dalej opcjonalnie adres MAC (odwrócony), bajt możliwości i jeden obiekt z
/// pomiarem (id uint16 LE, długość, wartość). Flower Care nadaje pomiary jawnie, po jednym w reklamie.
/// </summary>
public sealed record MiBeacon(ushort FrameControl, ushort ProductId, byte FrameCounter, string? Mac, bool Encrypted, ushort? ObjectId, byte[]? ObjectValue)
{
    public bool IsFlowerCare => ProductId == FlowerCareProtocol.ProductId;

    /// <param name="data">Dane usługi 0xFE95 bez UUID usługi (z UUID 95 FE na początku też przejdą).</param>
    public static MiBeacon? TryParse(ReadOnlySpan<byte> data)
    {
        // Niektóre platformy dołączają 16-bitowy UUID usługi (95 FE) na początku danych – rozpoznajemy go po modelu za nim.
        if (data is [0x95, 0xFE, _, _, _, _, ..] && BinaryPrimitives.ReadUInt16LittleEndian(data[4..]) == FlowerCareProtocol.ProductId) data = data[2..];
        if (data.Length < 5) return null;
        var control = BinaryPrimitives.ReadUInt16LittleEndian(data);
        var product = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
        var counter = data[4];
        var rest = data[5..];
        string? mac = null;
        if ((control & 0x0010) != 0)
        {
            if (rest.Length < 6) return new(control, product, counter, null, (control & 0x0008) != 0, null, null);
            var bytes = rest[..6].ToArray();
            Array.Reverse(bytes);
            mac = string.Join(":", bytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
            rest = rest[6..];
        }
        if ((control & 0x0020) != 0 && rest.Length > 0) rest = rest[1..];
        var encrypted = (control & 0x0008) != 0;
        if ((control & 0x0040) == 0 || encrypted || rest.Length < 3) return new(control, product, counter, mac, encrypted, null, null);
        var id = BinaryPrimitives.ReadUInt16LittleEndian(rest);
        var length = rest[2];
        return rest.Length < 3 + length
            ? new(control, product, counter, mac, encrypted, id, null)
            : new(control, product, counter, mac, encrypted, id, rest.Slice(3, length).ToArray());
    }

    /// <summary>Pomiar z reklamy po polsku (do diagnostyki), np. „przewodność 1607 µS/cm”; null – brak albo nieznany obiekt.</summary>
    public string? DescribeObject()
    {
        if (ObjectId is not { } id || ObjectValue is not { } value) return null;
        return (id, value.Length) switch
        {
            (0x1004, 2) => string.Create(CultureInfo.InvariantCulture, $"temperatura {BinaryPrimitives.ReadInt16LittleEndian(value) / 10m} °C"),
            (0x1007, 3) => $"światło {value[0] | value[1] << 8 | value[2] << 16} lx",
            (0x1008, 1) => $"wilgotność {value[0]}%",
            (0x1009, 2) => $"przewodność {BinaryPrimitives.ReadUInt16LittleEndian(value)} µS/cm",
            (0x100A, 1) => $"bateria {value[0]}%",
            _ => $"obiekt 0x{id:X4}: {Convert.ToHexString(value)}"
        };
    }
}

/// <summary>Rozpoznanie czujnika Flower Care w reklamach.</summary>
public static class FlowerCareAdvertising
{
    /// <summary>Nazwa, którą czujnik podaje w reklamie: „Flower care” (także „Flower mate” w części serii).</summary>
    public static bool IsFlowerCareName(string? name) =>
        name is not null && (name.Equals("Flower care", StringComparison.OrdinalIgnoreCase) || name.Equals("Flower mate", StringComparison.OrdinalIgnoreCase));
}
