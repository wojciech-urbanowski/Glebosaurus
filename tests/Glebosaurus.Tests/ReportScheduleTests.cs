using Glebosaurus.Core.Reports;

namespace Glebosaurus.Tests;

public class ReportScheduleTests
{
    private static readonly TimeSpan CatchUp = TimeSpan.FromHours(3);

    private static DateTimeOffset Utc(int day, int hour, int minute) => new(2026, 11, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void ReportIsDueAfterItsTimeWithinCatchUp()
    {
        var due = Assert.Single(ReportSchedule.Due(["07:00"], Utc(13, 7, 30), TimeZoneInfo.Utc, CatchUp));
        Assert.Equal("07:00|2026-11-13", due.Key);
        Assert.Equal(Utc(13, 7, 0), due.ScheduledAt);
    }

    [Theory]
    [InlineData(6, 59)]
    [InlineData(10, 0)]
    [InlineData(11, 30)]
    public void ReportIsNotDueBeforeItsTimeOrAfterCatchUp(int hour, int minute) =>
        Assert.Empty(ReportSchedule.Due(["07:00"], Utc(13, hour, minute), TimeZoneInfo.Utc, CatchUp));

    [Fact]
    public void LateReportIsCaughtUpAfterMidnight()
    {
        var due = Assert.Single(ReportSchedule.Due(["23:30"], Utc(14, 0, 30), TimeZoneInfo.Utc, CatchUp));
        Assert.Equal("23:30|2026-11-13", due.Key);
    }

    [Fact]
    public void SeveralTimesAreReturnedInOrderAndInvalidOnesIgnored()
    {
        var due = ReportSchedule.Due(["19:00", "nie-godzina", "18:00"], Utc(13, 19, 30), TimeZoneInfo.Utc, CatchUp);
        Assert.Equal(["18:00|2026-11-13", "19:00|2026-11-13"], due.Select(d => d.Key));
    }

    [Fact]
    public void LocalTimeZoneShiftsTheSchedule()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("test+1", TimeSpan.FromHours(1), "test", "test");
        // 07:00 lokalnie to 06:00 UTC.
        Assert.Single(ReportSchedule.Due(["07:00"], Utc(13, 6, 30), zone, CatchUp));
        Assert.Empty(ReportSchedule.Due(["07:00"], Utc(13, 5, 30), zone, CatchUp));
    }

    [Theory]
    [InlineData("07:00", true)]
    [InlineData("7:00", false)]
    [InlineData("24:00", false)]
    [InlineData("07:60", false)]
    [InlineData("rano", false)]
    public void OnlyHhMmTimesAreAccepted(string text, bool valid) =>
        Assert.Equal(valid, ReportSchedule.TryParseTime(text, out _));
}
