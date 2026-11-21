using System.Text.Json;
using System.Text.Json.Nodes;

namespace Glebosaurus.Cloud.Tests;

/// <summary>Parowanie zdalne (faza 3d): polecenie do wskazanego agenta, postęp, klucz w Vault przed wysłaniem do czujnika,
/// czujnik zakładany przez bazę po weryfikacji, nikt poza wskazanym agentem nie dotyka klucza.</summary>
public sealed class OwnerPairingTests(CloudDatabase db)
{
    [Fact]
    public async Task RemotePairing_FromStartToNewSensor()
    {
        db.SkipIfUnavailable();
        var s = new Scenario(db, CloudDatabase.NextTime());
        var owner = await s.OwnerAsync();
        var pc = await s.AgentAsync("pc");
        var other = await s.AgentAsync("inny");
        var sensor = Scenario.Unique("zdalny");
        var mac = Scenario.RandomMac();

        var created = await db.OwnerAsync("owner_pair_start", owner, s.T0, ("p_agent", pc.Name), ("p_sensor", sensor), ("p_name", "Zdalny"),
            ("p_mac", null));
        var id = created.GetProperty("id").GetInt64();
        Assert.False((await db.OwnerAsync("owner_pair_start", owner, s.T0, ("p_agent", pc.Name), ("p_sensor", sensor), ("p_name", "Zdalny"),
            ("p_mac", null))).GetProperty("created").GetBoolean());              // jedno parowanie naraz

        // Polecenie dostaje tylko wskazany agent.
        Assert.DoesNotContain((await s.SyncAsync(other, s.T0)).GetProperty("commands").EnumerateArray(), c => c.GetProperty("id").GetInt64() == id);
        var command = (await s.SyncAsync(pc, s.T0)).GetProperty("commands").EnumerateArray().Single(c => c.GetProperty("id").GetInt64() == id);
        Assert.Equal(sensor, command.GetProperty("params").GetProperty("sensor").GetString());

        Assert.True((await db.AgentAsync("command_progress", s.T0, ("p_token", pc.Token), ("p_id", id),
            ("p_progress", new JsonObject { ["stage"] = "scanning", ["text"] = "skan działa" }))).GetBoolean());
        var status = (await db.OwnerAsync("owner_command_status", owner, s.T0, ("p_ids", new[] { id }))).EnumerateArray().Single();
        Assert.Equal("scanning", status.GetProperty("progress").GetProperty("stage").GetString());
        Assert.Equal(sensor, status.GetProperty("sensor").GetString());

        Assert.Equal(JsonValueKind.Null, (await db.AgentAsync("pair_key_get", s.T0, ("p_token", pc.Token), ("p_id", id))).GetProperty("credentials").ValueKind);
        var key = Scenario.Credentials(mac);
        await Scenario.FailsAsync("22023", () => db.AgentAsync("pair_key_put", s.T0, ("p_token", other.Token), ("p_id", id), ("p_credentials", key)));
        Assert.True((await db.AgentAsync("pair_key_put", s.T0, ("p_token", pc.Token), ("p_id", id), ("p_credentials", key))).GetBoolean());
        Assert.Equal(key["localKeyHex"]!.GetValue<string>(),
            (await db.AgentAsync("pair_key_get", s.T0, ("p_token", pc.Token), ("p_id", id))).GetProperty("credentials").GetProperty("localKeyHex").GetString());

        var done = await db.AgentAsync("pair_complete", s.T0, ("p_token", pc.Token), ("p_id", id), ("p_result", new JsonObject { ["moisture"] = 41 }));
        Assert.True(done.GetProperty("created").GetBoolean());
        Assert.Equal(mac, done.GetProperty("mac").GetString());

        var config = await db.AgentAsync("get_config", s.T0, ("p_token", other.Token));
        var forAgent = config.GetProperty("sensors").EnumerateArray().Single(x => x.GetProperty("id").GetString() == sensor);
        Assert.Equal("Zdalny", forAgent.GetProperty("name").GetString());
        Assert.Equal(key["localKeyHex"]!.GetValue<string>(), forAgent.GetProperty("tuya").GetProperty("localKeyHex").GetString());
        var pending = await db.AdminAsync("select to_jsonb(count(*))::text from vault.secrets where name = @n", null, ("n", "glebosaurus:pairing:" + sensor));
        Assert.Equal(0, pending.GetInt32());
        status = (await db.OwnerAsync("owner_command_status", owner, s.T0, ("p_ids", new[] { id }))).EnumerateArray().Single();
        Assert.True(status.GetProperty("result").GetProperty("ok").GetBoolean());
        Assert.Equal(41, status.GetProperty("result").GetProperty("moisture").GetInt32());

        // Po wykonaniu polecenia klucza już nie da się ani wziąć, ani podmienić.
        await Scenario.FailsAsync("22023", () => db.AgentAsync("pair_key_get", s.T0, ("p_token", pc.Token), ("p_id", id)));
    }

