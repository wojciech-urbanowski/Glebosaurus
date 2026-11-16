using System.Security.Cryptography.X509Certificates;

namespace Glebosaurus.Cloud.Admin;

/// <summary>
/// Certyfikat główny Supabase („Supabase Root 2021 CA”, ważny do 2031-04-26), którym podpisane są certyfikaty serwerów baz
/// i poolerów. Publiczny – pobrany przez użytkownika z panelu projektu (Database → SSL Configuration) 2026-11-16 do
/// <c>supabase/prod-ca-2021.crt</c> i wbudowany w bibliotekę. Odcisk zapisany tu na stałe, żeby podmiana pliku nie przeszła
/// niezauważona.
/// </summary>
public static class SupabaseRootCertificate
{
    public const string Sha256Thumbprint = "807025AD50D4ED219D2C9C7D299C004F824EB00CF7F65AFEF607D07B72E6CAFA";

    public static X509Certificate2 Load()
    {
        using var stream = typeof(SupabaseRootCertificate).Assembly.GetManifestResourceStream("Glebosaurus.Supabase.prod-ca-2021.crt")
            ?? throw new InvalidOperationException("Brak wbudowanego certyfikatu głównego Supabase.");
        using var reader = new StreamReader(stream);
        var certificate = X509Certificate2.CreateFromPem(reader.ReadToEnd());
        var thumbprint = Convert.ToHexString(certificate.GetCertHash(System.Security.Cryptography.HashAlgorithmName.SHA256));
        if (thumbprint != Sha256Thumbprint)
            throw new InvalidOperationException($"Certyfikat główny Supabase ma nieoczekiwany odcisk {thumbprint}.");
        return certificate;
    }
}
