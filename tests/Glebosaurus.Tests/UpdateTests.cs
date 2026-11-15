using System.Text;
using Glebosaurus.Core.Updates;

namespace Glebosaurus.Tests;

public class ReleaseVersionTests
{
    [Theory]
    [InlineData("v0.1.0", 0, 1, 0)]
    [InlineData("0.1.0", 0, 1, 0)]
    [InlineData("V1.20.3", 1, 20, 3)]
    [InlineData("0.1.0+90d53df5b9572a1d", 0, 1, 0)]
    [InlineData(" 2.0.10 ", 2, 0, 10)]
    public void ParsesTagsAndInformationalVersions(string text, int major, int minor, int patch)
    {
        Assert.True(ReleaseVersion.TryParse(text, out var version));
        Assert.Equal(new Version(major, minor, patch), version);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("v0.2.0-rc1")]
    [InlineData("0.1")]
    [InlineData("0.1.0.0")]
    [InlineData("najnowsza")]
    [InlineData("v0.1.x")]
    [InlineData("0.-1.0")]
    public void RejectsPrereleasesAndGarbage(string? text) => Assert.False(ReleaseVersion.TryParse(text, out _));

    [Fact]
    public void PackageNameHasThreePartVersion() =>
        Assert.Equal("Glebosaurus-0.1.0-win-x64.zip", ReleaseAssets.WindowsPackage(new Version(0, 1, 0)));
}

public class ReleaseInfoTests
{
    internal static string Json(string tag = "v0.2.0", bool draft = false, bool prerelease = false, params string[] assets) =>
        $$"""
        {
          "url": "https://api.github.com/repos/wojciech-urbanowski/Glebosaurus/releases/1",
          "tag_name": "{{tag}}",
          "name": "Glebosaurus {{tag}}",
          "draft": {{(draft ? "true" : "false")}},
          "prerelease": {{(prerelease ? "true" : "false")}},
          "assets": [{{string.Join(",", assets.Select(a => $$"""{ "name": "{{a}}", "size": 10, "browser_download_url": "https://github.com/wojciech-urbanowski/Glebosaurus/releases/download/{{tag}}/{{a}}" }"""))}}]
        }
        """;

    [Fact]
    public void ReadsTagFlagsAndAssets()
    {
        var release = ReleaseInfo.Parse(Json("v0.2.0", assets: ["Glebosaurus-0.2.0-win-x64.zip", "SHA256SUMS"]));

        Assert.Equal("v0.2.0", release.Tag);
        Assert.Equal(new Version(0, 2, 0), release.Version);
        Assert.False(release.IsDraft);
        Assert.False(release.IsPrerelease);
        Assert.Equal(["Glebosaurus-0.2.0-win-x64.zip", "SHA256SUMS"], release.Assets.Select(a => a.Name));
        Assert.Equal("https://github.com/wojciech-urbanowski/Glebosaurus/releases/download/v0.2.0/SHA256SUMS",
            release.FindAsset("SHA256SUMS")!.DownloadUrl.ToString());
        Assert.Null(release.FindAsset("sha256sums"));
    }

    [Fact]
    public void ReleaseWithoutAssetsIsStillARelease() =>
        Assert.Empty(ReleaseInfo.Parse("""{ "tag_name": "v1.0.0" }""").Assets);

    [Theory]
    [InlineData("""{ "tag_name": "nightly" }""")]
    [InlineData("""{ "message": "Not Found" }""")]
    [InlineData("<html>rate limit</html>")]
    [InlineData("[]")]
    public void UnreadableReleaseIsInvalidData(string json) => Assert.Throws<InvalidDataException>(() => ReleaseInfo.Parse(json));
}

public class Sha256SumsTests
{
    private const string HashA = "6b86b273ff34fce19d6b804eff5a3f5747ada4eaa22f1d49c01e52ddb7875b4b"; // "1"
    private const string HashB = "D4735E3A265E16EEE03F59718B9B5D03019C07D8B6C51F90DA3A666EEC13AB35"; // "2"

