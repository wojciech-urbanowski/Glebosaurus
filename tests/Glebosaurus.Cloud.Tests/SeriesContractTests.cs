using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Glebosaurus.Core.Charts;
using Glebosaurus.Core.Cloud;
using Glebosaurus.Core.Domain;

namespace Glebosaurus.Cloud.Tests;

/// <summary>
/// Wykresy strony w chmurze (owner_series) mają te same punkty co lokalny panel (<see cref="ReadingSeriesBuilder"/>):
/// ta sama drabinka przedziałów, przedziały w czasie lokalnym (od poniedziałku), zmiana czasu, dziury, średnie, min i max.
/// </summary>
public sealed class SeriesContractTests(CloudDatabase db)
{
    private const string Zone = "Europe/Warsaw";

    /// <summary>Scenariusz: odczyty co <c>step</c> minut (z drganiem) od <c>start</c> przez <c>days</c>, z dziurami.</summary>
    private sealed record Setup(string Name, DateTimeOffset Start, double Days, double StepMinutes, int Points, (double From, double To)[] GapsInDays);

    private static readonly Setup[] All =
    [
        new("pojedyncze odczyty", new(2026, 11, 1, 6, 0, 0, TimeSpan.Zero), 3, 125, 300, []),
        new("przedziały z dziurą", new(2026, 11, 1, 0, 7, 0, TimeSpan.Zero), 6, 15, 100, [(2.2, 2.9)]),
        new("zmiana czasu (koniec czasu letniego)", new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero), 2, 10, 60, []),
        new("dni", new(2026, 8, 1, 3, 0, 0, TimeSpan.Zero), 60, 43, 50, [(20, 24)]),
        new("tygodnie od poniedziałku", new(2026, 3, 3, 9, 30, 0, TimeSpan.Zero), 200, 180, 30, [(100, 101.5)]),
    ];

    public static TheoryData<string> Setups() => [.. All.Select(s => s.Name)];

    [Theory]
    [MemberData(nameof(Setups))]
    public async Task OwnerSeries_MatchesLocalPanel(string name)
    {
        db.SkipIfUnavailable();
        var setup = All.Single(s => s.Name == name);
        var s = new Scenario(db, DateTimeOffset.UtcNow);
        var owner = await s.OwnerAsync();
        var sensor = await s.SensorAsync("seria", new JsonObject { ["readIntervalMinutes"] = 15 });
        var readings = Readings(sensor, setup);
        await InsertAsync(readings);
        var from = setup.Start.AddHours(-1);
        var to = setup.Start.AddDays(setup.Days).AddHours(1);

        var cloud = await db.OwnerAsync("owner_series", owner, null, ("p_sensor", sensor), ("p_from", from), ("p_to", to),
            ("p_points", setup.Points), ("p_zone", Zone));
        var (bucket, points) = ReadingSeriesBuilder.Build(readings, from, to, setup.Points, TimeSpan.FromMinutes(15),
            TimeZones.Resolve(Zone, out _));

        Assert.Equal(bucket, cloud.GetProperty("bucketMinutes").GetInt32());
        var cloudPoints = cloud.GetProperty("points").EnumerateArray().ToList();
        Assert.Equal(points.Count, cloudPoints.Count);
        for (var i = 0; i < points.Count; i++)
        {
            var expected = points[i];
            var actual = cloudPoints[i];
            var at = DateTimeOffset.Parse(actual.GetProperty("at").GetString()!, CultureInfo.InvariantCulture);
            Assert.True(Math.Abs((at - expected.At).TotalMilliseconds) <= 1, $"punkt {i}: {at:O} ≠ {expected.At:O}");
            Assert.Equal(expected.Count, actual.GetProperty("count").GetInt32());
            Near(expected.Moisture, actual, "moisture", i);
            Near(expected.MoistureMin, actual, "moistureMin", i);
            Near(expected.MoistureMax, actual, "moistureMax", i);
            Near(expected.Temperature, actual, "temperature", i);
            Near(expected.TemperatureMin, actual, "temperatureMin", i);
            Near(expected.TemperatureMax, actual, "temperatureMax", i);
            Assert.True(expected.GapBefore == actual.GetProperty("gapBefore").GetBoolean(), $"punkt {i}: gapBefore");
            // Światło i żyzność (Flower Care) – baza pomija klucze, gdy w punkcie nie ma takich odczytów.
            foreach (var (value, property) in new[] { (expected.Light, "light"), (expected.LightMin, "lightMin"), (expected.LightMax, "lightMax"),
                         (expected.Conductivity, "conductivity"), (expected.ConductivityMin, "conductivityMin"), (expected.ConductivityMax, "conductivityMax") })
                if (value is null) Assert.False(actual.TryGetProperty(property, out _), $"punkt {i}: {property} powinno być pominięte");
                else Near(value, actual, property, i);
        }
        Assert.Equal(readings.Min(r => r.At), DateTimeOffset.Parse(cloud.GetProperty("firstAt").GetString()!, CultureInfo.InvariantCulture));
    }

    private static void Near(double? expected, JsonElement point, string property, int index)
    {
        var value = point.GetProperty(property);
        if (expected is null)
        {
            Assert.True(value.ValueKind == JsonValueKind.Null, $"punkt {index}: {property} powinno być puste");
            return;
        }
        Assert.True(Math.Abs(expected.Value - value.GetDouble()) <= 0.011, $"punkt {index}: {property} {value.GetDouble()} ≠ {expected}");
    }

    private static List<StoredReading> Readings(string sensor, Setup setup)
    {
        var random = new Random(setup.Name.Length * 7919);
        var readings = new List<StoredReading>();
        var end = setup.Start.AddDays(setup.Days);
        for (var at = setup.Start; at < end; at = at.AddMinutes(setup.StepMinutes))
        {
            var day = (at - setup.Start).TotalDays;
            if (setup.GapsInDays.Any(g => day >= g.From && day < g.To)) continue;
            var jittered = at.AddMilliseconds(random.Next(-90_000, 90_000));
            int? moisture = random.Next(20) == 0 ? null : 20 + random.Next(60);
            decimal? temperature = random.Next(25) == 0 ? null : (150 + random.Next(120)) / 10m;
            // Co trzeci odczyt jak z Flower Care (światło, żyzność), część z samym światłem – reszta jak z Tuya.
            int? light = random.Next(3) == 0 ? random.Next(120_000) : null;
            int? conductivity = light is not null && random.Next(5) > 0 ? random.Next(3000) : null;
            readings.Add(new(sensor, jittered, moisture, temperature, 80, 2) { LightLux = light, ConductivityMicroSiemens = conductivity });
        }
        return readings;
    }

    private async Task InsertAsync(IReadOnlyList<StoredReading> readings)
    {
        foreach (var chunk in readings.Chunk(2000))
        {
            var rows = new JsonArray([.. chunk.Select(r => (JsonNode)new JsonObject
            {
                ["sensor_id"] = r.SensorId, ["at"] = r.At.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                ["moisture"] = r.MoisturePercent, ["temp_tenths"] = r.SoilTemperatureCelsius is { } t ? (int)(t * 10) : null,
                ["battery"] = r.BatteryPercent, ["battery_state"] = r.BatteryState,
                ["light_lux"] = r.LightLux, ["conductivity"] = r.ConductivityMicroSiemens
            })]);
            await db.AdminAsync("""
                insert into glebosaurus.readings (sensor_id, at, moisture, temp_tenths, battery, battery_state, light_lux, conductivity)
                select sensor_id, at, moisture, temp_tenths, battery, battery_state, light_lux, conductivity
                  from jsonb_to_recordset(@rows) as x(sensor_id text, at timestamptz, moisture smallint, temp_tenths smallint,
                                                      battery smallint, battery_state smallint, light_lux int, conductivity smallint)
                returning '{}'::text
                """, null, ("rows", rows));
        }
    }
}
