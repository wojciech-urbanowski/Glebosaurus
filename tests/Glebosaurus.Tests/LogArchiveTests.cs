using Glebosaurus.Core.Logging;

namespace Glebosaurus.Tests;

public class LogArchiveTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "glebosaurus-logs-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationToken ct = TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private static DateOnly D(int month, int day) => new(2026, month, day);

    private string Touch(string name, string content = "x")
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private string[] Names() => Directory.Exists(directory)
        ? Directory.GetFiles(directory).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal).ToArray()
        : [];

    [Fact]
    public void FilesAreNamedAfterTheLocalDay() =>
        Assert.Equal(Path.Combine(directory, "glebosaurus-20261112.log"), new DailyLogFiles(directory).PathFor(D(11, 12)));

    [Fact]
    public async Task ReadsTheWholeDayAndReturnsNullWhenThereIsNone()
    {
        var files = new DailyLogFiles(directory);
        Touch("glebosaurus-20261112.log", "2026-11-12 10:00:00.000 [INF] Glebosaurus: zażółć gęślą jaźń\n");

        Assert.Contains("zażółć gęślą jaźń", await files.ReadDayAsync(D(11, 12), ct));
        Assert.Null(await files.ReadDayAsync(D(11, 11), ct));
        Assert.Null(await new DailyLogFiles(Path.Combine(directory, "nie-ma-takiego-katalogu")).ReadDayAsync(D(11, 12), ct));
    }

    [Fact]
    public async Task ReadsAFileThatTheLoggerStillHasOpenForAppending()
    {
        var files = new DailyLogFiles(directory);
        Directory.CreateDirectory(directory);
        await using var writer = new FileStream(files.PathFor(D(11, 12)), FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        await writer.WriteAsync("linia z otwartego pliku\n"u8.ToArray(), ct);
        await writer.FlushAsync(ct);

        Assert.Contains("linia z otwartego pliku", await files.ReadDayAsync(D(11, 12), ct));
    }

    [Fact]
    public void RetentionOfSevenDaysKeepsSevenPreviousDaysAndTodayOnly()
    {
        var files = new DailyLogFiles(directory);
        for (var day = 1; day <= 13; day++) Touch($"glebosaurus-202611{day:D2}.log");

        var deleted = files.ApplyRetention(today: D(11, 13), keepDays: 7);

        Assert.Equal(5, deleted);                                         // 01–05 listopada
        Assert.Equal(["glebosaurus-20261106.log", "glebosaurus-20261107.log", "glebosaurus-20261108.log", "glebosaurus-20261109.log",
            "glebosaurus-20261110.log", "glebosaurus-20261111.log", "glebosaurus-20261112.log", "glebosaurus-20261113.log"], Names());
    }

    [Fact]
    public void RetentionDoesNotTouchFilesThatAreNotDailyLogs()
    {
        var files = new DailyLogFiles(directory);
        Touch("glebosaurus-20200101.log");                                // stary dziennik – do usunięcia
        Touch("glebosaurus-notadate.log");
        Touch("glebosaurus-20261399.log");                                // „data” spoza kalendarza
        Touch("glebosaurus-2020010.log");
        Touch("inny.log");
        Touch("glebosaurus-20200101.txt");

        files.ApplyRetention(D(11, 13), 7);

        Assert.Equal(["glebosaurus-2020010.log", "glebosaurus-20200101.txt", "glebosaurus-20261399.log", "glebosaurus-notadate.log", "inny.log"], Names());
    }

    [Fact]
    public void RetentionKeepsYesterdayEvenWithTheShortestSetting()
    {
        var files = new DailyLogFiles(directory);
        Touch("glebosaurus-20261111.log");
        Touch("glebosaurus-20261112.log");

        files.ApplyRetention(D(11, 13), keepDays: 1);

        Assert.Equal(["glebosaurus-20261112.log"], Names());                // wczorajszy musi zostać do wysyłki administratorowi
    }

    [Fact]
    public void RetentionOnMissingDirectoryDoesNothing() =>
        Assert.Equal(0, new DailyLogFiles(Path.Combine(directory, "nie-ma")).ApplyRetention(D(11, 13), 7));

    [Fact]
    public void ServiceLogsHaveTheirOwnNamesAndRetention()
    {
        var service = new DailyLogFiles(directory, DailyLogFiles.ServiceName);
        Touch("glebosaurus-20261101.log");
        Touch("service-20261101.log");
        Touch("service-20261112.log");

        Assert.Equal(Path.Combine(directory, "service-20261112.log"), service.PathFor(D(11, 12)));
        Assert.Equal(1, service.ApplyRetention(D(11, 12), 7));
        Assert.Equal(["glebosaurus-20261101.log", "service-20261112.log"], Names());
        Assert.Equal(1, new DailyLogFiles(directory).ApplyRetention(D(11, 12), 7));
        Assert.Equal(["service-20261112.log"], Names());
    }
}
