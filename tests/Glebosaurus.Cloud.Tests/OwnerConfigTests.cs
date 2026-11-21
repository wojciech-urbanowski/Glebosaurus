using System.Text.Json;
using System.Text.Json.Nodes;

namespace Glebosaurus.Cloud.Tests;

/// <summary>Edytor konfiguracji na stronie (faza 3c): zapis z wersją, czujniki z plikiem klucza, archiwum, sekrety tylko do
/// ustawienia, agenci i próbny mail przez nadawcę.</summary>
public sealed class OwnerConfigTests(CloudDatabase db)
{
    private static long Version(JsonElement config) => config.GetProperty("configVersion").GetInt64();

    private async Task<long> VersionAsync(JsonObject owner) => Version(await db.OwnerAsync("owner_config", owner));

    private Task<JsonElement> AddAsync(JsonObject owner, long version, string id, JsonObject credentials, JsonObject? options = null) =>
        db.OwnerAsync("owner_sensor_add", owner, null, ("p_version", version), ("p_id", id), ("p_name", "Czujnik " + id),
            ("p_credentials", credentials), ("p_options", options ?? new JsonObject()));

    [Fact]
    public async Task StaleVersion_IsAConflict_NotASilentOverwrite()
    {
        db.SkipIfUnavailable();
        var owner = await new Scenario(db, DateTimeOffset.UtcNow).OwnerAsync();
        var version = await VersionAsync(owner);

        var saved = await db.OwnerAsync("owner_settings_update", owner, null, ("p_version", version),
            ("p", new JsonObject { ["alertReminderHours"] = 24 }));
        Assert.True(Version(saved) > version);

        var error = await Scenario.FailsAsync("40001", () => db.OwnerAsync("owner_settings_update", owner, null, ("p_version", version),
            ("p", new JsonObject { ["alertReminderHours"] = 12 })));
        Assert.Contains("conflict", error.MessageText);
    }

