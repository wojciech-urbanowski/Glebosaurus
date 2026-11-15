using System.Security.Cryptography;
using System.Text;

namespace Glebosaurus.Tuya;

/// <summary>Tożsamość czujnika odczytana z reklamy BLE Tuya (usługa 0xA201 + dane producenta 0x07D0).</summary>
/// <remarks>
/// Niesparowany czujnik ujawnia w reklamie product_id jako ASCII; sparowany ukrywa go (bajty nie są ASCII), ale 16-znakowy
/// UUID urządzenia da się odczytać zawsze – dlatego po nim dopasowujemy czujnik do poświadczeń, niezależnie od formatu
/// adresu zwracanego przez daną platformę BLE.
/// </remarks>
public sealed record TuyaIdentity(string Uuid, string? ProductId, bool IsBound, int ProtocolMajor)
{
    public const string ServiceUuid = "0000a201-0000-1000-8000-00805f9b34fb";
    public const ushort CompanyId = 0x07D0;

    /// <param name="serviceData">Dane usługi 0xA201 bez 2-bajtowego UUID: bajt 0 == 0, dalej bajty product_id.</param>
    /// <param name="manufacturerData">Dane producenta 0x07D0 bez ID firmy: 22 bajty (flagi, wersja, zaszyfrowany UUID).</param>
    public static TuyaIdentity? TryParse(ReadOnlySpan<byte> serviceData, ReadOnlySpan<byte> manufacturerData)
    {
        if (serviceData.Length < 2 || serviceData[0] != 0 || manufacturerData.Length != 22) return null;
        var product = serviceData[1..];
        var key = MD5.HashData(product); // MD5 wynika z protokołu Tuya.
        using var aes = Aes.Create();
        aes.Key = key;
        var uuid = aes.DecryptCbc(manufacturerData[6..], key, PaddingMode.None);
        if (uuid.Any(b => b < 32 || b >= 127)) return null;
        var readable = product.ToArray().All(b => char.IsAsciiLetterOrDigit((char)b));
        return new(Encoding.ASCII.GetString(uuid), readable ? Encoding.ASCII.GetString(product) : null,
            (manufacturerData[0] & 0x80) != 0, manufacturerData[1]);
    }
}
