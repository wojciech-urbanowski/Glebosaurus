using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Glebosaurus.Cloud.Tests;

/// <summary>Alerty i historia maili na stronie (faza 3b): potwierdzenie, ręczne zamknięcie, wyciszenia w konfiguracji agentów,
/// archiwum maili tylko od obecnego nadawcy.</summary>
public sealed class OwnerAlertsTests(CloudDatabase db)
{
    private static async Task<(Scenario S, JsonObject Owner, TestAgent Sender, long Epoch, string Sensor)> SetupAsync(CloudDatabase db, string prefix)
    {
        var s = new Scenario(db, CloudDatabase.NextTime());
        var owner = await s.OwnerAsync();
        var sender = await s.AgentAsync(prefix, canSendMail: true, priority: 1);
        var sensor = await s.SensorAsync(prefix);
        var epoch = (await s.ClaimAsync(sender, "notifier", s.T0)).GetProperty("epoch").GetInt64();
        return (s, owner, sender, epoch, sensor);
    }

    private Task<JsonElement> OpenAsync(TestAgent sender, long epoch, string sensor, DateTimeOffset at) =>
        db.AgentAsync("alert_open", at, ("p_token", sender.Token), ("p_epoch", epoch), ("p_sensor", sensor), ("p_kind", "NeedsWater"),
            ("p_detail", "sucho"));

    [Fact]
    public async Task Acknowledge_ReachesAgents_AndClose_LetsANewEpisodeOpen()
    {
        db.SkipIfUnavailable();
        var (s, owner, sender, epoch, sensor) = await SetupAsync(db, "ack");
        var alert = (await OpenAsync(sender, epoch, sensor, s.T0)).GetProperty("alert");
        var id = alert.GetProperty("id").GetInt64();

        await db.OwnerAsync("owner_alert_acknowledge", owner, s.T0, ("p_id", id), ("p_acknowledge", true));
        var listed = (await db.AgentAsync("alert_list", s.T0, ("p_token", sender.Token))).EnumerateArray().Single(a => a.GetProperty("id").GetInt64() == id);
        Assert.Equal(JsonValueKind.String, listed.GetProperty("acknowledgedAt").ValueKind);

        var closed = await db.OwnerAsync("owner_alert_close", owner, s.At(TimeSpan.FromMinutes(1)), ("p_id", id));
        Assert.True(closed.GetProperty("closedManually").GetBoolean());
        await Scenario.FailsAsync("22023", () => db.OwnerAsync("owner_alert_close", owner, s.T0, ("p_id", id)));

        // Stan trwa – nadawca otwiera nowy epizod (nowy początek = nowy klucz maila).
        var again = await OpenAsync(sender, epoch, sensor, s.At(TimeSpan.FromMinutes(2)));
        Assert.True(again.GetProperty("created").GetBoolean());
        Assert.NotEqual(id, again.GetProperty("alert").GetProperty("id").GetInt64());

        var page = await db.OwnerAsync("owner_alerts", owner, s.At(TimeSpan.FromMinutes(3)), ("p_days", 30));
        Assert.Contains(page.GetProperty("closed").EnumerateArray(), a => a.GetProperty("id").GetInt64() == id);
        Assert.Contains(page.GetProperty("open").EnumerateArray(), a => a.GetProperty("sensor").GetString() == sensor);
    }

