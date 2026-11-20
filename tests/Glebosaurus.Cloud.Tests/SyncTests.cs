using System.Text.Json;
using System.Text.Json.Nodes;

namespace Glebosaurus.Cloud.Tests;

/// <summary>agent_sync: odczyty (idempotentne, odrzucone z powodem), słyszenia, dzierżawy w jednym wywołaniu, konfiguracja.</summary>
public sealed class SyncTests(CloudDatabase db)
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    private static JsonObject Reading(string sensor, DateTimeOffset at, int? moisture = 40) => new()
    {
        ["sensor"] = sensor, ["at"] = at.ToString("O"), ["moisture"] = moisture, ["tempTenths"] = 215, ["battery"] = 80, ["batteryState"] = 2
    };

    [Fact]
    public async Task Readings_AreStoredOnce_AndBadOnesComeBackWithReason()
    {
        db.SkipIfUnavailable();
        var s = new Scenario(db, CloudDatabase.NextTime());
        var sensor = await s.SensorAsync();
        var agent = await s.AgentAsync("a");
        var payload = new JsonObject
        {
            ["readings"] = new JsonArray(
                Reading(sensor, s.At(-30 * Minute)), Reading(sensor, s.At(-15 * Minute)), Reading(sensor, s.T0),
                Reading(sensor, s.T0),
                Reading(Scenario.Unique("brak"), s.T0),
                Reading(sensor, s.At(TimeSpan.FromHours(1))),
                Reading(sensor, s.At(-Minute), moisture: 140))
        };

        var first = (await s.SyncAsync(agent, s.T0, payload.DeepClone().AsObject())).GetProperty("readings");
        Assert.Equal(7, first.GetProperty("received").GetInt32());
        Assert.Equal(3, first.GetProperty("inserted").GetInt32());
        Assert.Equal(new[] { "future", "out_of_range", "unknown_sensor" },
            first.GetProperty("rejected").EnumerateArray().Select(r => r.Text("reason")!).Order().ToArray());

        var second = (await s.SyncAsync(agent, s.At(Minute), payload)).GetProperty("readings");
        Assert.Equal(0, second.GetProperty("inserted").GetInt32());

        var stored = await db.AgentAsync("readings_range", s.At(Minute), ("p_token", agent.Token), ("p_sensors", new[] { sensor }),
            ("p_from", s.At(-TimeSpan.FromHours(1))), ("p_to", s.At(Minute)));
        Assert.Equal(3, stored.GetArrayLength());
        Assert.Equal(215, stored[0].GetProperty("tempTenths").GetInt32());
        Assert.Equal(s.At(-30 * Minute), stored[0].GetProperty("at").GetDateTimeOffset());
    }

    [Fact]
    public async Task Sightings_OneRowPerAgentAndSensor_ShowBestOtherAgent()
    {
        db.SkipIfUnavailable();
        var s = new Scenario(db, CloudDatabase.NextTime());
        var sensor = await s.SensorAsync();
        var a = await s.AgentAsync("a");
        var b = await s.AgentAsync("b");
        JsonObject Heard(int rssi) => new() { ["sightings"] = new JsonArray(new JsonObject { ["sensor"] = sensor, ["rssi"] = rssi, ["heardSecondsAgo"] = 20 }) };

        await s.SyncAsync(a, s.T0, Heard(-84));
        await s.SyncAsync(a, s.At(Minute), Heard(-80));
        await s.SyncAsync(b, s.At(Minute), Heard(-70));

        var forA = (await s.SyncAsync(a, s.At(2 * Minute))).GetProperty("sensors").Single("sensor", sensor).GetProperty("bestOther");
        Assert.Equal(b.Name, forA.Text("agent"));
        Assert.Equal(-70, forA.GetProperty("rssi").GetInt32());
        Assert.Equal(80, forA.GetProperty("heardSecondsAgo").GetDouble());

        var forB = (await s.SyncAsync(b, s.At(2 * Minute))).GetProperty("sensors").Single("sensor", sensor).GetProperty("bestOther");
        Assert.Equal(-80, forB.GetProperty("rssi").GetInt32());

        var rows = await db.AdminAsync("select to_jsonb(count(*))::text from glebosaurus.sightings where sensor_id = @s", null, ("s", sensor));
        Assert.Equal(2, rows.GetInt32());

        // Słyszenie sprzed ponad 15 min nie liczy się do oddawania czujnika.
        var stale = (await s.SyncAsync(a, s.At(20 * Minute))).GetProperty("sensors").Single("sensor", sensor).GetProperty("bestOther");
        Assert.Equal(System.Text.Json.JsonValueKind.Null, stale.ValueKind);
    }

    [Fact]
    public async Task Sync_ReleasesThenClaims_AndShowsHolders()
    {
        db.SkipIfUnavailable();
        var s = new Scenario(db, CloudDatabase.NextTime());
        var first = await s.SensorAsync();
        var second = await s.SensorAsync();
        var a = await s.AgentAsync("a");
        var b = await s.AgentAsync("b");

        var sync = await s.SyncAsync(a, s.T0, new() { ["claims"] = new JsonArray("sensor:" + second, "sensor:" + first) });
        var leases = sync.GetProperty("leases");
        Assert.True(leases.Single("resource", "sensor:" + first).Granted());
        Assert.True(leases.Single("resource", "sensor:" + second).Granted());
        var epoch = leases.Single("resource", "sensor:" + first).Epoch();

        var forB = await s.SyncAsync(b, s.At(Minute), new() { ["claims"] = new JsonArray("sensor:" + first) });
        Assert.False(forB.GetProperty("leases")[0].Granted());
        Assert.Equal(a.Name, forB.GetProperty("sensors").Single("sensor", first).Text("holder"));

        var handover = await s.SyncAsync(a, s.At(2 * Minute), new()
        {
            ["releases"] = new JsonArray(new JsonObject { ["resource"] = "sensor:" + first, ["epoch"] = epoch }),
            ["claims"] = new JsonArray("sensor:" + second)
        });
        Assert.True(handover.GetProperty("released")[0].GetProperty("released").GetBoolean());
        Assert.True((await s.ClaimAsync(b, "sensor:" + first, s.At(3 * Minute))).Granted());
    }

    [Fact]
    public async Task Sync_RequiresProtocolOne_AndLimitsBatch()
    {
        db.SkipIfUnavailable();
        var s = new Scenario(db, CloudDatabase.NextTime());
        var agent = await s.AgentAsync("a");

        await Scenario.FailsAsync("22023", () => db.AgentAsync("agent_sync", s.T0, ("p_token", agent.Token), ("p_payload", new JsonObject())));
        await Scenario.FailsAsync("22023", () => s.SyncAsync(agent, s.T0, new() { ["protocol"] = 2 }));
        var tooMany = new JsonArray(Enumerable.Range(0, 1001).Select(_ => (JsonNode)new JsonObject()).ToArray());
        await Scenario.FailsAsync("22023", () => s.SyncAsync(agent, s.T0, new() { ["readings"] = tooMany }));
    }

    [Fact]
    public async Task Heartbeat_IsStored_AndAgentsListShowsSilence()
    {
        db.SkipIfUnavailable();
        var s = new Scenario(db, CloudDatabase.NextTime());
        var a = await s.AgentAsync("a");
        var b = await s.AgentAsync("b", canSendMail: true, priority: 1);
        await s.SyncAsync(a, s.T0, new() { ["status"] = new JsonObject { ["version"] = "0.2.0", ["ble"] = "ok" } });

        var sync = await s.SyncAsync(b, s.At(10 * Minute));
        var seenA = sync.GetProperty("agents").Single("name", a.Name);
        Assert.Equal(600, seenA.GetProperty("lastSeenSecondsAgo").GetDouble());
        Assert.True(sync.GetProperty("agents").Single("name", b.Name).GetProperty("canSendMail").GetBoolean());

        var status = await db.AdminAsync("select last_status::text from glebosaurus.agents where name = @n", null, ("n", a.Name));
        Assert.Equal("0.2.0", status.Text("version"));
    }

    [Fact]
    public async Task AgentsList_HasEachAgentsHeartbeat_OnlyKnownFieldsOfRightType()
    {
        db.SkipIfUnavailable();
        var s = new Scenario(db, CloudDatabase.NextTime());
        var phone = await s.AgentAsync("telefon");
        var pc = await s.AgentAsync("pc");
        var odd = await s.AgentAsync("dziwny");
        var pi = await s.AgentAsync("malina");
        await s.SyncAsync(phone, s.T0, new()
        {
            ["status"] = new JsonObject
            {
                ["version"] = "0.2.3", ["platform"] = "android", ["ble"] = "ok", ["batteryPercent"] = 80, ["batteryTemperatureCelsius"] = 31.46,
                ["charging"] = true, ["network"] = "wifi", ["wifiDbm"] = -58, ["thermalStatus"] = "light", ["inne"] = "nie przechodzi"
            }
        });
        // Zły typ albo wartość spoza zakresu nie może zepsuć innym agentom synchronizacji – pole znika.
        await s.SyncAsync(pc, s.T0, new()
        {
            ["status"] = new JsonObject
            {
                ["version"] = "0.2.3", ["platform"] = "windows", ["ble"] = 1, ["batteryPercent"] = "80", ["batteryTemperatureCelsius"] = 400,
                ["charging"] = "tak", ["network"] = "5g", ["wifiDbm"] = 12.5, ["thermalStatus"] = 3, ["cpuTemperatureCelsius"] = 400
            }
        });
        // Raspberry Pi: temperatura procesora (migracja 20261120120000).
        await s.SyncAsync(pi, s.T0, new()
        {
            ["status"] = new JsonObject { ["version"] = "0.2.6", ["platform"] = "linux", ["ble"] = "wyłączony", ["cpuTemperatureCelsius"] = 47.26 }
        });
        await s.SyncAsync(odd, s.T0, new() { ["status"] = new JsonObject { ["ble"] = "ok", ["batteryPercent"] = 50 } });   // bez wersji

        // Listę czyta inny agent – synchronizacja bez statusu nadpisuje własny sygnał życia pytającego.
        var agents = (await s.SyncAsync(await s.AgentAsync("panel"), s.At(Minute))).GetProperty("agents");

        var phoneStatus = agents.Single("name", phone.Name).GetProperty("status");
        Assert.Equal(["batteryPercent", "batteryTemperatureCelsius", "ble", "charging", "network", "platform", "thermalStatus", "version", "wifiDbm"],
            phoneStatus.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal((80, 31.5, true, -58), (phoneStatus.GetProperty("batteryPercent").GetInt32(),
            phoneStatus.GetProperty("batteryTemperatureCelsius").GetDouble(), phoneStatus.GetProperty("charging").GetBoolean(),
            phoneStatus.GetProperty("wifiDbm").GetInt32()));
        Assert.Equal(("android", "wifi", "light"), (phoneStatus.Text("platform"), phoneStatus.Text("network"), phoneStatus.Text("thermalStatus")));
        Assert.Equal(["platform", "version"],
            agents.Single("name", pc.Name).GetProperty("status").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(JsonValueKind.Null, agents.Single("name", odd.Name).GetProperty("status").ValueKind);
        var piStatus = agents.Single("name", pi.Name).GetProperty("status");
        Assert.Equal(["ble", "cpuTemperatureCelsius", "platform", "version"],
            piStatus.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(47.3, piStatus.GetProperty("cpuTemperatureCelsius").GetDouble());
    }

    [Fact]
    public async Task Config_HasSensorsWithKeys_VersionGrowsOnChange_MailOnlyForSenders()
    {
        db.SkipIfUnavailable();
        var s = new Scenario(db, CloudDatabase.NextTime());
        var sensor = await s.SensorAsync(options: new() { ["wateringBelowPercent"] = 30, ["wateringClearAbovePercent"] = 40 });
        var collector = await s.AgentAsync("android");
        var sender = await s.AgentAsync("pi", canSendMail: true, priority: 1);

        var config = await db.AgentAsync("get_config", null, ("p_token", collector.Token));
        var entry = config.GetProperty("sensors").Single("id", sensor);
        Assert.Equal(30, entry.GetProperty("wateringBelowPercent").GetInt32());
        Assert.Equal("a1b2c3d4e5f6", entry.GetProperty("tuya").Text("localKeyHex"));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, config.GetProperty("mail").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Array, (await db.AgentAsync("get_config", null, ("p_token", sender.Token)))
            .GetProperty("mail").GetProperty("alertRecipients").ValueKind);

        var before = (await s.SyncAsync(collector, s.T0)).GetProperty("configVersion").GetInt64();
        await db.AdminAsync("select to_jsonb(null::text) from glebosaurus.admin_sensor_upsert(@id, 'Nowa nazwa', @mac, '{}')", null,
            ("id", sensor), ("mac", entry.Text("mac")));
        var after = (await s.SyncAsync(collector, s.At(Minute))).GetProperty("configVersion").GetInt64();
        Assert.True(after > before);
    }

    [Fact]
    public async Task Admin_RejectsBadConfiguration()
    {
        db.SkipIfUnavailable();
        var s = new Scenario(db, CloudDatabase.NextTime());
        var sensor = await s.SensorAsync(withCredentials: false);

        await Scenario.FailsAsync("22023", () => db.AdminAsync("select to_jsonb(null::text) from glebosaurus.admin_settings_update(@p)", null,
            ("p", new JsonObject { ["reportTime"] = new JsonArray("07:00") })));
        await Scenario.FailsAsync("23514", () => db.AdminAsync("select to_jsonb(null::text) from glebosaurus.admin_settings_update(@p)", null,
            ("p", new JsonObject { ["reportTimes"] = new JsonArray("7:00") })));
        await Scenario.FailsAsync("22023", () => db.AdminAsync("select to_jsonb(null::text) from glebosaurus.admin_sensor_credentials(@id, @c)", null,
            ("id", sensor), ("c", Scenario.Credentials(Scenario.RandomMac()))));
        await Scenario.FailsAsync("23514", () => db.AdminAsync("select to_jsonb(null::text) from glebosaurus.admin_sensor_upsert(@id, 'x', @mac, @o)", null,
            ("id", Scenario.Unique("s")), ("mac", Scenario.RandomMac()), ("o", new JsonObject { ["wateringBelowPercent"] = 50, ["wateringClearAbovePercent"] = 40 })));
    }
}
