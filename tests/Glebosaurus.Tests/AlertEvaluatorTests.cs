using Glebosaurus.Core.Alerts;
using Glebosaurus.Core.Domain;

namespace Glebosaurus.Tests;

public class AlertEvaluatorTests
{
    private static readonly DateTimeOffset Now = Fixtures.T0.AddHours(10);

    private static IReadOnlyList<StoredReading> Readings(params int?[] moistureNewestFirst) =>
        moistureNewestFirst.Select((m, i) => Fixtures.Reading("s1", Now.AddMinutes(-15 * (i + 1)), moisture: m)).ToList();

    private static IReadOnlyList<DesiredAlert> Evaluate(IReadOnlyList<StoredReading> recent, params AlertKind[] open) =>
        AlertEvaluator.Evaluate(Fixtures.Sensor(), recent, Fixtures.T0, Now, open.ToHashSet());

    [Fact]
    public void SingleLowReadingDoesNotOpenAlert() =>
        Assert.DoesNotContain(Evaluate(Readings(18, 50)), a => a.Kind == AlertKind.NeedsWater);

    [Fact]
    public void TwoConsecutiveLowReadingsOpenAlertWithDetail()
    {
        var alert = Assert.Single(Evaluate(Readings(18, 20)), a => a.Kind == AlertKind.NeedsWater);
        Assert.Contains("18%", alert.Detail);
        Assert.Contains("25%", alert.Detail);
    }

    [Fact]
    public void NotEnoughReadingsYetMeansNoAlert() =>
        Assert.Empty(Evaluate(Readings(10)));

    [Fact]
    public void OpenAlertStaysOpenBetweenThresholds() =>
        Assert.Contains(Evaluate(Readings(30, 18), AlertKind.NeedsWater), a => a.Kind == AlertKind.NeedsWater);

    [Fact]
    public void OpenAlertClosesAtClearThreshold() =>
        Assert.DoesNotContain(Evaluate(Readings(35, 18), AlertKind.NeedsWater), a => a.Kind == AlertKind.NeedsWater);

    [Fact]
    public void ClosedAlertDoesNotReopenBetweenThresholds() =>
        Assert.DoesNotContain(Evaluate(Readings(30, 30)), a => a.Kind == AlertKind.NeedsWater);

    [Fact]
    public void MissingMoistureValuesAreIgnoredNotTreatedAsZero() =>
        Assert.Empty(Evaluate(Readings(null, null, 60)));

    [Theory]
    [InlineData(18, true)]  // próg jest włącznie: 18% lub mniej
    [InlineData(19, false)]
    public void LowBatteryThresholdIsInclusive(int battery, bool alert)
    {
        var readings = new[] { Fixtures.Reading("s1", Now.AddMinutes(-15), battery: battery), Fixtures.Reading("s1", Now.AddMinutes(-30), battery: battery) };
        var desired = AlertEvaluator.Evaluate(Fixtures.Sensor(), readings, Fixtures.T0, Now, new HashSet<AlertKind>());
        Assert.Equal(alert, desired.Any(a => a.Kind == AlertKind.LowBattery));
        Assert.Equal(alert, Fixtures.Sensor().IsBatteryLow(battery));
    }

    [Fact]
    public void LowBatteryNeedsTwoReadingsAndUsesWiderHysteresis()
    {
        var low = new[] { Fixtures.Reading("s1", Now.AddMinutes(-15), battery: 15), Fixtures.Reading("s1", Now.AddMinutes(-30), battery: 18) };
        Assert.Contains(AlertEvaluator.Evaluate(Fixtures.Sensor(), low, Fixtures.T0, Now, new HashSet<AlertKind>()), a => a.Kind == AlertKind.LowBattery);

        var recovering = new[] { Fixtures.Reading("s1", Now.AddMinutes(-15), battery: 25), Fixtures.Reading("s1", Now.AddMinutes(-30), battery: 15) };
        var open = new HashSet<AlertKind> { AlertKind.LowBattery };
        Assert.Contains(AlertEvaluator.Evaluate(Fixtures.Sensor(), recovering, Fixtures.T0, Now, open), a => a.Kind == AlertKind.LowBattery); // 25% < 18% + 10%

        var recovered = new[] { Fixtures.Reading("s1", Now.AddMinutes(-15), battery: 30), Fixtures.Reading("s1", Now.AddMinutes(-30), battery: 15) };
        Assert.DoesNotContain(AlertEvaluator.Evaluate(Fixtures.Sensor(), recovered, Fixtures.T0, Now, open), a => a.Kind == AlertKind.LowBattery);
    }

