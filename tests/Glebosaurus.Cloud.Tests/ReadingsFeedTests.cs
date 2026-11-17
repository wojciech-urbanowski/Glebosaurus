using System.Text.Json;
using System.Text.Json.Nodes;

namespace Glebosaurus.Cloud.Tests;

/// <summary>
/// readings_feed: odczyty wszystkich agentów po numerze kolejnym – tylko „ustalone” (przyjęte co najmniej 30 s temu), w
/// kolejności numerów, paczkami; wiersz nieustalony o niższym numerze zatrzymuje wyższe (żaden nie przepada).
/// </summary>
public sealed class ReadingsFeedTests(CloudDatabase db)
{
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    private static JsonObject Readings(string sensor, params DateTimeOffset[] times) => new()
    {
        ["readings"] = new JsonArray([.. times.Select(at => (JsonNode)new JsonObject
        {
            ["sensor"] = sensor, ["at"] = at.ToString("O"), ["moisture"] = 40, ["tempTenths"] = 215, ["battery"] = 80, ["batteryState"] = 2
        })])
    };

    private async Task<long> LastSeqAsync() =>
        (await db.AdminAsync("select to_jsonb(coalesce(max(seq), 0))::text from glebosaurus.readings", null)).GetInt64();

    private Task<JsonElement> FeedAsync(TestAgent agent, DateTimeOffset at, long after, int limit = 5000) =>
        db.AgentAsync("readings_feed", at, ("p_token", agent.Token), ("p_after", after), ("p_limit", limit));

    private static DateTimeOffset[] Times(JsonElement feed) =>
        [.. feed.GetProperty("readings").EnumerateArray().Select(r => r.GetProperty("at").GetDateTimeOffset())];

    [Fact]
    public async Task Feed_GivesSettledReadingsOfAllAgents_InOrder_InPages()
    {
        db.SkipIfUnavailable();
        var s = new Scenario(db, CloudDatabase.NextTime());
        var sensor = await s.SensorAsync();
        var a = await s.AgentAsync("a");
        var b = await s.AgentAsync("b");
        var start = await LastSeqAsync();

        await s.SyncAsync(a, s.T0, Readings(sensor, s.At(-3 * Second), s.At(-2 * Second), s.At(-Second)));
        await s.SyncAsync(b, s.At(10 * Second), Readings(sensor, s.At(5 * Second), s.At(6 * Second)));

        // Nic jeszcze nie jest ustalone – „last” zostaje, żeby agent zapytał od tego samego miejsca.
        var early = await FeedAsync(b, s.At(20 * Second), start);
        Assert.Equal(0, early.GetProperty("readings").GetArrayLength());
        Assert.Equal(start, early.GetProperty("last").GetInt64());
        Assert.False(early.GetProperty("more").GetBoolean());

        // Po 30 s ustalone są tylko odczyty agenta a (agent b je dostaje – także cudze).
        var first = await FeedAsync(b, s.At(35 * Second), start);
        Assert.Equal([s.At(-3 * Second), s.At(-2 * Second), s.At(-Second)], Times(first));
        Assert.Equal(sensor, first.GetProperty("readings")[0].Text("sensor"));
        Assert.Equal(215, first.GetProperty("readings")[0].GetProperty("tempTenths").GetInt32());

        var second = await FeedAsync(a, s.At(60 * Second), first.GetProperty("last").GetInt64());
        Assert.Equal([s.At(5 * Second), s.At(6 * Second)], Times(second));
        Assert.Equal(await LastSeqAsync(), second.GetProperty("last").GetInt64());

        // Paczkami po 2: 2 + 2 + 1, „more” do ostatniej.
        var after = start;
        var pages = new List<int>();
        for (var more = true; more;)
        {
            var page = await FeedAsync(a, s.At(60 * Second), after, limit: 2);
            pages.Add(page.GetProperty("readings").GetArrayLength());
            after = page.GetProperty("last").GetInt64();
            more = page.GetProperty("more").GetBoolean();
        }
        Assert.Equal([2, 2, 1], pages);
    }

    [Fact]
    public async Task Feed_StopsAtUnsettledReadingWithLowerNumber()
    {
        db.SkipIfUnavailable();
        var s = new Scenario(db, CloudDatabase.NextTime());
        var sensor = await s.SensorAsync();
        var a = await s.AgentAsync("a");
        var start = await LastSeqAsync();

        // Niższy numer, później przyjęty (transakcja zaczęta później, zatwierdzona wcześniej) – i odwrotnie.
        await s.SyncAsync(a, s.At(50 * Second), Readings(sensor, s.At(40 * Second)));
        await s.SyncAsync(a, s.T0, Readings(sensor, s.At(-Second)));

        var blocked = await FeedAsync(a, s.At(40 * Second), start);
        Assert.Equal(0, blocked.GetProperty("readings").GetArrayLength());
        Assert.Equal(start, blocked.GetProperty("last").GetInt64());

        var settled = await FeedAsync(a, s.At(90 * Second), start);
        Assert.Equal([s.At(40 * Second), s.At(-Second)], Times(settled));
    }

    [Fact]
    public async Task Feed_RejectsBadArguments_AndUnknownToken()
    {
        db.SkipIfUnavailable();
        var s = new Scenario(db, CloudDatabase.NextTime());
        var a = await s.AgentAsync("a");

        await Scenario.FailsAsync("22023", () => FeedAsync(a, s.T0, -1));
        await Scenario.FailsAsync("22023", () => FeedAsync(a, s.T0, 0, limit: 0));
        await Scenario.FailsAsync("22023", () => FeedAsync(a, s.T0, 0, limit: 10_001));
        await Scenario.FailsAsync("28000", () => FeedAsync(a with { Token = "glb_nieznany" }, s.T0, 0));
    }
}
