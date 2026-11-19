using Glebosaurus.Cloud.Admin;
using Glebosaurus.Core.Config;

namespace Glebosaurus.Tests;

/// <summary>Wzory z <c>examples/</c> wczytują się tymi samymi loaderami co prawdziwe pliki – dokumentacja nie rozjedzie się z kodem.</summary>
public sealed class ExamplesTests : IDisposable
{
    private readonly DirectoryInfo temp = Directory.CreateTempSubdirectory("glebosaurus-examples-");

    public void Dispose() => temp.Delete(recursive: true);

    private static string Example(string name)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Glebosaurus.slnx")))
                return Path.Combine(directory.FullName, "examples", name);
        throw new DirectoryNotFoundException("Nie znaleziono katalogu repozytorium (Glebosaurus.slnx).");
    }

    [Fact]
    public void AgentExample_IsAValidAgentFile()
    {
        var config = AgentConfigLoader.Load(Example("glebosaurus.config.json"));

        Assert.Equal("abcdefghijklmnopqrst.supabase.co", config.Cloud!.ProjectUrl.Host);
        Assert.Null(config.Cloud.Backup);
        Assert.Null(config.Web);
        Assert.True(config.Bluetooth);
    }

    [Fact]
    public void CloudSetupExample_IsValid_WithTheKeyFileFromPair()
    {
        var path = Path.Combine(temp.FullName, "cloud-config.json");
        File.Copy(Example("cloud-config.json"), path);
        File.WriteAllText(Path.Combine(temp.FullName, "monstera.credentials.json"),
            """{ "mac": "DC:23:51:CC:03:C9", "uuid": "07aaad78f40b0a74", "deviceId": "1234567890123456789012", "localKeyHex": "a1b2c3d4e5f6", "productId": "gvygg3m8" }""");

        var setup = CloudSetupLoader.Load(path);

        Assert.Equal("Europe/Warsaw", setup.TimeZone);
        Assert.Equal("monstera", Assert.Single(setup.Sensors).Id);
        Assert.Equal(["ja@example.com"], setup.Mail!.AlertRecipients);
        Assert.Empty(CloudSetupWriter.Plan(setup, null).Problems);
    }

    [Fact]
    public void AdminExample_IsReadable()
    {
        var settings = CloudAdminSettings.Load(Example("cloud-admin.json"));

        Assert.Equal("abcdefghijklmnopqrst", settings.ProjectRef);
        Assert.Equal(CloudAdminSettings.DefaultDatabaseHost, settings.DatabaseHost);
        Assert.Null(settings.SecretKey);
        Assert.StartsWith("https://hc-ping.com/", settings.HealthchecksPingUrl);
    }
}
