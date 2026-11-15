namespace Glebosaurus.Tuya.Tests;

public class SoilProfileTests
{
    private static TuyaDataPoint Value(int id, int value) =>
        new(id, 2, value.ToString("X8"), value.ToString(), null);

    private static TuyaDataPoint Enum(int id, int value) =>
        new(id, 4, value.ToString("X2"), value.ToString(), null);

    [Fact]
    public void Sgs01MapsValuesObservedOnRealSensor()
    {
        // Odczyt z ATLO SMS1 (gvygg3m8) 2026-11-13, czujnik w powietrzu.
        var points = new[] { Value(6, 0), Value(3, 18), Value(5, 237), Value(15, 33), Enum(14, 1), Enum(9, 0) };
        var at = DateTimeOffset.Parse("2026-11-13T11:59:00+01:00");

        var reading = SoilSensorProfiles.Map(SoilSensorProfiles.Sgs01, points, at);

        Assert.Equal(at, reading.At);
        Assert.Equal(18, reading.MoisturePercent);
        Assert.Equal(23.7m, reading.SoilTemperatureCelsius);
        Assert.Equal(33, reading.BatteryPercent);
        Assert.Equal(1, reading.BatteryState);
        Assert.Equal("0", reading.RawDataPoints[9]);
    }

    [Fact]
    public void LaterReportOfTheSameDatapointWins()
    {
        var reading = SoilSensorProfiles.Map(SoilSensorProfiles.Sgs01, [Value(3, 20), Value(3, 18)], DateTimeOffset.UnixEpoch);
        Assert.Equal(18, reading.MoisturePercent);
    }

    private static TuyaDataPoint Timed(int id, int value, DateTimeOffset at) =>
        new(id, 2, value.ToString("X8"), value.ToString(), at);

    /// <summary>Jeden rekord historii: wilgotność, temperatura ×10 i bateria z tym samym czasem czujnika.</summary>
    private static TuyaDataPoint[] Record(string time, int moisture, int tenths, int battery)
    {
        var at = DateTimeOffset.Parse($"2026-11-13T{time}+01:00");
        return [Timed(3, moisture, at), Timed(5, tenths, at), Timed(15, battery, at)];
    }

    [Fact]
    public void MapAllBuildsOneRecordPerSensorTimeInArrivalOrderAndGivesUntimedPointsTheConnectionTime()
    {
        // Przebieg zaobserwowany 2026-11-13: historia przychodzi w ramkach 0x8003 od najstarszego, stan z chwili połączenia jest ostatni.
        var t1 = DateTimeOffset.Parse("2026-11-13T14:13:47+01:00");
        var t2 = DateTimeOffset.Parse("2026-11-13T15:13:47+01:00");
        var connected = DateTimeOffset.Parse("2026-11-13T15:30:00+01:00");
        var points = new[]
        {
            Timed(3, 79, t1), Timed(5, 160, t1), Timed(15, 30, t1),
            Timed(3, 81, t2), Timed(5, 99, t2), Timed(15, 30, t2),
            Value(3, 80), Value(5, 120)                              // zwykły raport stanu, bez czasu czujnika
        };

        var readings = SoilSensorProfiles.MapAll(SoilSensorProfiles.Sgs01, points, connected);

        Assert.Equal([t1, t2, connected], readings.Select(r => r.At));
        Assert.Equal([79, 81, 80], readings.Select(r => r.MoisturePercent));
        Assert.Equal([16.0m, 9.9m, 12.0m], readings.Select(r => r.SoilTemperatureCelsius));
    }

    [Fact]
    public void MapAllKeepsEveryRecordEvenWhenSensorRepeatsTheSameDatapoint()
    {
        // Regresja: sesja scalała punkty o tym samym numerze, więc z całej historii zostawał jeden rekord.
        var start = DateTimeOffset.Parse("2026-11-13T10:00:00+01:00");
        var points = Enumerable.Range(0, 5).SelectMany(i => new[] { Timed(3, 20 + i, start.AddHours(i)), Timed(5, 200, start.AddHours(i)) });

        var readings = SoilSensorProfiles.MapAll(SoilSensorProfiles.Sgs01, points, start.AddHours(4).AddMinutes(1));

        Assert.Equal([20, 21, 22, 23, 24], readings.Select(r => r.MoisturePercent));
        Assert.Equal(Enumerable.Range(0, 5).Select(i => start.AddHours(i)), readings.Select(r => r.At)); // zegar dobry – bez przesunięcia
    }

