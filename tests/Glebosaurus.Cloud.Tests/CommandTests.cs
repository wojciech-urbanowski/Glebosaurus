using System.Text.Json;
using System.Text.Json.Nodes;

namespace Glebosaurus.Cloud.Tests;

/// <summary>Polecenia: trafiają tam, gdzie jest władza (dzierżawa czujnika, blokada nadawcy albo wskazany agent).</summary>
public sealed class CommandTests(CloudDatabase db)
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    private Task<JsonElement> CreateAsync(TestAgent agent, string kind, string? sensor, DateTimeOffset at) =>
        db.AgentAsync("command_create", at, ("p_token", agent.Token), ("p_kind", kind), ("p_sensor", sensor));

    private static long[] Ids(JsonElement sync) =>
        [.. sync.GetProperty("commands").EnumerateArray().Select(c => c.GetProperty("id").GetInt64())];

    [Fact]
    public async Task ReadNow_GoesToSensorHolder_UntilDone()
    {
        db.SkipIfUnavailable();
        var s = new Scenario(db, CloudDatabase.NextTime());
        var sensor = await s.SensorAsync();
        var bestia = await s.AgentAsync("bestia");
        var android = await s.AgentAsync("android");
        await s.ClaimAsync(android, "sensor:" + sensor, s.T0);

        var created = await CreateAsync(bestia, "read_now", sensor, s.At(Minute));
        Assert.True(created.GetProperty("created").GetBoolean());
        var id = created.GetProperty("id").GetInt64();

        Assert.DoesNotContain(id, Ids(await s.SyncAsync(bestia, s.At(Minute))));
        var forAndroid = await s.SyncAsync(android, s.At(Minute), new() { ["claims"] = new JsonArray("sensor:" + sensor) });
        var command = forAndroid.GetProperty("commands").Single("kind", "read_now");
        Assert.Equal(sensor, command.Text("sensor"));
        Assert.Contains(id, Ids(await s.SyncAsync(android, s.At(2 * Minute))));

        Assert.False((await db.AgentAsync("command_done", s.At(2 * Minute), ("p_token", bestia.Token), ("p_id", id),
            ("p_result", new JsonObject()))).GetBoolean());
        Assert.True((await db.AgentAsync("command_done", s.At(2 * Minute), ("p_token", android.Token), ("p_id", id),
            ("p_result", new JsonObject { ["ok"] = true }))).GetBoolean());
        Assert.DoesNotContain(id, Ids(await s.SyncAsync(android, s.At(3 * Minute))));
    }

    [Fact]
    public async Task SameRequest_AtMostOncePerMinute()
    {
        db.SkipIfUnavailable();
        var s = new Scenario(db, CloudDatabase.NextTime());
        var sensor = await s.SensorAsync();
        var bestia = await s.AgentAsync("bestia");

        var first = (await CreateAsync(bestia, "read_now", sensor, s.T0)).GetProperty("id").GetInt64();
        var again = await CreateAsync(bestia, "read_now", sensor, s.At(TimeSpan.FromSeconds(30)));
        Assert.False(again.GetProperty("created").GetBoolean());
        Assert.Equal(first, again.GetProperty("id").GetInt64());

        // Nikt nie trzyma czujnika – polecenie czeka; po wygaśnięciu (10 min) można zlecić nowe.
        Assert.False((await CreateAsync(bestia, "read_now", sensor, s.At(5 * Minute))).GetProperty("created").GetBoolean());
        Assert.True((await CreateAsync(bestia, "read_now", sensor, s.At(11 * Minute))).GetProperty("created").GetBoolean());
    }

    [Fact]
    public async Task ReportNow_GoesToCurrentNotifier()
    {
        db.SkipIfUnavailable();
        var s = new Scenario(db, CloudDatabase.NextTime());
        var android = await s.AgentAsync("android");
        var pi = await s.AgentAsync("pi", canSendMail: true, priority: 1);
        await s.ClaimAsync(pi, "notifier", s.T0);

        var id = (await CreateAsync(android, "report_now", null, s.At(Minute))).GetProperty("id").GetInt64();

        Assert.DoesNotContain(id, Ids(await s.SyncAsync(android, s.At(Minute))));
        Assert.Contains(id, Ids(await s.SyncAsync(pi, s.At(Minute), new() { ["claims"] = new JsonArray("notifier") })));
    }

    [Fact]
    public async Task OwnerCommands_GoToNamedAgent_AndAgentsCannotIssueThem()
    {
        db.SkipIfUnavailable();
        var s = new Scenario(db, CloudDatabase.NextTime());
        var sensor = await s.SensorAsync();
        var bestia = await s.AgentAsync("bestia");
        var android = await s.AgentAsync("android");

        var pair = await db.AdminAsync("select glebosaurus.admin_command('pair', @s, @a, @p)::text", s.T0,
            ("s", sensor), ("a", bestia.Name), ("p", new JsonObject { ["confirmBind"] = true }));
        var id = pair.GetProperty("id").GetInt64();

        var forBestia = await s.SyncAsync(bestia, s.At(Minute));
        Assert.Contains(id, Ids(forBestia));
        Assert.True(forBestia.GetProperty("commands").Single("kind", "pair").GetProperty("params").GetProperty("confirmBind").GetBoolean());
        Assert.DoesNotContain(id, Ids(await s.SyncAsync(android, s.At(Minute))));
        Assert.DoesNotContain(id, Ids(await s.SyncAsync(bestia, s.At(16 * Minute))));

        await Scenario.FailsAsync("22023", () => CreateAsync(android, "pair", sensor, s.T0));
        await Scenario.FailsAsync("22023", () => CreateAsync(android, "release", sensor, s.T0));
        await Scenario.FailsAsync("22023", () => CreateAsync(android, "read_now", null, s.T0));
        await Scenario.FailsAsync("22023", () => db.AdminAsync("select glebosaurus.admin_command('send_log')::text", s.T0));
    }

    [Fact]
    public async Task Release_FollowsCurrentHolder()
    {
        db.SkipIfUnavailable();
        var s = new Scenario(db, CloudDatabase.NextTime());
        var sensor = await s.SensorAsync();
        var bestia = await s.AgentAsync("bestia");
        var android = await s.AgentAsync("android");
        var epoch = (await s.ClaimAsync(bestia, "sensor:" + sensor, s.T0)).Epoch();
        var id = (await db.AdminAsync("select glebosaurus.admin_command('release', @s)::text", s.T0, ("s", sensor)))
            .GetProperty("id").GetInt64();

        Assert.Contains(id, Ids(await s.SyncAsync(bestia, s.At(Minute))));
        await s.ReleaseAsync(bestia, "sensor:" + sensor, epoch, s.At(Minute));
        await s.ClaimAsync(android, "sensor:" + sensor, s.At(2 * Minute));

        Assert.DoesNotContain(id, Ids(await s.SyncAsync(bestia, s.At(2 * Minute))));
        Assert.Contains(id, Ids(await s.SyncAsync(android, s.At(2 * Minute))));
    }
}
