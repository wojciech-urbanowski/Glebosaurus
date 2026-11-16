namespace Glebosaurus.Cloud.Tests;

/// <summary>Wspólny stan alertów: zmienia go tylko obecny nadawca, a nowy nadawca widzi to, co zostawił poprzedni.</summary>
public sealed class AlertTests(CloudDatabase db)
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    private Task<System.Text.Json.JsonElement> OpenAsync(TestAgent agent, long epoch, string sensor, string kind, DateTimeOffset at) =>
        db.AgentAsync("alert_open", at, ("p_token", agent.Token), ("p_epoch", epoch), ("p_sensor", sensor), ("p_kind", kind),
            ("p_detail", "wilgotność 18% (próg 25%)"));

    [Fact]
    public async Task Open_IsIdempotentPerEpisode_AndResolveStartsNewOne()
    {
        db.SkipIfUnavailable();
        var s = new Scenario(db, CloudDatabase.NextTime());
        var sensor = await s.SensorAsync();
        var pi = await s.AgentAsync("pi", canSendMail: true, priority: 1);
        var epoch = (await s.ClaimAsync(pi, "notifier", s.T0)).Epoch();

        var first = await OpenAsync(pi, epoch, sensor, "NeedsWater", s.At(Minute));
        Assert.True(first.GetProperty("created").GetBoolean());
        var alert = first.GetProperty("alert");
        Assert.Equal(s.At(Minute), alert.GetProperty("openedAt").GetDateTimeOffset());

        var again = await OpenAsync(pi, epoch, sensor, "NeedsWater", s.At(2 * Minute));
        Assert.False(again.GetProperty("created").GetBoolean());
        Assert.Equal(alert.GetProperty("id").GetInt64(), again.GetProperty("alert").GetProperty("id").GetInt64());

        var other = await OpenAsync(pi, epoch, sensor, "LowBattery", s.At(2 * Minute));
        Assert.True(other.GetProperty("created").GetBoolean());

        Assert.True((await db.AgentAsync("alert_resolve", s.At(3 * Minute), ("p_token", pi.Token), ("p_epoch", epoch),
            ("p_id", alert.GetProperty("id").GetInt64()))).GetBoolean());
        var next = await OpenAsync(pi, epoch, sensor, "NeedsWater", s.At(4 * Minute));
        Assert.True(next.GetProperty("created").GetBoolean());
        Assert.NotEqual(alert.GetProperty("id").GetInt64(), next.GetProperty("alert").GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task OnlyCurrentNotifier_ChangesAlerts_ButEveryAgentReadsThem()
    {
        db.SkipIfUnavailable();
        var s = new Scenario(db, CloudDatabase.NextTime());
        var sensor = await s.SensorAsync();
        var pi = await s.AgentAsync("pi", canSendMail: true, priority: 1);
        var android = await s.AgentAsync("android");
        var epoch = (await s.ClaimAsync(pi, "notifier", s.T0)).Epoch();

        await Scenario.FailsAsync("42501", () => OpenAsync(android, epoch, sensor, "NeedsWater", s.At(Minute)));
        await Scenario.FailsAsync("42501", () => OpenAsync(pi, epoch + 1, sensor, "NeedsWater", s.At(Minute)));
        await Scenario.FailsAsync("42501", () => OpenAsync(pi, epoch, sensor, "NeedsWater", s.At(6 * Minute)));

        var id = (await OpenAsync(pi, epoch, sensor, "NeedsWater", s.At(Minute))).GetProperty("alert").GetProperty("id").GetInt64();
        var seenByAndroid = await db.AgentAsync("alert_list", s.At(Minute), ("p_token", android.Token));
        Assert.Contains(seenByAndroid.EnumerateArray(), a => a.GetProperty("id").GetInt64() == id);
        await Scenario.FailsAsync("42501", () => db.AgentAsync("alert_resolve", s.At(Minute), ("p_token", android.Token),
            ("p_epoch", epoch), ("p_id", id)));
    }

    [Fact]
    public async Task NewNotifier_ContinuesWhereOldOneStopped()
    {
        db.SkipIfUnavailable();
        var s = new Scenario(db, CloudDatabase.NextTime());
        var sensor = await s.SensorAsync();
        var bestia = await s.AgentAsync("bestia", canSendMail: true, priority: 2);
        var pi = await s.AgentAsync("pi", canSendMail: true, priority: 1);
        var oldEpoch = (await s.ClaimAsync(bestia, "notifier", s.T0)).Epoch();
        var id = (await OpenAsync(bestia, oldEpoch, sensor, "LowBattery", s.At(Minute))).GetProperty("alert").GetProperty("id").GetInt64();
        Assert.Equal(1, (await db.AgentAsync("alert_notified", s.At(Minute), ("p_token", bestia.Token), ("p_epoch", oldEpoch),
            ("p_ids", new[] { id }))).GetInt32());

        var newEpoch = (await s.ClaimAsync(pi, "notifier", s.At(10 * Minute))).Epoch();
        var reopened = await OpenAsync(pi, newEpoch, sensor, "LowBattery", s.At(11 * Minute));

        Assert.False(reopened.GetProperty("created").GetBoolean());
        Assert.Equal(id, reopened.GetProperty("alert").GetProperty("id").GetInt64());
        Assert.Equal(s.At(Minute), reopened.GetProperty("alert").GetProperty("lastNotifiedAt").GetDateTimeOffset());
        await Scenario.FailsAsync("42501", () => db.AgentAsync("alert_notified", s.At(11 * Minute), ("p_token", bestia.Token),
            ("p_epoch", oldEpoch), ("p_ids", new[] { id })));
    }

    [Fact]
    public async Task BadAlert_IsRejected()
    {
        db.SkipIfUnavailable();
        var s = new Scenario(db, CloudDatabase.NextTime());
        var sensor = await s.SensorAsync();
        var pi = await s.AgentAsync("pi", canSendMail: true, priority: 1);
        var epoch = (await s.ClaimAsync(pi, "notifier", s.T0)).Epoch();

        await Scenario.FailsAsync("22023", () => OpenAsync(pi, epoch, sensor, "Flood", s.T0));
        await Scenario.FailsAsync("22023", () => OpenAsync(pi, epoch, Scenario.Unique("brak"), "NeedsWater", s.T0));
        await Scenario.FailsAsync("22023", () => db.AgentAsync("alert_open", s.T0, ("p_token", pi.Token), ("p_epoch", epoch),
            ("p_sensor", sensor), ("p_kind", "NeedsWater"), ("p_detail", "")));
    }
}