    [Fact]
    public void ParsesSha256sumOutputIncludingBinaryMarkerAndCrLf()
    {
        var sums = Sha256Sums.Parse($"{HashA}  Glebosaurus-0.1.0-win-x64.zip\r\n{HashB} *install-windows.ps1\r\n\r\n");

        Assert.Equal(HashA, sums["Glebosaurus-0.1.0-win-x64.zip"]);
        Assert.Equal(HashB.ToLowerInvariant(), sums["install-windows.ps1"]);
    }

    [Theory]
    [InlineData("abc  plik.zip")]
    [InlineData(HashA + " plik.zip")]
    [InlineData(HashA + "  ")]
    public void RejectsMalformedLines(string line) => Assert.Throws<InvalidDataException>(() => Sha256Sums.Parse(line));

    [Fact]
    public void RejectsDuplicateNames() =>
        Assert.Throws<InvalidDataException>(() => Sha256Sums.Parse($"{HashA}  a.zip\n{HashB}  a.zip\n"));

    [Fact]
    public void VerifiesContentAgainstTheList()
    {
        var sums = Sha256Sums.Parse($"{HashA}  a.txt\n{HashB}  b.txt\n");

        Sha256Sums.Verify(sums, "a.txt", Sha256Sums.Compute(Encoding.ASCII.GetBytes("1")));
        Sha256Sums.Verify(sums, "b.txt", Sha256Sums.Compute(new MemoryStream(Encoding.ASCII.GetBytes("2"))));
        Assert.Throws<InvalidDataException>(() => Sha256Sums.Verify(sums, "a.txt", Sha256Sums.Compute(Encoding.ASCII.GetBytes("2"))));
        Assert.Throws<InvalidDataException>(() => Sha256Sums.Verify(sums, "c.txt", HashA));
    }
}

public class UpdatePolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 3, 30, 0, TimeSpan.Zero);
    private static readonly Version Installed = new(0, 1, 0);
    private static readonly string[] AllAssets = ["Glebosaurus-0.2.0-win-x64.zip", "install-windows.ps1", "SHA256SUMS"];

    private static ReleaseInfo Release(string tag = "v0.2.0", bool draft = false, bool prerelease = false, params string[] assets) =>
        ReleaseInfo.Parse(ReleaseInfoTests.Json(tag, draft, prerelease, assets.Length > 0 ? assets : AllAssets));

    [Fact]
    public void NewerCompleteReleaseIsOffered()
    {
        var decision = UpdatePolicy.Decide(Installed, Release(), null, Now);

        Assert.Equal(UpdateVerdict.Available, decision.Verdict);
        Assert.Equal(new Version(0, 2, 0), decision.Offer!.Version);
        Assert.Equal("Glebosaurus-0.2.0-win-x64.zip", decision.Offer.Package.Name);
        Assert.Equal("install-windows.ps1", decision.Offer.Installer.Name);
        Assert.Equal("SHA256SUMS", decision.Offer.Checksums.Name);
    }

    [Theory]
    [InlineData("v0.1.0")]
    [InlineData("v0.0.9")]
    public void SameOrOlderReleaseMeansUpToDate(string tag)
    {
        var decision = UpdatePolicy.Decide(Installed, Release(tag, assets: ["x"]), null, Now);

        Assert.Equal(UpdateVerdict.UpToDate, decision.Verdict);
        Assert.Null(decision.Offer);
    }

    [Fact]
    public void DraftsAndPrereleasesAreIgnored()
    {
        Assert.Equal(UpdateVerdict.UpToDate, UpdatePolicy.Decide(Installed, Release(draft: true), null, Now).Verdict);
        Assert.Equal(UpdateVerdict.UpToDate, UpdatePolicy.Decide(Installed, Release(prerelease: true), null, Now).Verdict);
    }

    [Fact]
    public void ReleaseMissingFilesIsNotOffered()
    {
        var decision = UpdatePolicy.Decide(Installed, Release(assets: ["Glebosaurus-0.2.0-win-x64.zip"]), null, Now);

        Assert.Equal(UpdateVerdict.Incomplete, decision.Verdict);
        Assert.Null(decision.Offer);
        Assert.Contains("install-windows.ps1", decision.Reason);
        Assert.Contains("SHA256SUMS", decision.Reason);
    }

    [Fact]
    public void PackageForAnotherVersionDoesNotCount() =>
        Assert.Equal(UpdateVerdict.Incomplete,
            UpdatePolicy.Decide(Installed, Release(assets: ["Glebosaurus-0.1.9-win-x64.zip", "install-windows.ps1", "SHA256SUMS"]), null, Now).Verdict);

    [Fact]
    public void RecentAttemptAtTheSameVersionPostponesForADay()
    {
        var attempt = new UpdateAttempt(new Version(0, 2, 0), Now.AddHours(-23), "skrót się nie zgadza");

        var decision = UpdatePolicy.Decide(Installed, Release(), attempt, Now);

        Assert.Equal(UpdateVerdict.Postponed, decision.Verdict);
        Assert.Null(decision.Offer);
        Assert.Contains("skrót się nie zgadza", decision.Reason);
    }

    [Fact]
    public void OldAttemptOrAttemptAtAnotherVersionDoesNotBlock()
    {
        Assert.Equal(UpdateVerdict.Available,
            UpdatePolicy.Decide(Installed, Release(), new UpdateAttempt(new Version(0, 2, 0), Now.AddHours(-24), null), Now).Verdict);
        Assert.Equal(UpdateVerdict.Available,
            UpdatePolicy.Decide(Installed, Release(), new UpdateAttempt(new Version(0, 1, 5), Now.AddMinutes(-5), null), Now).Verdict);
    }
}

