namespace Glebosaurus.Cloud.Tests;

public sealed class EnrollmentTests(CloudDatabase db)
{
    [Fact]
    public async Task Code_GivesTokenOnlyOnce()
    {
        db.SkipIfUnavailable();
        var scenario = new Scenario(db, CloudDatabase.NextTime());
        var name = Scenario.Unique("agent");
        var code = await scenario.CodeAsync(name, false, null, scenario.T0);
        Assert.StartsWith("glbe_", code);

        var enrolled = await db.AgentAsync("enroll", scenario.At(TimeSpan.FromMinutes(1)), ("p_code", code));
        Assert.Equal(name, enrolled.Text("agent"));
        Assert.StartsWith("glb_", enrolled.Text("token"));

        await Scenario.FailsAsync("28000", () => db.AgentAsync("enroll", scenario.At(TimeSpan.FromMinutes(2)), ("p_code", code)));
    }

    [Fact]
    public async Task Code_Expires()
    {
        db.SkipIfUnavailable();
        var scenario = new Scenario(db, CloudDatabase.NextTime());
        var code = await scenario.CodeAsync(Scenario.Unique("agent"), false, null, scenario.T0, TimeSpan.FromHours(1));

        await Scenario.FailsAsync("28000", () => db.AgentAsync("enroll", scenario.At(TimeSpan.FromMinutes(61)), ("p_code", code)));
    }

    [Fact]
    public async Task UnknownToken_IsRejected()
    {
        db.SkipIfUnavailable();
        await Scenario.FailsAsync("28000", () => db.AgentAsync("get_config", null, ("p_token", "glb_nieznany")));
        await Scenario.FailsAsync("28000", () => db.AgentAsync("get_config", null, ("p_token", null)));
    }

    [Fact]
    public async Task Revoke_CutsAgentOffImmediately()
    {
        db.SkipIfUnavailable();
        var scenario = new Scenario(db, CloudDatabase.NextTime());
        var agent = await scenario.AgentAsync("agent");
        await scenario.SyncAsync(agent, scenario.T0);

        await db.AdminAsync("select to_jsonb(glebosaurus.admin_agent_revoke(@n))::text", null, ("n", agent.Name));

        await Scenario.FailsAsync("28000", () => scenario.SyncAsync(agent, scenario.At(TimeSpan.FromMinutes(1))));
    }

    [Fact]
    public async Task Reenrollment_ReplacesOldToken()
    {
        db.SkipIfUnavailable();
        var scenario = new Scenario(db, CloudDatabase.NextTime());
        var old = await scenario.AgentAsync("agent");
        var code = await scenario.CodeAsync(old.Name, false, null, scenario.T0);
        var fresh = (await db.AgentAsync("enroll", scenario.T0, ("p_code", code))).Text("token")!;

        await Scenario.FailsAsync("28000", () => scenario.SyncAsync(old, scenario.T0));
        await scenario.SyncAsync(old with { Token = fresh }, scenario.T0);
    }

    [Fact]
    public async Task DisabledAgent_CannotEnrollOrSync()
    {
        db.SkipIfUnavailable();
        var scenario = new Scenario(db, CloudDatabase.NextTime());
        var agent = await scenario.AgentAsync("agent");
        var code = await scenario.CodeAsync(agent.Name, false, null, scenario.T0);
        await db.AdminAsync("select to_jsonb(glebosaurus.admin_agent_upsert(@n, false, null, false))::text", null, ("n", agent.Name));

        await Scenario.FailsAsync("28000", () => scenario.SyncAsync(agent, scenario.T0));
        await Scenario.FailsAsync("28000", () => db.AgentAsync("enroll", scenario.T0, ("p_code", code)));
    }
}
