using QRCoder;

namespace Glebosaurus.Agent;

/// <summary>Kod QR odnośnika (rejestracja telefonu – <c>EnrollmentLink</c>, strona właściciela – <c>OwnerWebLink</c>):
/// plik PNG albo znaki w konsoli.</summary>
internal static class EnrollmentQr
{
    public static byte[] Png(string link)
    {
        using var data = Encode(link);
        return new PngByteQRCode(data).GetGraphic(8);
    }

    /// <summary>
    /// Półbloki w konsoli. Odwrócone, bo terminale zwykle mają ciemne tło: znak bloku jest jasny, więc rysujemy nim jasne
    /// moduły – na ekranie wychodzi zwykły kod (ciemny na jasnym), który skanery czytają.
    /// </summary>
    public static string Text(string link)
    {
        using var data = Encode(link);
        return new AsciiQRCode(data).GetGraphicSmall(drawQuietZones: true, invert: true);
    }

    private static QRCodeData Encode(string link)
    {
        using var generator = new QRCodeGenerator();
        return generator.CreateQrCode(link, QRCodeGenerator.ECCLevel.M);
    }
}
