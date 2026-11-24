using System.Text.Json;
using System.Text.Json.Nodes;

namespace Glebosaurus.Cloud.Tests;

/// <summary>
/// Czujnik Xiaomi Flower Care w chmurze (migracja 20261124220000): dodanie po samym MAC, konfiguracja agentów bez danych
/// Tuya, odmowy dla klucza i parowania, nowy adres, światło i żyzność w odczytach i na stronie.
/// </summary>
public sealed class FlowerCareTests(CloudDatabase db)
{
    private static long Version(JsonElement config) => config.GetProperty("configVersion").GetInt64();

    private async Task<long> VersionAsync(JsonObject owner) => Version(await db.OwnerAsync("owner_config", owner));

    private async Task<JsonElement> AddAsync(JsonObject owner, string id, string mac, JsonObject? options = null) =>
        await db.OwnerAsync("owner_sensor_add_flower_care", owner, null, ("p_version", await VersionAsync(owner)), ("p_id", id),
            ("p_name", "Fikus " + id), ("p_mac", mac), ("p_options", options ?? new JsonObject()));

    private static JsonElement ById(JsonElement list, string id) => list.EnumerateArray().Single(x => x.GetProperty("id").GetString() == id);

    [Fact]
    public async Task AddedByMac_GoesToAgentsWithoutTuyaData_AndRefusesKeysAndPairing()
    {
        db.SkipIfUnavailable();
        var s = new Scenario(db, CloudDatabase.NextTime());
        var owner = await s.OwnerAsync();
        var agent = await s.AgentAsync("fc");
        var id = Scenario.Unique("fikus");
        var mac = Scenario.RandomMac();

        var added = await AddAsync(owner, id, mac.ToLowerInvariant().Replace(':', '-'), new JsonObject { ["wateringBelowPercent"] = 20, ["wateringClearAbovePercent"] = 30 });
        Assert.Equal(mac, added.GetProperty("mac").GetString());

        var forAgent = ById((await db.AgentAsync("get_config", s.T0, ("p_token", agent.Token))).GetProperty("sensors"), id);
        Assert.Equal(("flower_care", mac, 20), (forAgent.GetProperty("model").GetString(), forAgent.GetProperty("mac").GetString(),
            forAgent.GetProperty("wateringBelowPercent").GetInt32()));
        Assert.Equal(JsonValueKind.Null, forAgent.GetProperty("tuya").ValueKind);
        var tuyaSensor = await s.SensorAsync("tuya");
        Assert.Equal("tuya", ById((await db.AgentAsync("get_config", s.T0, ("p_token", agent.Token))).GetProperty("sensors"), tuyaSensor)
            .GetProperty("model").GetString());

        var config = ById((await db.OwnerAsync("owner_config", owner)).GetProperty("sensors"), id);
        Assert.Equal(("flower_care", JsonValueKind.Null), (config.GetProperty("model").GetString(), config.GetProperty("keySetAt").ValueKind));
        var page = (await db.OwnerAsync("owner_sensor", owner, null, ("p_id", id))).GetProperty("sensor");
        Assert.Equal(("flower_care", JsonValueKind.Null), (page.GetProperty("model").GetString(), page.GetProperty("productId").ValueKind));

        // Tego samego adresu nie może mieć drugi czujnik; zły adres – odmowa z przykładem.
        var duplicate = await Scenario.FailsAsync("22023", () => AddAsync(owner, Scenario.Unique("drugi"), mac));
        Assert.Contains("ten sam adres MAC", duplicate.MessageText);
        Assert.Contains("glebosaurus scan", (await Scenario.FailsAsync("22023", () => AddAsync(owner, Scenario.Unique("zly"), "5C:85:7E"))).MessageText);

        // Klucz Tuya i parowanie zdalne dotyczą tylko czujników Tuya.
        var key = await Scenario.FailsAsync("22023", async () => await db.OwnerAsync("owner_sensor_credentials", owner, null,
            ("p_version", await VersionAsync(owner)), ("p_id", id), ("p_credentials", Scenario.Credentials(Scenario.RandomMac()))));
        Assert.Contains("Flower Care", key.MessageText);
        var pairing = await Scenario.FailsAsync("22023", () => db.OwnerAsync("owner_pair_start", owner, null, ("p_agent", agent.Name),
            ("p_sensor", id), ("p_name", "X"), ("p_mac", null)));
        Assert.Contains("nie wymaga parowania", pairing.MessageText);
    }