    [Fact]
    public async Task Settings_NormalizeRecipientsAndReportTimes()
    {
        db.SkipIfUnavailable();
        var owner = await new Scenario(db, DateTimeOffset.UtcNow).OwnerAsync();
        await using var session = await OwnerSession.StartAsync(db, owner);

        await session.CallAsync("owner_settings_update", ("p_version", await session.VersionAsync()), ("p", new JsonObject
        {
            ["gmailAddress"] = " nadawca@gmail.com ",
            ["alertRecipients"] = new JsonArray(" Ola@Example.com ", "ola@example.com", "jan@example.com"),
            ["reportTimes"] = new JsonArray("19:00", "07:00", "19:00")
        }));

        var mail = (await session.CallAsync("owner_config")).GetProperty("mail");
        Assert.Equal("nadawca@gmail.com", mail.GetProperty("gmailAddress").GetString());
        Assert.Equal(["Ola@Example.com", "jan@example.com"], mail.GetProperty("alertRecipients").EnumerateArray().Select(a => a.GetString()));
        var times = (await session.CallAsync("owner_config")).GetProperty("settings").GetProperty("reportTimes");
        Assert.Equal(["07:00", "19:00"], times.EnumerateArray().Select(t => t.GetString()));

        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => session.CallAsync("owner_settings_update",
            ("p_version", 0L), ("p", new JsonObject { ["readIntervalMinutes"] = "15" })));
        Assert.Equal("40001", error.SqlState);   // najpierw wersja…
        error = await Assert.ThrowsAsync<Npgsql.PostgresException>(async () => await session.CallAsync("owner_settings_update",
            ("p_version", await session.VersionAsync()), ("p", new JsonObject { ["readIntervalMinutes"] = "15" })));
        Assert.Contains("podaj liczbę", error.MessageText);   // …potem typ: napis udający liczbę nie przechodzi
    }

    [Fact]
    public async Task Sensor_AddedFromKeyFile_GoesToAgents_WithKey_ButConfigShowsOnlyThatItIsSet()
    {
        db.SkipIfUnavailable();
        var s = new Scenario(db, CloudDatabase.NextTime());
        var owner = await s.OwnerAsync();
        var agent = await s.AgentAsync("cfg");
        var id = Scenario.Unique("nowy");
        var mac = Scenario.RandomMac();
        var file = Scenario.Credentials(mac.ToLowerInvariant().Replace(':', '-'));
        file["bindingAttempted"] = true;
        file["verified"] = true;

        var added = await AddAsync(owner, await VersionAsync(owner), id, file, new JsonObject { ["wateringBelowPercent"] = 30, ["wateringClearAbovePercent"] = 40 });
        Assert.Equal(mac, added.GetProperty("mac").GetString());

        var config = await db.OwnerAsync("owner_config", owner);
        var sensor = config.GetProperty("sensors").EnumerateArray().Single(x => x.GetProperty("id").GetString() == id);
        Assert.Equal(30, sensor.GetProperty("wateringBelowPercent").GetInt32());
        Assert.Equal(JsonValueKind.String, sensor.GetProperty("keySetAt").ValueKind);
        Assert.False(sensor.GetProperty("hasReadings").GetBoolean());
        Assert.DoesNotContain("a1b2c3d4e5f6", config.GetRawText(), StringComparison.OrdinalIgnoreCase);

        var forAgent = (await db.AgentAsync("get_config", s.T0, ("p_token", agent.Token))).GetProperty("sensors").EnumerateArray()
            .Single(x => x.GetProperty("id").GetString() == id);
        var tuya = forAgent.GetProperty("tuya");
        Assert.Equal(mac, tuya.GetProperty("mac").GetString());
        Assert.Equal("a1b2c3d4e5f6", tuya.GetProperty("localKeyHex").GetString());
        Assert.False(tuya.TryGetProperty("bindingAttempted", out _));   // do Vault trafiają tylko znane pola

        // Nowy klucz (np. po resecie) – MAC z pliku zastępuje dotychczasowy.
        var newMac = Scenario.RandomMac();
        await db.OwnerAsync("owner_sensor_credentials", owner, null, ("p_version", await VersionAsync(owner)), ("p_id", id),
            ("p_credentials", Scenario.Credentials(newMac)));
        sensor = (await db.OwnerAsync("owner_config", owner)).GetProperty("sensors").EnumerateArray().Single(x => x.GetProperty("id").GetString() == id);
        Assert.Equal(newMac, sensor.GetProperty("mac").GetString());
    }

    [Fact]
    public async Task Archive_HidesSensorFromAgentsAndDashboard_KeepsHistory_AndRestoreBringsItBackDisabled()
    {
        db.SkipIfUnavailable();
        var s = new Scenario(db, CloudDatabase.NextTime());
        var owner = await s.OwnerAsync();
        var sender = await s.AgentAsync("arch", canSendMail: true, priority: 1);
        var id = await s.SensorAsync("arch");
        await s.ClaimAsync(sender, "notifier", s.T0);
        var epoch = (await s.ClaimAsync(sender, "notifier", s.T0)).GetProperty("epoch").GetInt64();
        await s.SyncAsync(sender, s.T0, new JsonObject
        {
            ["readings"] = new JsonArray(new JsonObject { ["sensor"] = id, ["at"] = s.At(TimeSpan.FromMinutes(-5)).ToString("O"), ["moisture"] = 10 })
        });
        await db.AgentAsync("alert_open", s.T0, ("p_token", sender.Token), ("p_epoch", epoch), ("p_sensor", id), ("p_kind", "NeedsWater"),
            ("p_detail", "sucho"));

        await db.OwnerAsync("owner_sensor_archive", owner, s.T0, ("p_version", await VersionAsync(owner)), ("p_id", id), ("p_archive", true));

        var forAgent = await db.AgentAsync("get_config", s.T0, ("p_token", sender.Token));
        Assert.DoesNotContain(forAgent.GetProperty("sensors").EnumerateArray(), x => x.GetProperty("id").GetString() == id);
        var dashboard = await db.OwnerAsync("owner_dashboard", owner, s.T0);
        Assert.DoesNotContain(dashboard.GetProperty("sensors").EnumerateArray(), x => x.GetProperty("id").GetString() == id);
        Assert.False((await s.ClaimAsync(sender, "sensor:" + id, s.T0)).GetProperty("granted").GetBoolean());
        var alerts = await db.AgentAsync("alert_list", s.T0, ("p_token", sender.Token));
        Assert.DoesNotContain(alerts.EnumerateArray(), a => a.GetProperty("sensor").GetString() == id);
        var archived = (await db.OwnerAsync("owner_config", owner)).GetProperty("sensors").EnumerateArray().Single(x => x.GetProperty("id").GetString() == id);
        Assert.True(archived.GetProperty("hasReadings").GetBoolean());
        Assert.Equal(JsonValueKind.String, archived.GetProperty("archivedAt").ValueKind);

        // Z odczytami – tylko archiwum, nie usunięcie; włączyć można dopiero po przywróceniu.
        await Scenario.FailsAsync("22023", async () => await db.OwnerAsync("owner_sensor_delete", owner, null,
            ("p_version", await VersionAsync(owner)), ("p_id", id)));
        await Scenario.FailsAsync("22023", async () => await db.OwnerAsync("owner_sensor_update", owner, null,
            ("p_version", await VersionAsync(owner)), ("p_id", id), ("p_name", "X"), ("p_options", new JsonObject { ["enabled"] = true })));

        await db.OwnerAsync("owner_sensor_archive", owner, null, ("p_version", await VersionAsync(owner)), ("p_id", id), ("p_archive", false));
        var restored = (await db.OwnerAsync("owner_config", owner)).GetProperty("sensors").EnumerateArray().Single(x => x.GetProperty("id").GetString() == id);
        Assert.Equal(JsonValueKind.Null, restored.GetProperty("archivedAt").ValueKind);
        Assert.False(restored.GetProperty("enabled").GetBoolean());
        await db.OwnerAsync("owner_sensor_update", owner, null, ("p_version", await VersionAsync(owner)), ("p_id", id), ("p_name", "Wraca"),
            ("p_options", new JsonObject { ["enabled"] = true }));
        Assert.Contains((await db.AgentAsync("get_config", s.T0, ("p_token", sender.Token))).GetProperty("sensors").EnumerateArray(),
            x => x.GetProperty("id").GetString() == id && x.GetProperty("name").GetString() == "Wraca");
    }

    [Fact]
    public async Task Delete_OnlyWithoutReadings_RemovesKeyToo()
    {
        db.SkipIfUnavailable();
        var owner = await new Scenario(db, DateTimeOffset.UtcNow).OwnerAsync();
        var id = Scenario.Unique("pomylka");
        await AddAsync(owner, await VersionAsync(owner), id, Scenario.Credentials(Scenario.RandomMac()));

        await db.OwnerAsync("owner_sensor_delete", owner, null, ("p_version", await VersionAsync(owner)), ("p_id", id));

        Assert.DoesNotContain((await db.OwnerAsync("owner_config", owner)).GetProperty("sensors").EnumerateArray(),
            x => x.GetProperty("id").GetString() == id);
        var secret = await db.AdminAsync("select to_jsonb(count(*))::text from vault.secrets where name = @n", null, ("n", "glebosaurus:sensor:" + id));
        Assert.Equal(0, secret.GetInt32());
    }

    [Fact]
    public async Task Secrets_CanBeSetAndCleared_NeverRead()
    {
        db.SkipIfUnavailable();
        var owner = await new Scenario(db, DateTimeOffset.UtcNow).OwnerAsync();
        var before = await db.AdminAsync("select coalesce(to_jsonb(glebosaurus.secret_get('glebosaurus:gmail-app-password')), 'null')::text", null);

        try
        {
            var set = await db.OwnerAsync("owner_secret_set", owner, null, ("p_name", "gmail-app-password"), ("p_value", "abcd efgh ijkl mnop"));
            Assert.True(set.GetProperty("set").GetBoolean());
            Assert.Equal("abcdefghijklmnop", (await db.AdminAsync("select to_jsonb(glebosaurus.secret_get('glebosaurus:gmail-app-password'))::text", null)).GetString());
            var config = await db.OwnerAsync("owner_config", owner);
            Assert.Equal(JsonValueKind.String, config.GetProperty("mail").GetProperty("appPasswordSetAt").ValueKind);
            Assert.DoesNotContain("abcdefghijklmnop", config.GetRawText());

            await Scenario.FailsAsync("22023", () => db.OwnerAsync("owner_secret_set", owner, null, ("p_name", "healthchecks-ping-url"), ("p_value", "http://hc-ping.com/x")));
            await Scenario.FailsAsync("22023", () => db.OwnerAsync("owner_secret_set", owner, null, ("p_name", "sensor:x"), ("p_value", "x")));
        }
        finally
        {
            // Hasło innych testów wraca na miejsce.
            if (before.ValueKind == JsonValueKind.String)
                await db.AdminAsync("select glebosaurus.secret_put('glebosaurus:gmail-app-password', @v)::text", null, ("v", before.GetString()));
            else await db.OwnerAsync("owner_secret_set", owner, null, ("p_name", "gmail-app-password"), ("p_value", null));
        }
    }

    [Fact]
    public async Task Agents_CreateWithCode_EnrollChangeRoleAndRevoke()
    {
        db.SkipIfUnavailable();
        var s = new Scenario(db, CloudDatabase.NextTime());
        var owner = await s.OwnerAsync();
        var name = Scenario.Unique("web");

        await db.OwnerAsync("owner_agent_save", owner, null, ("p_name", name), ("p_can_send_mail", false), ("p_sender_priority", null), ("p_enabled", true));
        var code = (await db.OwnerAsync("owner_agent_code", owner, null, ("p_name", name))).GetProperty("code").GetString();
        var listed = (await db.OwnerAsync("owner_config", owner)).GetProperty("agents").EnumerateArray().Single(a => a.GetProperty("name").GetString() == name);
        Assert.Equal(JsonValueKind.String, listed.GetProperty("codeValidUntil").ValueKind);
        Assert.DoesNotContain(code!, (await db.OwnerAsync("owner_config", owner)).GetRawText());

        var token = (await db.AgentAsync("enroll", null, ("p_code", code))).GetProperty("token").GetString();
        await db.OwnerAsync("owner_agent_save", owner, null, ("p_name", name), ("p_can_send_mail", true), ("p_sender_priority", 3), ("p_enabled", true));
        var view = await db.AgentAsync("get_config", null, ("p_token", token));
        Assert.True(view.GetProperty("agent").GetProperty("canSendMail").GetBoolean());
        await Scenario.FailsAsync("22023", () => db.OwnerAsync("owner_agent_save", owner, null, ("p_name", name), ("p_can_send_mail", true),
            ("p_sender_priority", 10), ("p_enabled", true)));

        var revoked = await db.OwnerAsync("owner_agent_revoke", owner, null, ("p_name", name));
        Assert.True(revoked.GetProperty("hadToken").GetBoolean());
        await Scenario.FailsAsync("28000", () => db.AgentAsync("get_config", null, ("p_token", token)));
    }

    [Fact]
    public async Task TestMail_GoesToCurrentNotifier_AndItsResultIsVisible()
    {
        db.SkipIfUnavailable();
        var s = new Scenario(db, CloudDatabase.NextTime());
        var owner = await s.OwnerAsync();
        var sender = await s.AgentAsync("mt", canSendMail: true, priority: 1);
        var collector = await s.AgentAsync("mtc");
        var epoch = (await s.ClaimAsync(sender, "notifier", s.T0)).GetProperty("epoch").GetInt64();

        var created = await db.OwnerAsync("owner_command", owner, s.T0, ("p_kind", "mail_test"), ("p_sensor", null), ("p_agent", null));
        var id = created.GetProperty("id").GetInt64();

        Assert.DoesNotContain((await s.SyncAsync(collector, s.T0)).GetProperty("commands").EnumerateArray(), c => c.GetProperty("id").GetInt64() == id);
        var command = (await s.SyncAsync(sender, s.T0)).GetProperty("commands").EnumerateArray().Single(c => c.GetProperty("id").GetInt64() == id);
        Assert.Equal("mail_test", command.GetProperty("kind").GetString());

        Assert.True((await s.MailClaimAsync(sender, $"mail-test|{id}", epoch, s.T0)).GetProperty("claimed").GetBoolean());
        await db.AgentAsync("command_done", s.T0, ("p_token", sender.Token), ("p_id", id), ("p_result", new JsonObject { ["ok"] = true, ["recipients"] = 1 }));

        var status = (await db.OwnerAsync("owner_command_status", owner, s.T0, ("p_ids", new[] { id }))).EnumerateArray().Single();
        Assert.Equal(sender.Name, status.GetProperty("takenBy").GetString());
        Assert.True(status.GetProperty("result").GetProperty("ok").GetBoolean());
        Assert.False(status.GetProperty("expired").GetBoolean());

        await Scenario.FailsAsync("22023", () => db.OwnerAsync("owner_command", owner, s.T0, ("p_kind", "pair"), ("p_sensor", null), ("p_agent", sender.Name)));
    }
}