public class UpdateAttemptTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "glebosaurus-update-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void SavesAndLoadsTheAttempt()
    {
        var path = Path.Combine(directory, "updates", UpdateAttempt.FileName);
        new UpdateAttempt(new Version(0, 2, 0), new DateTimeOffset(2026, 11, 20, 4, 0, 0, TimeSpan.FromHours(1)), "zażółć").Save(path);

        var loaded = UpdateAttempt.Load(path);

        Assert.Equal(new UpdateAttempt(new Version(0, 2, 0), new DateTimeOffset(2026, 11, 20, 3, 0, 0, TimeSpan.Zero), "zażółć"), loaded);
    }

    [Fact]
    public void ReadsWhatTheInstallScriptWrites()
    {
        // ConvertTo-Json z Windows PowerShell 5.1: klucze jak w skrypcie, czas z przesunięciem.
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, UpdateAttempt.FileName);
        File.WriteAllText(path, """
            {
                "version":  "0.2.0",
                "at":  "2026-11-20T03:31:12.1234567+01:00",
                "reason":  "Monitor nie zatrzymał się w 240 s."
            }
            """);

        var attempt = UpdateAttempt.Load(path)!;

        Assert.Equal(new Version(0, 2, 0), attempt.Version);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 2, 31, 12, TimeSpan.Zero).AddTicks(1234567), attempt.At);
        Assert.Equal("Monitor nie zatrzymał się w 240 s.", attempt.Reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ \"version\": \"jutro\", \"at\": \"2026-11-20T03:00:00Z\" }")]
    [InlineData("{ \"version\": \"0.2.0\" }")]
    [InlineData("[1, 2]")]
    public void UnreadableOrMissingFileMeansNoAttempt(string content)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, UpdateAttempt.FileName);
        File.WriteAllText(path, content);

        Assert.Null(UpdateAttempt.Load(path));
        Assert.Null(UpdateAttempt.Load(Path.Combine(directory, "brak.json")));
    }
}

public class UpdateCheckScheduleTests
{
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");

    private static UpdateCheckSchedule Schedule(int hour = 3, int minute = 30) =>
        new(new TimeOnly(hour, minute), TimeSpan.FromMinutes(30), Warsaw);