    [Fact]
    public void SilentSensorAlertUsesLastReadingOrFallbackReference()
    {
        var sensor = Fixtures.Sensor(); // silentAfterHours = 6

        var fresh = new[] { Fixtures.Reading("s1", Now.AddHours(-1)) };
        Assert.DoesNotContain(AlertEvaluator.Evaluate(sensor, fresh, Fixtures.T0, Now, new HashSet<AlertKind>()), a => a.Kind == AlertKind.SensorSilent);

        var stale = new[] { Fixtures.Reading("s1", Now.AddHours(-7)) };
        var alert = Assert.Single(AlertEvaluator.Evaluate(sensor, stale, Fixtures.T0, Now, new HashSet<AlertKind>()), a => a.Kind == AlertKind.SensorSilent);
        Assert.Contains("7 h", alert.Detail);

        // Czujnik bez żadnego odczytu: liczymy od startu monitora.
        Assert.Contains(AlertEvaluator.Evaluate(sensor, [], Now.AddHours(-7), Now, new HashSet<AlertKind>()), a => a.Kind == AlertKind.SensorSilent);
        Assert.Empty(AlertEvaluator.Evaluate(sensor, [], Now.AddHours(-1), Now, new HashSet<AlertKind>()));
    }

    [Fact]
    public void DuringGraceSilentAlertIsNotOpenedButAnOpenOneStaysUntilAReading()
    {
        var sensor = Fixtures.Sensor(); // silentAfterHours = 6
        var stale = new[] { Fixtures.Reading("s1", Now.AddHours(-18)) };
        var none = new HashSet<AlertKind>();
        var open = new HashSet<AlertKind> { AlertKind.SensorSilent };

        Assert.DoesNotContain(AlertEvaluator.Evaluate(sensor, stale, Fixtures.T0, Now, none, holdSilent: true), a => a.Kind == AlertKind.SensorSilent);
        Assert.Contains(AlertEvaluator.Evaluate(sensor, stale, Fixtures.T0, Now, open, holdSilent: true), a => a.Kind == AlertKind.SensorSilent);

        var fresh = new[] { Fixtures.Reading("s1", Now.AddMinutes(-1)) };
        Assert.DoesNotContain(AlertEvaluator.Evaluate(sensor, fresh, Fixtures.T0, Now, open, holdSilent: true), a => a.Kind == AlertKind.SensorSilent);
    }

    [Fact]
    public void GraceHoldsAfterStartAndAgainAfterAPause()
    {
        var grace = new SilenceGrace();
        var start = Fixtures.T0;

        Assert.Null(grace.Observe(start));
        Assert.True(grace.Holds(start + TimeSpan.FromMinutes(29)));
        Assert.False(grace.Holds(start + TimeSpan.FromMinutes(30)));

        // Zwykła praca: przeliczenia co kilka minut (nawet dokładnie co 10 min) – bez przerwy.
        var t = start;
        for (var i = 0; i < 6; i++) Assert.Null(grace.Observe(t += TimeSpan.FromMinutes(10)));
        Assert.False(grace.Holds(t));

        // Uśpienie na 8 h: nowy okres ochronny od wybudzenia.
        var woke = t + TimeSpan.FromHours(8);
        Assert.Equal(TimeSpan.FromHours(8), grace.Observe(woke));
        Assert.True(grace.Holds(woke + TimeSpan.FromMinutes(29)));
        Assert.False(grace.Holds(woke + SilenceGrace.DefaultGrace));
    }
}