    [Fact]
    public async Task NewMac_OnlyForFlowerCare()
    {
        db.SkipIfUnavailable();
        var s = new Scenario(db, CloudDatabase.NextTime());
        var owner = await s.OwnerAsync();
        var id = Scenario.Unique("wymiana");
        await AddAsync(owner, id, Scenario.RandomMac());
        var newMac = Scenario.RandomMac();

        var changed = await db.OwnerAsync("owner_sensor_mac", owner, null, ("p_version", await VersionAsync(owner)), ("p_id", id),
            ("p_mac", newMac.ToLowerInvariant()));

        Assert.Equal(newMac, changed.GetProperty("mac").GetString());
        Assert.Equal(newMac, ById((await db.OwnerAsync("owner_config", owner)).GetProperty("sensors"), id).GetProperty("mac").GetString());
        var tuya = await s.SensorAsync("tuya");
        var refused = await Scenario.FailsAsync("22023", async () => await db.OwnerAsync("owner_sensor_mac", owner, null,
            ("p_version", await VersionAsync(owner)), ("p_id", tuya), ("p_mac", Scenario.RandomMac())));
        Assert.Contains("czujnik Tuya", refused.MessageText);
    }

    [Fact]
    public async Task LightAndConductivity_TravelThroughSyncRangeFeedAndOwnerPages_OnlyWhenPresent()
    {
        db.SkipIfUnavailable();
        var s = new Scenario(db, CloudDatabase.NextTime());
        var owner = await s.OwnerAsync();
        var agent = await s.AgentAsync("fcr");
        var id = Scenario.Unique("fikus");
        await AddAsync(owner, id, Scenario.RandomMac());
        var tuya = await s.SensorAsync("tuya");
        var at = s.At(TimeSpan.FromMinutes(-5));

        var sync = await s.SyncAsync(agent, s.T0, new JsonObject
        {
            ["readings"] = new JsonArray(
                new JsonObject { ["sensor"] = id, ["at"] = at.ToString("O"), ["moisture"] = 41, ["tempTenths"] = 214, ["battery"] = 100,
                                 ["light"] = 82386, ["conductivity"] = 1607 },
                new JsonObject { ["sensor"] = id, ["at"] = at.AddHours(-1).ToString("O"), ["moisture"] = 42, ["light"] = 200_001 },
                new JsonObject { ["sensor"] = id, ["at"] = at.AddHours(-2).ToString("O"), ["moisture"] = 42, ["conductivity"] = 20_001 },
                new JsonObject { ["sensor"] = tuya, ["at"] = at.ToString("O"), ["moisture"] = 30, ["tempTenths"] = 190, ["battery"] = 70, ["batteryState"] = 1 })
        });
        var readings = sync.GetProperty("readings");
        Assert.Equal(2, readings.GetProperty("inserted").GetInt32());
        Assert.Equal(["out_of_range", "out_of_range"], readings.GetProperty("rejected").EnumerateArray().Select(r => r.GetProperty("reason").GetString()));

        var range = await db.AgentAsync("readings_range", s.T0, ("p_token", agent.Token), ("p_sensors", new[] { id, tuya }),
            ("p_from", at.AddHours(-1)), ("p_to", at.AddHours(1)));
        var flower = range.EnumerateArray().Single(r => r.GetProperty("sensor").GetString() == id);
        Assert.Equal((82386, 1607), (flower.GetProperty("light").GetInt32(), flower.GetProperty("conductivity").GetInt32()));
        Assert.False(range.EnumerateArray().Single(r => r.GetProperty("sensor").GetString() == tuya).TryGetProperty("light", out _));

        var feed = await db.AgentAsync("readings_feed", s.At(TimeSpan.FromMinutes(5)), ("p_token", agent.Token), ("p_after", 0L), ("p_limit", 10000));
        Assert.Contains(feed.GetProperty("readings").EnumerateArray(), r => r.GetProperty("sensor").GetString() == id
            && r.GetProperty("light").GetInt32() == 82386);

        var last = ById((await db.OwnerAsync("owner_dashboard", owner, s.T0)).GetProperty("sensors"), id);
        Assert.Equal(("flower_care", 82386, 1607), (last.GetProperty("model").GetString(),
            last.GetProperty("last").GetProperty("light").GetInt32(), last.GetProperty("last").GetProperty("conductivity").GetInt32()));
        var list = await db.OwnerAsync("owner_readings", owner, null, ("p_id", id), ("p_count", 10));
        Assert.Equal(1607, list.EnumerateArray().Single().GetProperty("conductivity").GetInt32());
        var sensorPage = await db.OwnerAsync("owner_sensor", owner, s.T0, ("p_id", id));
        Assert.Equal(82386, sensorPage.GetProperty("last").GetProperty("light").GetInt32());
    }
}
