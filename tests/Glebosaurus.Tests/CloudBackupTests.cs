using Glebosaurus.Cloud.Admin;
using Glebosaurus.Core.Config;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Glebosaurus.Tests;

/// <summary>Kopia zapasowa chmury bez sieci: nazwy plików, retencja, „raz dziennie przy pierwszej okazji”, sekcja konfiguracji.</summary>
public sealed class CloudBackupTests : IDisposable
{
    private const string Project = "abcdefghijklmnopqrst";
    private static readonly CancellationToken Ct = CancellationToken.None;
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"glebosaurus-backup-{Guid.NewGuid():N}");
    private readonly ManualTimeProvider clock = new(new DateTimeOffset(2026, 11, 17, 22, 30, 0, TimeSpan.Zero));

    public CloudBackupTests() => Directory.CreateDirectory(directory);

    public void Dispose()
    {
        try { Directory.Delete(directory, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private string Touch(DateTimeOffset at, string project = Project)
    {
        var path = Path.Combine(directory, CloudBackup.FileName(project, at));
        File.WriteAllText(path, "kopia");
        return path;
    }

    [Fact]
    public void FileName_CarriesProjectAndUtcTime_AndParsesBack()
    {
        var at = new DateTimeOffset(2026, 11, 17, 23, 5, 9, TimeSpan.FromHours(1));

        var name = CloudBackup.FileName(Project, at);

        Assert.Equal("glebosaurus-backup-abcdefghijklmnopqrst-20261117T220509Z.zip", name);
        Assert.Equal(at, CloudBackup.CreatedAt(name, Project));
        Assert.Null(CloudBackup.CreatedAt(name, "innyprojektinnyproje"));
        Assert.Null(CloudBackup.CreatedAt(name + ".tmp", Project));
    }

    [Fact]
    public void Prune_KeepsNewestOfThisProject_AndLeavesOtherFilesAlone()
    {
        var start = clock.GetUtcNow();
        var files = Enumerable.Range(0, 5).Select(i => Touch(start.AddDays(-i))).ToList();
        var other = Touch(start.AddDays(-10), "innyprojektinnyproje");
        File.WriteAllText(Path.Combine(directory, "notatka.txt"), "x");

        var removed = CloudBackup.Prune(directory, Project, keep: 3);

        Assert.Equal(files[3..], removed.Order().Reverse().ToList());
        Assert.Equal(files[..3], CloudBackup.List(directory, Project).Select(f => f.Path).ToList());
        Assert.True(File.Exists(other));
        Assert.True(File.Exists(Path.Combine(directory, "notatka.txt")));
    }

    private DailyCloudBackup Daily(Func<CancellationToken, Task<CloudBackupResult>> backup, TimeZoneInfo? zone = null) =>
        new(backup, directory, Project, keep: 2, zone ?? TimeZoneInfo.Utc, clock, NullLogger.Instance);

    private Task<CloudBackupResult> FakeBackup(CancellationToken _)
    {
        var path = Touch(clock.GetUtcNow());
        return Task.FromResult(new CloudBackupResult(path, clock.GetUtcNow(), new Dictionary<string, long> { ["readings"] = 10 }, 3, 5));
    }

    [Fact]
    public async Task Daily_OncePerLocalDay_AtFirstChance_ThenPrunes()
    {
        var calls = 0;
        var daily = Daily(ct => { calls++; return FakeBackup(ct); });

        Assert.True(await daily.TryBackupAsync(Ct));
        clock.Advance(TimeSpan.FromHours(1));
        Assert.False(await daily.TryBackupAsync(Ct));   // ten sam dzień (UTC 23:30)
        clock.Advance(TimeSpan.FromHours(1));
        Assert.True(await daily.TryBackupAsync(Ct));    // po północy – nowy dzień
        clock.Advance(TimeSpan.FromDays(3));            // komputer wyłączony przez 3 dni – jedna kopia, nie trzy
        Assert.True(await daily.TryBackupAsync(Ct));
        Assert.False(await daily.TryBackupAsync(Ct));

        Assert.Equal(3, calls);
        Assert.Equal(2, CloudBackup.List(directory, Project).Count);
        Assert.Equal(clock.GetUtcNow(), daily.LastBackupAt);
    }

    [Fact]
    public void Daily_DayFollowsDeviceTimeZone()
    {
        Touch(clock.GetUtcNow());   // 22:30 UTC = 23:30 w Warszawie (zima)
        var warsaw = TimeZoneInfo.CreateCustomTimeZone("Warszawa (zima)", TimeSpan.FromHours(1), "Warszawa", "Warszawa");
        var daily = Daily(FakeBackup, warsaw);

        clock.Advance(TimeSpan.FromMinutes(45));         // 23:15 UTC – w Warszawie już następny dzień
        Assert.True(daily.IsDue(clock.GetUtcNow()));
        Assert.False(Daily(FakeBackup).IsDue(clock.GetUtcNow()));
    }

    [Fact]
    public async Task Daily_Failure_IsLoggedAndRetriedLater()
    {
        var fail = true;
        var daily = Daily(ct => fail ? throw new NpgsqlException("projekt uśpiony") : FakeBackup(ct));

        Assert.False(await daily.TryBackupAsync(Ct));
        Assert.Equal("projekt uśpiony", daily.LastError);
        Assert.Null(daily.LastBackupAt);

        fail = false;
        clock.Advance(TimeSpan.FromHours(1));
        Assert.True(await daily.TryBackupAsync(Ct));
        Assert.Null(daily.LastError);
    }

    // ---- Sekcja „cloud.backup” ---------------------------------------------------------------------------------------

    private string WriteConfig(string backup)
    {
        var path = Path.Combine(directory, "glebosaurus.config.json");
        File.WriteAllText(path, $$"""
            { "cloud": { "url": "https://abcdefghijklmnopqrst.supabase.co", "publishableKey": "sb_publishable_x", {{backup}} } }
            """);
        return path;
    }

    [Fact]
    public void BackupSection_Defaults()
    {
        var config = AgentConfigLoader.Load(WriteConfig(""" "backup": { "adminFile": "cloud-admin.json" } """));

        Assert.Equal(("cloud-admin.json", "backups", 14), (config.Cloud!.Backup!.AdminFile, config.Cloud.Backup.Directory, config.Cloud.Backup.Keep));
    }

    [Theory]
    [InlineData(""" "backup": { } """)]
    [InlineData(""" "backup": { "adminFile": "a.json", "keep": 0 } """)]
    [InlineData(""" "backup": { "adminFile": "a.json", "directory": " " } """)]
    public void BackupSection_Invalid_IsRejected(string backup)
    {
        Assert.Throws<InvalidDataException>(() => AgentConfigLoader.Load(WriteConfig(backup)));
    }
}
