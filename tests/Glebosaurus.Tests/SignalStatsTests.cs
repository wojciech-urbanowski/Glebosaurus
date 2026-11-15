using Glebosaurus.Core.Monitoring;

namespace Glebosaurus.Tests;

public class SignalStatsTests
{
    private static SignalStats Of(params int[] samples)
    {
        var stats = new SignalStats();
        foreach (var sample in samples) stats.Add(sample);
        return stats;
    }

    [Fact]
    public void EmptyStatsHaveNoAverageMinOrMax()
    {
        var stats = new SignalStats();

        Assert.Null(stats.Average);
        Assert.Null(stats.Min);
        Assert.Null(stats.Max);
        Assert.Equal(0, stats.Count);
    }

    [Theory]
    [InlineData(-127, false)] // Windows: „brak wartości”
    [InlineData(127, false)]  // HCI: „brak wartości”
    [InlineData(-126, true)]
    [InlineData(-94, true)]
    [InlineData(-52, true)]
    public void OnlyRealSignalLevelsAreValid(int dbm, bool valid) => Assert.Equal(valid, SignalStats.IsValid(dbm));

    [Fact]
    public void UnavailableSamplesAreCountedButDoNotDragTheAverageDown()
    {
        // To właśnie błąd, który naprawiamy: ostatni pakiet −127 nie może zasłonić dobrych próbek.
        var stats = Of(-80, -127, -82, 127, -78);

        Assert.Equal(3, stats.Count);
        Assert.Equal(2, stats.Unavailable);
        Assert.Equal(-80, stats.Average);
        Assert.Equal(-82, stats.Min);
        Assert.Equal(-78, stats.Max);
    }

    [Fact]
    public void OnlyUnavailableSamplesLeaveTheAverageUnknown()
    {
        var stats = Of(-127, -127);

        Assert.Null(stats.Average);
        Assert.Equal(2, stats.Unavailable);
    }

    [Fact]
    public void AverageIsRoundedAwayFromZeroToWholeDbm()
    {
        Assert.Equal(-81, Of(-80, -81).Average);   // −80,5 → −81
        Assert.Equal(-81, Of(-83, -81, -78).Average); // −80,67 → −81
        Assert.Equal(-80, Of(-80, -80, -81).Average); // −80,33 → −80
    }

    [Fact]
    public void FormatUsesPlainMinusAndSaysUnknownForMissingValue()
    {
        Assert.Equal("-81 dBm", SignalStats.Format(-81));
        Assert.Equal("-81 dBm", SignalStats.Format((int?)-81));
        Assert.Equal("nieznany", SignalStats.Format((int?)null));
    }

    [Fact]
    public void DescribeShowsAverageRangeAndSampleCount()
    {
        Assert.Equal("śr. -81 dBm (-83…-78, 3 próbek)", Of(-83, -81, -78).Describe());
        Assert.Equal("śr. -80 dBm (-82…-78, 3 próbek, 2 bez wartości)", Of(-80, -127, -82, 127, -78).Describe());
        Assert.Equal("brak wartości (2 próbek bez RSSI)", Of(-127, -127).Describe());
    }
}