    [Fact]
    public void NextDailyCheckIsTodayIfStillAheadElseTomorrow()
    {
        // 15 listopada: czas zimowy, UTC+1.
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 3, 30, 0, TimeSpan.FromHours(1)),
            Schedule().NextDaily(new DateTimeOffset(2026, 11, 15, 22, 0, 0, TimeSpan.FromHours(1))));
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 3, 30, 0, TimeSpan.FromHours(1)),
            Schedule().NextDaily(new DateTimeOffset(2026, 11, 16, 1, 0, 0, TimeSpan.FromHours(1))));
        Assert.Equal(new DateTimeOffset(2026, 11, 17, 3, 30, 0, TimeSpan.FromHours(1)),
            Schedule().NextDaily(new DateTimeOffset(2026, 11, 16, 3, 30, 0, TimeSpan.FromHours(1))));
    }

    [Fact]
    public void DailyTimeFollowsLocalClockAcrossDaylightSaving()
    {
        // 29 marca 2026 zegar przeskakuje z 02:00 na 03:00 – 03:30 jest wtedy już w czasie letnim (UTC+2).
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 3, 30, 0, TimeSpan.FromHours(2)),
            Schedule().NextDaily(new DateTimeOffset(2026, 3, 28, 12, 0, 0, TimeSpan.FromHours(1))));
        // Pora w „dziurze” (02:30 nie istnieje tego dnia) przesuwa się o godzinę.
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 3, 30, 0, TimeSpan.FromHours(2)),
            Schedule(2, 30).NextDaily(new DateTimeOffset(2026, 3, 28, 12, 0, 0, TimeSpan.FromHours(1))));
        // 25 października 2026 02:30 występuje dwa razy – liczy się pierwsze (jeszcze czas letni).
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 2, 30, 0, TimeSpan.FromHours(2)),
            Schedule(2, 30).NextDaily(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.FromHours(2))));
    }

    [Fact]
    public void FailedCheckRetriesSoonButNotAfterTheDailyTime()
    {
        var noon = new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.FromHours(1));
        Assert.Equal(noon.AddMinutes(30), Schedule().AfterFailure(noon));

        var justBefore = new DateTimeOffset(2026, 11, 16, 3, 15, 0, TimeSpan.FromHours(1));
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 3, 30, 0, TimeSpan.FromHours(1)), Schedule().AfterFailure(justBefore));
    }
}

public class InstallSettingsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "glebosaurus-install-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private string Write(string json)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, InstallSettings.FileName);
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void MissingFileGivesDefaults()
    {
        var settings = InstallSettings.Load(Path.Combine(directory, InstallSettings.FileName));

        Assert.Equal("wojciech-urbanowski/Glebosaurus", settings.Repository);
        Assert.Equal("Glebosaurus", settings.TaskName);
        Assert.True(settings.AutoUpdate);
        Assert.Equal(new TimeOnly(3, 30), settings.DailyCheckTime);
        Assert.Equal("https://api.github.com/repos/wojciech-urbanowski/Glebosaurus/releases/latest", settings.LatestReleaseUri.ToString());
    }

    [Fact]
    public void ReadsWhatTheInstallScriptWrites()
    {
        var settings = InstallSettings.Load(Write("""
            {
                "repository":  "ktos/Glebosaurus",
                "apiBase":  "http://127.0.0.1:8471/",
                "taskName":  "Glebosaurus-test",
                "autoUpdate":  false,
                "dailyUpdateCheck":  "23:05"
            }
            """));

        Assert.Equal("Glebosaurus-test", settings.TaskName);
        Assert.False(settings.AutoUpdate);
        Assert.Equal(new TimeOnly(23, 5), settings.DailyCheckTime);
        Assert.Equal("http://127.0.0.1:8471/repos/ktos/Glebosaurus/releases/latest", settings.LatestReleaseUri.ToString());
    }

    [Theory]
    [InlineData("""{ "repository": "bez-ukosnika" }""")]
    [InlineData("""{ "apiBase": "ftp://example.com" }""")]
    [InlineData("""{ "dailyUpdateCheck": "3:30 rano" }""")]
    [InlineData("""{ "autoUpdate": "tak" """)]
    public void InvalidFileIsReported(string json) => Assert.Throws<InvalidDataException>(() => InstallSettings.Load(Write(json)));
}
