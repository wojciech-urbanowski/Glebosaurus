using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;

namespace Glebosaurus.Cloud.Tests;

/// <summary>Strona właściciela (faza 3): funkcje owner_* tylko dla zalogowanego właściciela z TOTP (aal2).</summary>
public sealed class OwnerTests(CloudDatabase db)
{
    public static readonly string[] OwnerFunctions =
    [
        "owner_agent_code", "owner_agent_revoke", "owner_agent_save", "owner_alert_acknowledge", "owner_alert_close", "owner_alerts",
        "owner_alerts_mute", "owner_command", "owner_command_status", "owner_config",
        "owner_dashboard", "owner_info", "owner_mail_body", "owner_mail_history", "owner_readings", "owner_secret_set", "owner_sensor", "owner_sensor_add", "owner_sensor_archive",
        "owner_sensor_credentials", "owner_sensor_delete", "owner_sensor_update", "owner_series", "owner_settings_update"
    ];

    private async Task<Guid> OwnerAsync()
    {
        var user = Guid.NewGuid();
        await db.AdminAsync("insert into glebosaurus.owners (user_id, email) values (@u, @e) returning '{}'::text", null,
            ("u", user), ("e", $"{user:N}@example.com"));
        return user;
    }

    [Fact]
    public async Task Authenticated_ExecutesExactlyTheOwnerFunctionsInPublic()
    {
        db.SkipIfUnavailable();
        var names = await db.AdminAsync("""
            select coalesce(jsonb_agg(p.proname order by p.proname), '[]')::text
              from pg_proc p
             where p.pronamespace = 'public'::regnamespace
               and has_function_privilege('authenticated', p.oid, 'execute')
               and p.prorettype not in ('trigger'::regtype, 'event_trigger'::regtype)
            """, null);

        Assert.Equal(OwnerFunctions, names.EnumerateArray().Select(n => n.GetString()!).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Owner_WithTotp_GetsInfo()
    {
        db.SkipIfUnavailable();
        var user = await OwnerAsync();

        var info = await db.OwnerAsync("owner_info", CloudDatabase.Claims(user));

        Assert.Equal($"{user:N}@example.com", info.GetProperty("email").GetString());
        Assert.True(info.GetProperty("test").GetBoolean());
        Assert.Contains("20261121120000", info.GetProperty("migrations").EnumerateArray().Select(m => m.GetString()));
    }

    [Fact]
    public async Task WithoutTotp_IsRefused_EvenForOwner()
    {
        db.SkipIfUnavailable();
        var user = await OwnerAsync();

        var error = await Scenario.FailsAsync("42501", () => db.OwnerAsync("owner_info", CloudDatabase.Claims(user, "aal1")));
        Assert.Contains("mfa_required", error.MessageText);
        error = await Scenario.FailsAsync("42501", () => db.OwnerAsync("owner_dashboard", new JsonObject { ["sub"] = user.ToString() }));
        Assert.Contains("mfa_required", error.MessageText);
    }

    [Fact]
    public async Task UserOutsideOwners_IsRefused()
    {
        db.SkipIfUnavailable();
        await OwnerAsync();

        var error = await Scenario.FailsAsync("42501", () => db.OwnerAsync("owner_info", CloudDatabase.Claims(Guid.NewGuid())));
        Assert.Contains("not_owner", error.MessageText);
        var badSubject = CloudDatabase.Claims(Guid.NewGuid());
        badSubject["sub"] = "nie-uuid";
        error = await Scenario.FailsAsync("42501", () => db.OwnerAsync("owner_dashboard", badSubject));
        Assert.Contains("not_owner", error.MessageText);
    }

    [Fact]
    public async Task Anon_CannotCallOwnerFunctions_EvenWithOwnerClaims()
    {
        db.SkipIfUnavailable();
        await OwnerAsync();
        foreach (var function in new[] { "owner_info", "owner_dashboard", "owner_config" })
            await Scenario.FailsAsync("42501", () => db.AsRoleAsync("anon", $"select public.{function}()::text"));
        var anonCanRun = await db.AdminAsync("""
            select coalesce(jsonb_agg(p.proname), '[]')::text from pg_proc p
             where p.pronamespace = 'public'::regnamespace and p.proname like 'owner\_%'
               and (has_function_privilege('anon', p.oid, 'execute') or has_function_privilege('service_role', p.oid, 'execute'))
            """, null);
        Assert.Equal(0, anonCanRun.GetArrayLength());
    }

    [Fact]
    public async Task Dashboard_ShowsSensorsLastReadingsAgentsAndNotifier_WithoutSecrets()
    {
        db.SkipIfUnavailable();
        var s = new Scenario(db, CloudDatabase.NextTime());
        var user = await OwnerAsync();
        var sender = await s.AgentAsync("ownd", canSendMail: true, priority: 1);
        var sensor = await s.SensorAsync("ownd");
        var quiet = await s.SensorAsync("ownq");
        await s.ClaimAsync(sender, "sensor:" + sensor, s.T0);
        await s.ClaimAsync(sender, "notifier", s.T0);
        await s.SyncAsync(sender, s.T0, new JsonObject
        {
            ["readings"] = new JsonArray(
                new JsonObject { ["sensor"] = sensor, ["at"] = s.At(TimeSpan.FromHours(-2)).ToString("O"), ["moisture"] = 50, ["tempTenths"] = 200 },
                new JsonObject { ["sensor"] = sensor, ["at"] = s.At(TimeSpan.FromMinutes(-1)).ToString("O"), ["moisture"] = 31, ["tempTenths"] = 215,
                                 ["battery"] = 80, ["batteryState"] = 2 })
        });

        var dashboard = await db.OwnerAsync("owner_dashboard", CloudDatabase.Claims(user), s.T0);

        var sensors = dashboard.GetProperty("sensors").EnumerateArray().ToDictionary(x => x.GetProperty("id").GetString()!);
        var last = sensors[sensor].GetProperty("last");
        Assert.Equal(31, last.GetProperty("moisture").GetInt32());
        Assert.Equal(21.5, last.GetProperty("temperature").GetDouble());
        Assert.Equal(sender.Name, last.GetProperty("agent").GetString());
        Assert.Equal(sender.Name, sensors[sensor].GetProperty("holder").GetString());
        Assert.Equal(JsonValueKind.Null, sensors[quiet].GetProperty("last").ValueKind);
        Assert.Equal(sender.Name, dashboard.GetProperty("notifier").GetProperty("agent").GetString());
        var agent = dashboard.GetProperty("agents").EnumerateArray().Single(a => a.GetProperty("name").GetString() == sender.Name);
        Assert.True(agent.GetProperty("enrolled").GetBoolean());
        Assert.Equal(1, agent.GetProperty("senderPriority").GetInt32());

        var text = dashboard.GetRawText();
        Assert.DoesNotContain("localKey", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(sender.Token, text, StringComparison.Ordinal);
    }

    /// <summary><c>cloud owner</c>: konto musi istnieć w Supabase Auth. Konto zakładamy w transakcji wycofywanej na końcu
    /// (tylko gdy rola postgres może pisać w auth.users – inaczej sprawdzamy tylko odmowę dla nieznanego adresu).</summary>
    [Fact]
    public async Task AdminOwner_AddsOnlyExistingAuthAccount_ListsAndRemoves()
    {
        db.SkipIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await db.DataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        async Task<object?> Run(string sql, params (string Name, object Value)[] args)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value);
            return await command.ExecuteScalarAsync(ct);
        }

        await transaction.SaveAsync("missing", ct);
        var missing = await Assert.ThrowsAsync<PostgresException>(() => Run("select glebosaurus.admin_owner_add('nikt-taki@example.com')"));
        Assert.Equal("22023", missing.SqlState);
        await transaction.RollbackAsync("missing", ct);

        var user = Guid.NewGuid();
        var email = $"Wlasciciel-{user:N}@Example.com";
        try
        {
            await Run("""
                insert into auth.users (instance_id, id, aud, role, email, created_at, updated_at)
                values ('00000000-0000-0000-0000-000000000000', @id, 'authenticated', 'authenticated', @email, now(), now())
                """, ("id", user), ("email", email));
        }
        catch (PostgresException e) when (e.SqlState == "42501")
        {
            Assert.Skip("Rola postgres nie może pisać w auth.users – dodanie właściciela sprawdzone tylko dla nieznanego adresu.");
        }

        var added = JsonDocument.Parse((string)(await Run("select glebosaurus.admin_owner_add(@e)::text", ("e", email.ToUpperInvariant())))!).RootElement;
        Assert.Equal(user, added.GetProperty("userId").GetGuid());
        var list = JsonDocument.Parse((string)(await Run("select glebosaurus.admin_owner_list()::text"))!).RootElement;
        var row = list.EnumerateArray().Single(o => o.GetProperty("userId").GetGuid() == user);
        Assert.True(row.GetProperty("account").GetBoolean());
        Assert.False(row.GetProperty("totp").GetBoolean());
        Assert.Equal(true, await Run("select glebosaurus.admin_owner_remove(@e)", ("e", email)));
        Assert.Equal(false, await Run("select glebosaurus.admin_owner_remove(@e)", ("e", email)));
        await transaction.RollbackAsync(ct);
    }

    /// <summary>Prawdziwa droga przez Data API: sam klucz publikowalny (rola anon) nie otwiera funkcji strony.</summary>
    [Fact]
    public async Task DataApi_PublishableKeyAlone_CannotCallOwnerFunctions()
    {
        db.SkipIfUnavailable();
        Assert.SkipWhen(db.Settings.PublishableKey is null, "Brak klucza publikowalnego w pliku próbnego projektu.");
        using var http = new HttpClient { BaseAddress = new Uri(db.Settings.Url, "/rest/v1/"), Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Add("apikey", db.Settings.PublishableKey);

        for (var attempt = 0; ; attempt++)
        {
            var response = await http.PostAsJsonAsync("rpc/owner_info", new JsonObject(), TestContext.Current.CancellationToken);
            // Po migracji Data API odświeża opis schematu z opóźnieniem – nieznana funkcja (404) przez chwilę jest normalna.
            if (response.StatusCode == HttpStatusCode.NotFound && attempt < 10)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
                continue;
            }
            Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, $"Data API: {(int)response.StatusCode}");
            break;
        }
    }
}
