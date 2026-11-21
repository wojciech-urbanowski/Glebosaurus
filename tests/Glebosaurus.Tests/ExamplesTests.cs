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
    public void AgentExample_IsAValidAgentFile_OnceTheKeyIsFilledIn()
    {
        // Wzór ma klucz-wzór „sb_publishable_…” – niewypełniony jest odrzucany z wyjaśnieniem, a po wpisaniu klucza (jak
        // zrobi to użytkownik) reszta pliku musi być poprawna.
        var example = Example("glebosaurus.config.json");
        Assert.Contains("to wciąż wzór", Assert.Throws<InvalidDataException>(() => AgentConfigLoader.Load(example)).Message);
        var path = Path.Combine(temp.FullName, "glebosaurus.config.json");
        var text = File.ReadAllText(example);
        Assert.Contains("\"sb_publishable_…\"", text);
        File.WriteAllText(path, text.Replace("\"sb_publishable_…\"", "\"sb_publishable_Przyklad-Klucza_123\"", StringComparison.Ordinal));

        var config = AgentConfigLoader.Load(path);

        Assert.Equal("abcdefghijklmnopqrst.supabase.co", config.Cloud!.ProjectUrl.Host);
        Assert.Null(config.Cloud.Backup);
        Assert.Null(config.Web);
        Assert.True(config.Bluetooth);
    }

    [Fact]
    public void AdminExample_IsReadable()
    {
        var settings = CloudAdminSettings.Load(Example("cloud-admin.json"));

        Assert.Equal("abcdefghijklmnopqrst", settings.ProjectRef);
        Assert.Equal(CloudAdminSettings.DefaultDatabaseHost, settings.DatabaseHost);
        Assert.Null(settings.SecretKey);
        Assert.StartsWith("sb_publishable_", settings.PublishableKey);
    }
}