    [Fact]
    public async Task RePairingExistingSensor_GetsItsKey_AndReplacesMac()
    {
        db.SkipIfUnavailable();
        var s = new Scenario(db, CloudDatabase.NextTime());
        var owner = await s.OwnerAsync();
        var pc = await s.AgentAsync("pc2");
        var sensor = await s.SensorAsync("reset");
        var id = (await db.OwnerAsync("owner_pair_start", owner, s.T0, ("p_agent", pc.Name), ("p_sensor", sensor), ("p_name", null),
            ("p_mac", "dc-23-51-00-00-0a"))).GetProperty("id").GetInt64();
        await s.SyncAsync(pc, s.T0);

        // Po resecie przyciskiem: ten sam klucz co dotąd.
        var existing = (await db.AgentAsync("pair_key_get", s.T0, ("p_token", pc.Token), ("p_id", id))).GetProperty("credentials");
        Assert.Equal("a1b2c3d4e5f6", existing.GetProperty("localKeyHex").GetString());

        var newMac = Scenario.RandomMac();
        await db.AgentAsync("pair_key_put", s.T0, ("p_token", pc.Token), ("p_id", id), ("p_credentials", Scenario.Credentials(newMac)));
        var done = await db.AgentAsync("pair_complete", s.T0, ("p_token", pc.Token), ("p_id", id), ("p_result", new JsonObject()));
        Assert.False(done.GetProperty("created").GetBoolean());
        var config = await db.OwnerAsync("owner_config", owner, s.T0);
        Assert.Equal(newMac, config.GetProperty("sensors").EnumerateArray().Single(x => x.GetProperty("id").GetString() == sensor).GetProperty("mac").GetString());
    }

    [Fact]
    public async Task PairStart_RefusesBadInput()
    {
        db.SkipIfUnavailable();
        var s = new Scenario(db, CloudDatabase.NextTime());
        var owner = await s.OwnerAsync();
        var pc = await s.AgentAsync("pc3");
        var notEnrolled = Scenario.Unique("bez");
        await db.OwnerAsync("owner_agent_save", owner, null, ("p_name", notEnrolled), ("p_can_send_mail", false), ("p_sender_priority", null), ("p_enabled", true));

        await Scenario.FailsAsync("22023", () => db.OwnerAsync("owner_pair_start", owner, s.T0, ("p_agent", notEnrolled), ("p_sensor", "x1"), ("p_name", null), ("p_mac", null)));
        await Scenario.FailsAsync("22023", () => db.OwnerAsync("owner_pair_start", owner, s.T0, ("p_agent", pc.Name), ("p_sensor", "Złe Id"), ("p_name", null), ("p_mac", null)));
        await Scenario.FailsAsync("22023", () => db.OwnerAsync("owner_pair_start", owner, s.T0, ("p_agent", pc.Name), ("p_sensor", "x2"), ("p_name", null), ("p_mac", "nie-mac")));
        await Scenario.FailsAsync("22023", () => db.OwnerAsync("owner_pair_start", owner, s.T0, ("p_agent", "nie-ma-agenta"), ("p_sensor", "x3"), ("p_name", null), ("p_mac", null)));
    }
}
