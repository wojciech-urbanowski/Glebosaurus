namespace Glebosaurus.Cloud.Tests;

/// <summary>Dzierżawa czujnika: 30 min, przejęcie dopiero po wygaśnięciu i 2 min zapasu, epoka rośnie przy zmianie właściciela.</summary>
public sealed class SensorLeaseTests(CloudDatabase db)
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task OnlyOneAgentHoldsSensor()
    {
        db.SkipIfUnavailable();
        var s = new Scenario(db, CloudDatabase.NextTime());
        var sensor = "sensor:" + await s.SensorAsync();
        var a = await s.AgentAsync("a");
        var b = await s.AgentAsync("b");

        var first = await s.ClaimAsync(a, sensor, s.T0);
        Assert.True(first.Granted());
        Assert.Equal(1800, first.GetProperty("validSeconds").GetDouble());

        var second = await s.ClaimAsync(b, sensor, s.At(Minute));
        Assert.False(second.Granted());
        Assert.Equal(a.Name, second.Text("holder"));
        Assert.Equal(31 * 60, second.GetProperty("retryInSeconds").GetDouble());
    }

    [Fact]
    public async Task Renewal_KeepsEpochAndExtends()
    {
        db.SkipIfUnavailable();
        var s = new Scenario(db, CloudDatabase.NextTime());
        var sensor = "sensor:" + await s.SensorAsync();
        var a = await s.AgentAsync("a");

        var first = await s.ClaimAsync(a, sensor, s.T0);
        var renewed = await s.ClaimAsync(a, sensor, s.At(10 * Minute));

        Assert.True(renewed.Granted());
        Assert.Equal(first.Epoch(), renewed.Epoch());
        Assert.Equal(1800, renewed.GetProperty("validSeconds").GetDouble());
    }

    [Fact]
    public async Task SuddenlyGoneAgent_LosesSensorOnlyAfterExpiryAndGrace()
    {
        db.SkipIfUnavailable();
        var s = new Scenario(db, CloudDatabase.NextTime());
        var sensor = "sensor:" + await s.SensorAsync();
        var a = await s.AgentAsync("a");
        var b = await s.AgentAsync("b");
        var epoch = (await s.ClaimAsync(a, sensor, s.T0)).Epoch();

        Assert.False((await s.ClaimAsync(b, sensor, s.At(30 * Minute))).Granted());
        Assert.False((await s.ClaimAsync(b, sensor, s.At(31 * Minute + TimeSpan.FromSeconds(59)))).Granted());

        var taken = await s.ClaimAsync(b, sensor, s.At(32 * Minute));
        Assert.True(taken.Granted());
        Assert.Equal(epoch + 1, taken.Epoch());

        var back = await s.ClaimAsync(a, sensor, s.At(33 * Minute));
        Assert.False(back.Granted());
        Assert.Equal(b.Name, back.Text("holder"));
    }

    [Fact]
    public async Task ExpiredButUntaken_SameHolderRenewsWithSameEpoch()
    {
        db.SkipIfUnavailable();
        var s = new Scenario(db, CloudDatabase.NextTime());
        var sensor = "sensor:" + await s.SensorAsync();
        var a = await s.AgentAsync("a");
        var epoch = (await s.ClaimAsync(a, sensor, s.T0)).Epoch();

        var later = await s.ClaimAsync(a, sensor, s.At(45 * Minute));

        Assert.True(later.Granted());
        Assert.Equal(epoch, later.Epoch());
    }

    [Fact]
    public async Task Release_HandsOverImmediately_OnlyWithCurrentEpoch()
    {
        db.SkipIfUnavailable();
        var s = new Scenario(db, CloudDatabase.NextTime());
        var sensor = "sensor:" + await s.SensorAsync();
        var a = await s.AgentAsync("a");
        var b = await s.AgentAsync("b");
        var epoch = (await s.ClaimAsync(a, sensor, s.T0)).Epoch();

        Assert.False(await s.ReleaseAsync(a, sensor, epoch + 1, s.At(Minute)));
        Assert.False(await s.ReleaseAsync(b, sensor, epoch, s.At(Minute)));
        Assert.True(await s.ReleaseAsync(a, sensor, epoch, s.At(Minute)));

        var taken = await s.ClaimAsync(b, sensor, s.At(Minute + TimeSpan.FromSeconds(5)));
        Assert.True(taken.Granted());
        Assert.Equal(epoch + 1, taken.Epoch());
    }

    [Fact]
    public async Task UnknownSensorOrResource_IsRefused()
    {
        db.SkipIfUnavailable();
        var s = new Scenario(db, CloudDatabase.NextTime());
        var a = await s.AgentAsync("a");
        var disabled = await s.SensorAsync(options: new() { ["enabled"] = false });

        Assert.Equal("unknown_sensor", (await s.ClaimAsync(a, "sensor:" + Scenario.Unique("brak"), s.T0)).Text("error"));
        Assert.Equal("unknown_sensor", (await s.ClaimAsync(a, "sensor:" + disabled, s.T0)).Text("error"));
        Assert.Equal("invalid_resource", (await s.ClaimAsync(a, "cokolwiek", s.T0)).Text("error"));
    }

    [Fact]
    public async Task SimultaneousClaims_HaveExactlyOneWinner()
    {
        db.SkipIfUnavailable();
        var s = new Scenario(db, CloudDatabase.NextTime());
        var sensor = "sensor:" + await s.SensorAsync();
        var agents = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => s.AgentAsync("r")));

        var results = await Task.WhenAll(agents.Select(agent => s.ClaimAsync(agent, sensor, s.T0)));

        Assert.Single(results, r => r.Granted());
    }
}
