using System.IO.Compression;
using System.Text;

namespace Glebosaurus.Cloud.Tests;

/// <summary>Dzienne logi agentów: gzip przez funkcję API, odczyt tylko dla obecnego nadawcy, retencja 7 dni.</summary>
public sealed class LogTests(CloudDatabase db)
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    private static string Gzip(string text)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.SmallestSize)) gzip.Write(Encoding.UTF8.GetBytes(text));
        return Convert.ToBase64String(buffer.ToArray());
    }

    private static string Gunzip(string base64)
    {
        using var gzip = new GZipStream(new MemoryStream(Convert.FromBase64String(base64)), CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private Task<System.Text.Json.JsonElement> PutAsync(TestAgent agent, DateOnly day, string content, DateTimeOffset at) =>
        db.AgentAsync("log_put", at, ("p_token", agent.Token), ("p_day", day), ("p_content_gzip_base64", content));

    private static DateOnly Day(DateTimeOffset at) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw")).DateTime);

    [Fact]
    public async Task NotifierGetsLogsOfAllAgents_ForTheDay()
    {
        db.SkipIfUnavailable();
        var s = new Scenario(db, CloudDatabase.NextTime());
        var android = await s.AgentAsync("android");
        var pi = await s.AgentAsync("pi", canSendMail: true, priority: 1);
        var yesterday = Day(s.T0).AddDays(-1);
        var bigLog = string.Concat(Enumerable.Repeat("12:00:00 info: odczyt Cisy – wilgotność 41%\n", 20000));

        await PutAsync(android, yesterday, Gzip("pierwsza wersja"), s.T0);
        var stored = await PutAsync(android, yesterday, Gzip(bigLog), s.At(Minute));
        Assert.True(stored.GetProperty("sizeBytes").GetInt32() < bigLog.Length / 10);
        await PutAsync(pi, yesterday, Gzip("log nadawcy"), s.At(Minute));
        var epoch = (await s.ClaimAsync(pi, "notifier", s.At(2 * Minute))).Epoch();

        var logs = await db.AgentAsync("log_get", s.At(2 * Minute), ("p_token", pi.Token), ("p_epoch", epoch), ("p_day", yesterday));

        Assert.Equal(bigLog, Gunzip(logs.Single("agent", android.Name).Text("contentGzipBase64")!));
        Assert.Equal("log nadawcy", Gunzip(logs.Single("agent", pi.Name).Text("contentGzipBase64")!));
        await Scenario.FailsAsync("42501", () => db.AgentAsync("log_get", s.At(2 * Minute), ("p_token", android.Token),
            ("p_epoch", epoch), ("p_day", yesterday)));
    }

    [Fact]
    public async Task OnlyGzip_RecentDays_AndLimitedSize()
    {
        db.SkipIfUnavailable();
        var s = new Scenario(db, CloudDatabase.NextTime());
        var agent = await s.AgentAsync("android");
        var today = Day(s.T0);

        await Scenario.FailsAsync("22023", () => PutAsync(agent, today, Convert.ToBase64String(Encoding.UTF8.GetBytes("zwykły tekst, nie gzip")), s.T0));
        await Scenario.FailsAsync("22023", () => PutAsync(agent, today, "to nie jest base64!", s.T0));
        await Scenario.FailsAsync("22023", () => PutAsync(agent, today.AddDays(2), Gzip("jutro"), s.T0));
        await Scenario.FailsAsync("22023", () => PutAsync(agent, today.AddDays(-31), Gzip("dawno"), s.T0));
        await Scenario.FailsAsync("22023", () => PutAsync(agent, today, new string('A', 5_600_004), s.T0));
    }

    [Fact]
    public async Task LogsOlderThanRetention_AreRemoved()
    {
        db.SkipIfUnavailable();
        var s = new Scenario(db, CloudDatabase.NextTime());
        var agent = await s.AgentAsync("android");
        var today = Day(s.T0);
        await PutAsync(agent, today.AddDays(-7), Gzip("tydzień temu"), s.T0);
        await PutAsync(agent, today.AddDays(-8), Gzip("osiem dni temu"), s.T0);

        await PutAsync(agent, today, Gzip("dziś"), s.At(Minute));

        var days = await db.AdminAsync("""
            select coalesce(jsonb_agg(l.day order by l.day), '[]')::text from glebosaurus.agent_logs l
              join glebosaurus.agents a on a.id = l.agent_id where a.name = @n
            """, null, ("n", agent.Name));
        Assert.Equal(new[] { today.AddDays(-7), today }, days.EnumerateArray().Select(d => DateOnly.Parse(d.GetString()!)).ToArray());
    }
}
