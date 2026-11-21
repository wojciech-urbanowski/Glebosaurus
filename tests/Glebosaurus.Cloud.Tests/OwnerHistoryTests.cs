using System.Text.Json;
using System.Text.Json.Nodes;

namespace Glebosaurus.Cloud.Tests;

/// <summary>Pulpit i strona czujnika w chmurze (faza 3a): alerty na pulpicie, stan czujnika, słyszenia, ostatnie odczyty.</summary>
public sealed class OwnerHistoryTests(CloudDatabase db)
{
    [Fact]
    public async Task SensorPage_ShowsStateSightingsAlertsAndReadings_WithoutKeys()
    {
        db.SkipIfUnavailable();
        var s = new Scenario(db, CloudDatabase.NextTime());
        var owner = await s.OwnerAsync();
        var sender = await s.AgentAsync("hist", canSendMail: true, priority: 1);
        var sensor = await s.SensorAsync("hist");
        await s.ClaimAsync(sender, "sensor:" + sensor, s.T0);
        var epoch = (await s.ClaimAsync(sender, "notifier", s.T0)).GetProperty("epoch").GetInt64();
        await s.SyncAsync(sender, s.T0, new JsonObject
        {
            ["sightings"] = new JsonArray(new JsonObject { ["sensor"] = sensor, ["rssi"] = -71, ["heardSecondsAgo"] = 30 }),
            ["readings"] = new JsonArray(
                new JsonObject { ["sensor"] = sensor, ["at"] = s.At(TimeSpan.FromHours(-3)).ToString("O"), ["moisture"] = 40, ["tempTenths"] = 201 },
                new JsonObject { ["sensor"] = sensor, ["at"] = s.At(TimeSpan.FromHours(-1)).ToString("O"), ["moisture"] = 12, ["tempTenths"] = 199 })
        });
        await db.AgentAsync("alert_open", s.T0, ("p_token", sender.Token), ("p_epoch", epoch), ("p_sensor", sensor), ("p_kind", "NeedsWater"),
            ("p_detail", "wilgotność 12%"));

        var page = await db.OwnerAsync("owner_sensor", owner, s.T0, ("p_id", sensor));

        Assert.Equal("gvygg3m8", page.GetProperty("sensor").GetProperty("productId").GetString());
        Assert.Equal(12, page.GetProperty("last").GetProperty("moisture").GetInt32());
        Assert.Equal(19.9, page.GetProperty("last").GetProperty("temperature").GetDouble());
        Assert.Equal(sender.Name, page.GetProperty("holder").GetProperty("agent").GetString());
        var sighting = Assert.Single(page.GetProperty("sightings").EnumerateArray());
        Assert.Equal(-71, sighting.GetProperty("rssi").GetInt32());
        Assert.Equal("NeedsWater", Assert.Single(page.GetProperty("alerts").EnumerateArray()).GetProperty("kind").GetString());
        Assert.DoesNotContain("localKey", page.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("a1b2c3d4e5f6", page.GetRawText());

        var readings = (await db.OwnerAsync("owner_readings", owner, s.T0, ("p_id", sensor), ("p_count", 1))).EnumerateArray().ToList();
        Assert.Equal(12, Assert.Single(readings).GetProperty("moisture").GetInt32());

        var dashboard = await db.OwnerAsync("owner_dashboard", owner, s.T0);
        var alert = dashboard.GetProperty("alerts").EnumerateArray().Single(a => a.GetProperty("sensor").GetString() == sensor);
        Assert.Equal("Czujnik " + sensor, alert.GetProperty("sensorName").GetString());

        await Scenario.FailsAsync("22023", () => db.OwnerAsync("owner_sensor", owner, s.T0, ("p_id", "nie-ma-takiego")));
        await Scenario.FailsAsync("22023", () => db.OwnerAsync("owner_series", owner, s.T0, ("p_sensor", sensor), ("p_from", s.T0),
            ("p_to", s.T0.AddHours(-1)), ("p_points", 100), ("p_zone", null)));
    }

    [Fact]
    public async Task Series_UnknownZone_FallsBackToReportZone()
    {
        db.SkipIfUnavailable();
        var s = new Scenario(db, CloudDatabase.NextTime());
        var owner = await s.OwnerAsync();
        var sensor = await s.SensorAsync("strefa");

        var series = await db.OwnerAsync("owner_series", owner, s.T0, ("p_sensor", sensor), ("p_from", s.T0.AddDays(-1)), ("p_to", s.T0),
            ("p_points", 100), ("p_zone", "Mars/Olympus"));

        Assert.Equal(0, series.GetProperty("points").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, series.GetProperty("firstAt").ValueKind);
    }
}
