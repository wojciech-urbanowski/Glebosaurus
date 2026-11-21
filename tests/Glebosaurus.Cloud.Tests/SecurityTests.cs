using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;

namespace Glebosaurus.Cloud.Tests;

/// <summary>Co widzi agent (rola anon, klucz publikowalny): tylko funkcje API; tabele i schemat glebosaurus są zamknięte.</summary>
public sealed class SecurityTests(CloudDatabase db)
{
    private static readonly string[] ApiFunctions =
    [
        "agent_sync", "alert_list", "alert_notified", "alert_open", "alert_resolve", "command_create", "command_done", "command_progress",
        "enroll", "get_config", "lease_claim", "lease_release", "log_get", "log_put", "mail_abandon", "mail_archive_put", "mail_claim",
        "mail_credentials", "mail_sent", "pair_complete", "pair_key_get", "pair_key_put", "readings_feed", "readings_range"
    ];

    [Fact]
    public async Task Anon_ExecutesExactlyTheApiFunctionsInPublic()
    {
        db.SkipIfUnavailable();
        var names = await db.AdminAsync("""
            select coalesce(jsonb_agg(p.proname order by p.proname), '[]')::text
              from pg_proc p
             where p.pronamespace = 'public'::regnamespace
               and has_function_privilege('anon', p.oid, 'execute')
               and p.prorettype not in ('trigger'::regtype, 'event_trigger'::regtype)
            """, null);

        Assert.Equal(ApiFunctions, names.EnumerateArray().Select(n => n.GetString()!).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Anon_CannotTouchPrivateSchema()
    {
        db.SkipIfUnavailable();
        var noAccess = await db.AdminAsync("""
            select to_jsonb(
                   not has_schema_privilege('anon', 'glebosaurus', 'usage')
               and not exists (select 1 from pg_proc where pronamespace = 'glebosaurus'::regnamespace
                                and (has_function_privilege('anon', oid, 'execute')
                                  or has_function_privilege('authenticated', oid, 'execute')))
               and not exists (select 1 from pg_class where relnamespace = 'glebosaurus'::regnamespace and relkind = 'r'
                                and (has_table_privilege('anon', oid, 'select, insert, update, delete')
                                  or has_table_privilege('authenticated', oid, 'select, insert, update, delete'))))::text
            """, null);
        Assert.True(noAccess.GetBoolean());

        await Scenario.FailsAsync("42501", () => db.AsRoleAsync("anon", "select to_jsonb(count(*))::text from glebosaurus.agents"));
        await Scenario.FailsAsync("42501", () => db.AsRoleAsync("anon", "select to_jsonb(glebosaurus.clock())::text"));
    }

    [Fact]
    public async Task EveryTable_HasRowLevelSecurity()
    {
        db.SkipIfUnavailable();
        var withoutRls = await db.AdminAsync("""
            select coalesce(jsonb_agg(relname), '[]')::text from pg_class
             where relnamespace = 'glebosaurus'::regnamespace and relkind = 'r' and not relrowsecurity
            """, null);
        Assert.Equal(0, withoutRls.GetArrayLength());
    }

    [Fact]
    public async Task TestClock_WorksOnlyInMarkedTestDatabase()
    {
        db.SkipIfUnavailable();
        await using var connection = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        async Task<object?> Run(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        }

        await Run("select set_config('glebosaurus.test_now', '2000-01-01T00:00:00Z', true)");
        Assert.Equal(true, await Run("select glebosaurus.clock() = timestamptz '2000-01-01T00:00:00Z'"));
        await Run("delete from glebosaurus.environment");
        Assert.Equal(true, await Run("select glebosaurus.clock() = now()"));
        await transaction.RollbackAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Prawdziwa droga agenta: Data API z kluczem publikowalnym (bez sterowania zegarem).</summary>
    [Fact]
    public async Task DataApi_AgentCanEnrollAndSync_ButSeesNothingElse()
    {
        db.SkipIfUnavailable();
        Assert.SkipWhen(db.Settings.PublishableKey is null, "Brak klucza publikowalnego w pliku próbnego projektu.");
        var s = new Scenario(db, DateTimeOffset.UtcNow);
        var name = Scenario.Unique("http");
        var code = await s.CodeAsync(name, false, null, DateTimeOffset.UtcNow);
        using var http = new HttpClient { BaseAddress = new Uri(db.Settings.Url, "/rest/v1/"), Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Add("apikey", db.Settings.PublishableKey);

        var enroll = await RpcAsync(http, "enroll", new JsonObject { ["p_code"] = code });
        Assert.Equal(HttpStatusCode.OK, enroll.StatusCode);
        var token = (await enroll.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).Text("token")!;

        var sync = await RpcAsync(http, "agent_sync", new JsonObject { ["p_token"] = token, ["p_payload"] = new JsonObject { ["protocol"] = 1 } });
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        Assert.Equal(name, (await sync.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("agent").Text("name"));

        var badToken = await RpcAsync(http, "get_config", new JsonObject { ["p_token"] = "glb_nieznany" });
        Assert.Equal(HttpStatusCode.Forbidden, badToken.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await RpcAsync(http, "admin_agent_upsert",
            new JsonObject { ["p_name"] = "x", ["p_can_send_mail"] = true, ["p_sender_priority"] = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("agents", TestContext.Current.CancellationToken)).StatusCode);
        using var otherSchema = new HttpRequestMessage(HttpMethod.Get, "agents");
        otherSchema.Headers.Add("Accept-Profile", "glebosaurus");
        Assert.False((await http.SendAsync(otherSchema, TestContext.Current.CancellationToken)).IsSuccessStatusCode);
    }

    private static async Task<HttpResponseMessage> RpcAsync(HttpClient http, string function, JsonObject body)
    {
        // Po migracji Data API odświeża opis schematu z opóźnieniem – nieznana funkcja (PGRST202) przez chwilę jest normalna.
        for (var attempt = 0; ; attempt++)
        {
            var response = await http.PostAsJsonAsync("rpc/" + function, body, TestContext.Current.CancellationToken);
            if (response.StatusCode != HttpStatusCode.NotFound || attempt >= 10 || function.StartsWith("admin_", StringComparison.Ordinal))
                return response;
            await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        }
    }
}
