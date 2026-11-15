using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Glebosaurus.Web;

/// <summary>
/// Certyfikat HTTPS panelu z podpisem własnym (ECDSA P-256), tworzony przy pierwszym uruchomieniu i trzymany w pliku PFX
/// obok konfiguracji. Przeglądarka ostrzeże o nim raz; użytkownik porównuje odcisk SHA-256 z tym w dzienniku albo z
/// wypisanym przez <c>web-password</c>. Ważność 825 dni (najdłuższa, jaką akceptują systemy Apple, gdyby ktoś dodał go do
/// zaufanych na telefonie); 30 dni przed końcem agent tworzy nowy (z nowym odciskiem).
/// </summary>
public static class PanelCertificate
{
    public static readonly TimeSpan Validity = TimeSpan.FromDays(825);
    public static readonly TimeSpan RenewBefore = TimeSpan.FromDays(30);

    private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";

    /// <returns>Certyfikat z kluczem prywatnym i informacja, czy właśnie powstał (nowy odcisk do pokazania użytkownikowi).</returns>
    public static (X509Certificate2 Certificate, bool Created) LoadOrCreate(string path, string monitorName, DateTimeOffset now)
    {
        if (File.Exists(path))
        {
            try
            {
                var existing = X509CertificateLoader.LoadPkcs12FromFile(path, null);
                if (existing.HasPrivateKey && existing.NotAfter.ToUniversalTime() - RenewBefore > now.UtcDateTime) return (existing, false);
                existing.Dispose();
            }
            catch (CryptographicException) { /* uszkodzony plik – tworzymy nowy certyfikat */ }
        }

        using var created = Create(monitorName, now);
        var pfx = created.Export(X509ContentType.Pfx);
        PrivateFile.Write(path, pfx);
        // Wczytanie z PFX zamiast użycia „created” wprost: na Windows SChannel nie przyjmuje klucza efemerycznego.
        return (X509CertificateLoader.LoadPkcs12(pfx, null), true);
    }

    /// <summary>Odcisk SHA-256 w postaci, jaką pokazują przeglądarki: „AB:CD:…”.</summary>
    public static string Fingerprint(X509Certificate2 certificate) =>
        string.Join(':', certificate.GetCertHash(HashAlgorithmName.SHA256).Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));

    internal static X509Certificate2 Create(string monitorName, DateTimeOffset now)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var name = new X500DistinguishedName($"CN={Sanitize($"Glebosaurus {monitorName}")}");
        var request = new CertificateRequest(name, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(ServerAuthOid)], false));

        var names = new SubjectAlternativeNameBuilder();
        foreach (var dns in new[] { "localhost", Environment.MachineName, Environment.MachineName.ToLowerInvariant() + ".local" }.Distinct(StringComparer.OrdinalIgnoreCase))
            names.AddDnsName(dns);
        foreach (var address in LocalAddresses().Append(IPAddress.Loopback).Append(IPAddress.IPv6Loopback).Distinct())
            names.AddIpAddress(address);
        request.CertificateExtensions.Add(names.Build());

        return request.CreateSelfSigned(now.AddMinutes(-5), now + Validity);
    }

    /// <summary>Adresy IPv4 tego komputera w sieciach lokalnych (do certyfikatu i do podpowiedzi adresu panelu).</summary>
    public static IReadOnlyList<IPAddress> LocalAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                .Distinct()
                .ToList();
        }
        catch (NetworkInformationException) { return []; }
    }

    private static string Sanitize(string text) => new(text.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.').ToArray());
}