    [Fact]
    public async Task Mutes_GoToAgentsInGetConfig_AndExpire()
    {
        db.SkipIfUnavailable();
        var (s, owner, sender, _, sensor) = await SetupAsync(db, "mute");
        var until = s.T0.AddDays(2);

        await db.OwnerAsync("owner_alerts_mute", owner, s.T0, ("p_sensor", sensor), ("p_until", until));
        var config = await db.AgentAsync("get_config", s.T0, ("p_token", sender.Token));
        var forAgent = config.GetProperty("sensors").EnumerateArray().Single(x => x.GetProperty("id").GetString() == sensor);
        Assert.Equal(until, forAgent.GetProperty("alertsMutedUntil").GetDateTimeOffset());

        try
        {
            await db.OwnerAsync("owner_alerts_mute", owner, s.T0, ("p_sensor", null), ("p_until", s.T0.AddDays(5)));
            config = await db.AgentAsync("get_config", s.T0, ("p_token", sender.Token));
            Assert.Equal(s.T0.AddDays(5), config.GetProperty("settings").GetProperty("alertsMutedUntil").GetDateTimeOffset());
            Assert.Equal(JsonValueKind.String, (await db.OwnerAsync("owner_dashboard", owner, s.T0)).GetProperty("alertsMutedUntil").ValueKind);
            // Po terminie strona nie pokazuje wyciszenia.
            Assert.Equal(JsonValueKind.Null, (await db.OwnerAsync("owner_alerts", owner, s.T0.AddDays(6), ("p_days", 1))).GetProperty("alertsMutedUntil").ValueKind);

            await Scenario.FailsAsync("22023", () => db.OwnerAsync("owner_alerts_mute", owner, s.T0, ("p_sensor", sensor), ("p_until", s.T0.AddDays(91))));
            await Scenario.FailsAsync("22023", () => db.OwnerAsync("owner_alerts_mute", owner, s.T0, ("p_sensor", sensor), ("p_until", s.T0.AddMinutes(-1))));
        }
        finally
        {
            await db.OwnerAsync("owner_alerts_mute", owner, s.T0, ("p_sensor", null), ("p_until", null));   // urlop nie przeszkadza innym testom
        }
    }

    private static string Gzip(string html)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true)) gzip.Write(Encoding.UTF8.GetBytes(html));
        return Convert.ToBase64String(buffer.ToArray());
    }

    [Fact]
    public async Task MailArchive_OnlyFromNotifier_ForItsOwnClaimedMail_ThenVisibleToOwner()
    {
        db.SkipIfUnavailable();
        var (s, owner, sender, epoch, _) = await SetupAsync(db, "arch");
        var collector = await s.AgentAsync("archc");
        var key = $"report-request|{Scenario.Unique("r")}";
        await s.MailClaimAsync(sender, key, epoch, s.T0);
        const string html = "<html><body><p style=\"color:red\">Raport</p></body></html>";

        (string, object?)[] Args(TestAgent agent, string k, string body) =>
            [("p_token", agent.Token), ("p_epoch", epoch), ("p_keys", new[] { k }), ("p_subject", "Poranny raport"),
             ("p_recipients", new[] { "ktos@example.com" }), ("p_html_gzip", body)];

        await Scenario.FailsAsync("42501", () => db.AgentAsync("mail_archive_put", s.T0, Args(collector, key, Gzip(html))));
        await Scenario.FailsAsync("22023", () => db.AgentAsync("mail_archive_put", s.T0, Args(sender, "report-request|cudzy", Gzip(html))));
        await Scenario.FailsAsync("22023", () => db.AgentAsync("mail_archive_put", s.T0, Args(sender, key, Convert.ToBase64String(Encoding.UTF8.GetBytes(html)))));
        await Scenario.FailsAsync("22023", () => db.AgentAsync("mail_archive_put", s.T0, Args(sender, $"log|{key}", Gzip(html))));

        Assert.True((await db.AgentAsync("mail_archive_put", s.T0, Args(sender, key, Gzip(html)))).GetBoolean());
        Assert.False((await db.AgentAsync("mail_archive_put", s.T0, Args(sender, key, Gzip(html)))).GetBoolean());

        var history = (await db.OwnerAsync("owner_mail_history", owner, s.T0, ("p_days", 30))).EnumerateArray()
            .Single(m => m.GetProperty("key").GetString() == key);
        Assert.Equal("report-request", history.GetProperty("kind").GetString());
        Assert.Equal(sender.Name, history.GetProperty("agent").GetString());
        var body = await db.OwnerAsync("owner_mail_body", owner, s.T0, ("p_key", key));
        using var reader = new StreamReader(new GZipStream(new MemoryStream(Convert.FromBase64String(body.GetProperty("htmlGzip").GetString()!)),
            CompressionMode.Decompress), Encoding.UTF8);
        Assert.Equal(html, reader.ReadToEnd());
    }
}
