using System.Security.Cryptography.X509Certificates;
using Glebosaurus.Cloud.Admin;

namespace Glebosaurus.Cloud.Tests;

/// <summary>Części biblioteki administratora bez sieci – działają też w CI.</summary>
public sealed class AdminLibraryTests
{
    [Fact]
    public void SupabaseRootCertificate_IsEmbeddedCa_WithKnownThumbprint_AndNotNearExpiry()
    {
        using var certificate = SupabaseRootCertificate.Load();

        Assert.Equal("CN=Supabase Root 2021 CA, O=Supabase Inc, L=New Castle, S=Delware, C=US", certificate.Subject);
        Assert.True(certificate.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority);
        // Ważny do 2031-04-26: na pół roku przed końcem ten test zacznie przypominać o nowym certyfikacie z panelu Supabase.
        Assert.True(certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddDays(180),
            $"Certyfikat główny Supabase wygasa {certificate.NotAfter:yyyy-MM-dd} – pobierz nowy z panelu projektu.");
    }

    [Fact]
    public void Settings_ToString_DoesNotRevealSecrets()
    {
        var path = Path.Combine(Path.GetTempPath(), $"glebosaurus-admin-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            {
              // komentarz jak w prawdziwym pliku
              "supabase": {
                "url": "https://abcdefghijklmnopqrst.supabase.co/",
                "publishableKey": "sb_publishable_przyklad",
                "secretKey": "sb_secret_przyklad",
                "databasePassword": "haslo-przyklad"
              }
            }
            """);
        try
        {
            var settings = CloudAdminSettings.Load(path);

            Assert.Equal("abcdefghijklmnopqrst", settings.ProjectRef);
            Assert.Equal(CloudAdminSettings.DefaultDatabaseHost, settings.DatabaseHost);
            Assert.DoesNotContain("haslo", settings.ToString());
            Assert.DoesNotContain("sb_", settings.ToString());
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("http://abcdefghijklmnopqrst.supabase.co")]
    [InlineData("https://example.com")]
    [InlineData("https://abcdefghijklmnopqrst.supabase.co/rest/v1")]
    public void Settings_RejectWrongProjectUrl(string url)
    {
        var path = Path.Combine(Path.GetTempPath(), $"glebosaurus-admin-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, $$"""{ "supabase": { "url": "{{url}}", "databasePassword": "x" } }""");
        try { Assert.Throws<InvalidDataException>(() => CloudAdminSettings.Load(path)); }
        finally { File.Delete(path); }
    }
}