    [Fact]
    public void MapAllRebasesRecordsAfterSensorClockResetSoTheLastOneIsTheConnectionState()
    {
        // Cisy 2026-11-13 po wymianie baterii (listen o 15:48:50): dwa ostatnie rekordy liczą czas od restartu i są o ok. 24 min
        // wstecz; 15:14:52 (w ziemi, 61%) jest sprzed wymiany i jest prawdziwy. Czujnik wysłał je w tej kolejności.
        var connected = DateTimeOffset.Parse("2026-11-13T15:48:50+01:00");
        var points = new[]
        {
            Record("13:54:55", 15, 177, 21), Record("14:16:55", 15, 177, 21), Record("15:14:52", 61, 102, 21),
            Record("15:14:06", 16, 160, 56), Record("15:24:42", 16, 160, 56)
        }.SelectMany(record => record);

        var readings = SoilSensorProfiles.MapAll(SoilSensorProfiles.Sgs01, points, connected);

        Assert.Equal([15, 15, 61, 16, 16], readings.Select(r => r.MoisturePercent));
        Assert.Equal(
            [DateTimeOffset.Parse("2026-11-13T13:54:55+01:00"), DateTimeOffset.Parse("2026-11-13T14:16:55+01:00"),
             DateTimeOffset.Parse("2026-11-13T15:14:52+01:00"), // sprzed restartu – bez zmian
             DateTimeOffset.Parse("2026-11-13T15:38:14+01:00"), // restart: 10 min 36 s przed połączeniem
             connected],                                        // rekord z chwili połączenia
            readings.Select(r => r.At));
        Assert.Equal(56, readings[^1].BatteryPercent); // „stan teraz” to ostatni rekord (nowe baterie, w powietrzu), nie 61% z gleby
    }

    [Fact]
    public void MapAllRebasesWholeSeriesWhenClockIsOffButNeverWentBackwards()
    {
        var connected = DateTimeOffset.Parse("2026-11-13T12:00:00+01:00");
        var points = new[] { Record("10:00:00", 40, 200, 80), Record("10:10:00", 41, 200, 80) }.SelectMany(record => record);

        var readings = SoilSensorProfiles.MapAll(SoilSensorProfiles.Sgs01, points, connected);

        Assert.Equal([connected - TimeSpan.FromMinutes(10), connected], readings.Select(r => r.At));
    }

    [Fact]
    public void MapAllLeavesAnAccurateClockAlone()
    {
        // Cyprysek: rekord z połączenia miał czas o kilkanaście sekund wcześniejszy niż moment odbioru.
        var connected = DateTimeOffset.Parse("2026-11-13T15:30:12+01:00");
        var points = new[] { Record("14:13:47", 79, 160, 30), Record("15:29:59", 81, 99, 30) }.SelectMany(record => record);

        var readings = SoilSensorProfiles.MapAll(SoilSensorProfiles.Sgs01, points, connected);

        Assert.Equal([DateTimeOffset.Parse("2026-11-13T14:13:47+01:00"), DateTimeOffset.Parse("2026-11-13T15:29:59+01:00")],
            readings.Select(r => r.At));
    }

    [Fact]
    public void MapAllSkipsRecordsThatCarryNoSoilMeasurement()
    {
        // Rekord z 13:13:23 miał tylko DP15 (bateria) i DP14 – to nie jest pomiar gleby.
        var at = DateTimeOffset.Parse("2026-11-13T13:13:23+01:00");
        var points = new[] { Timed(15, 30, at), new TuyaDataPoint(14, 4, "01", "1", at) };

        Assert.Empty(SoilSensorProfiles.MapAll(SoilSensorProfiles.Sgs01, points, at.AddHours(2)));
    }

    [Fact]
    public void MissingDatapointsStayNullInsteadOfBecomingZero()
    {
        var reading = SoilSensorProfiles.Map(SoilSensorProfiles.Sgs01, [Value(3, 18)], DateTimeOffset.UnixEpoch);
        Assert.Null(reading.SoilTemperatureCelsius);
        Assert.Null(reading.BatteryPercent);
    }

    [Theory]
    [InlineData("gvygg3m8", SoilSensorProfiles.Sgs01)]
    [InlineData("jabotj1z", SoilSensorProfiles.Sgs01)]
    [InlineData("ojzlzzsw", SoilSensorProfiles.Ojzlzzsw)]
    public void KnownProductsSelectTheirProfile(string productId, string expected) =>
        Assert.Equal(expected, SoilSensorProfiles.ForProduct(productId));

    [Fact]
    public void UnknownProductHasNoProfile() => Assert.Null(SoilSensorProfiles.ForProduct("zzzzzzzz"));
}
