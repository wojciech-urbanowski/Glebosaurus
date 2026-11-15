using System.Globalization;
using System.Text.RegularExpressions;

namespace Glebosaurus.Tuya;

/// <summary>Adres urządzenia BLE jako 48-bitowa liczba; zapis tekstowy AA:BB:CC:DD:EE:FF.</summary>
public static partial class BleAddress
{
    public static bool TryParse(string? value, out ulong address)
    {
        address = 0;
        if (value is null) return false;
        var hex = value.Replace(":", "").Replace("-", "").Replace("_", "");
        return hex.Length == 12 && ulong.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out address);
    }

    /// <summary>
    /// Wyciąga adres z identyfikatora urządzenia nadanego przez platformę BLE: Windows daje GUID z adresem w ostatnich 12
    /// znakach (00000000-0000-0000-0000-dc2351cc03c9), BlueZ ścieżkę D-Bus (…/dev_DC_23_51_CC_03_C9). Szuka od prawej.
    /// </summary>
    public static bool TryExtract(string? text, out ulong address)
    {
        address = 0;
        if (string.IsNullOrEmpty(text)) return false;
        var match = EmbeddedAddress().Match(text);
        return match.Success && TryParse(match.Value, out address);
    }

    [GeneratedRegex("(?:[0-9A-Fa-f]{2}[:_-]?){5}[0-9A-Fa-f]{2}", RegexOptions.RightToLeft)]
    private static partial Regex EmbeddedAddress();

    public static ulong Parse(string value) =>
        TryParse(value, out var address) ? address : throw new ArgumentException("Nieprawidłowy adres BLE. Przykład: DC:23:51:CB:F7:7D.", nameof(value));

    public static string Format(ulong address) =>
        string.Join(":", Convert.FromHexString(address.ToString("X12", CultureInfo.InvariantCulture)).Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
}
