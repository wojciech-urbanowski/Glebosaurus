using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;

namespace Glebosaurus.Cloud.Tests;

/// <summary>Zarejestrowany agent testu: nazwa i token (tylko w pamięci testu).</summary>
public sealed record TestAgent(string Name, string Token);

/// <summary>Budowanie danych testu w próbnej bazie: unikalne nazwy, żeby testy nie wchodziły sobie w drogę.</summary>
public sealed class Scenario(CloudDatabase db, DateTimeOffset start)
{
    public DateTimeOffset T0 { get; } = start;

    public DateTimeOffset At(TimeSpan offset) => T0 + offset;

    public static string Unique(string prefix) => prefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));

    public static string RandomMac() =>
        string.Join(":", RandomNumberGenerator.GetBytes(6).Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));

    public async Task<TestAgent> AgentAsync(string prefix, bool canSendMail = false, int? priority = null, DateTimeOffset? at = null)
    {
        var name = Unique(prefix);
        var code = await CodeAsync(name, canSendMail, priority ?? (canSendMail ? 2 : null), at ?? T0);
        var enrolled = await db.AgentAsync("enroll", at ?? T0, ("p_code", code));
        return new(name, enrolled.GetProperty("token").GetString()!);
    }

    public async Task<string> CodeAsync(string name, bool canSendMail, int? priority, DateTimeOffset at, TimeSpan? validFor = null)
    {
        await db.AdminAsync("select to_jsonb(glebosaurus.admin_agent_upsert(@n, @m, @p))::text", at,
            ("n", name), ("m", canSendMail), ("p", priority));
        return (await db.AdminAsync("select to_jsonb(glebosaurus.admin_enrollment_code(@n, @v))::text", at,
            ("n", name), ("v", validFor ?? TimeSpan.FromHours(24)))).GetString()!;
    }

    public async Task<string> SensorAsync(string prefix = "s", JsonObject? options = null, bool withCredentials = true)
    {
        var id = Unique(prefix);
        var mac = RandomMac();
        await db.AdminAsync("select to_jsonb(null::text) from glebosaurus.admin_sensor_upsert(@id, @name, @mac, @o)", null,
            ("id", id), ("name", "Czujnik " + id), ("mac", mac), ("o", options ?? new JsonObject()));
        if (withCredentials)
            await db.AdminAsync("select to_jsonb(null::text) from glebosaurus.admin_sensor_credentials(@id, @c)", null,
                ("id", id), ("c", Credentials(mac)));
        return id;
    }

    /// <summary>Dane Tuya czujnika testowego – UUID losowy, bo agent pomija czujniki z powtórzonym UUID.</summary>
    public static JsonObject Credentials(string mac) => new()
    {
        ["mac"] = mac, ["uuid"] = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)), ["deviceId"] = "1234567890123456789012",
        ["localKeyHex"] = "a1b2c3d4e5f6", ["productId"] = "gvygg3m8"
    };

    public Task<JsonElement> ClaimAsync(TestAgent agent, string resource, DateTimeOffset at) =>
        db.AgentAsync("lease_claim", at, ("p_token", agent.Token), ("p_resource", resource));

    public async Task<bool> ReleaseAsync(TestAgent agent, string resource, long epoch, DateTimeOffset at) =>
        (await db.AgentAsync("lease_release", at, ("p_token", agent.Token), ("p_resource", resource), ("p_epoch", epoch))).GetBoolean();

    public Task<JsonElement> MailClaimAsync(TestAgent agent, string key, long epoch, DateTimeOffset at) =>
        db.AgentAsync("mail_claim", at, ("p_token", agent.Token), ("p_key", key), ("p_epoch", epoch));

    public Task<JsonElement> SyncAsync(TestAgent agent, DateTimeOffset at, JsonObject? payload = null)
    {
        payload ??= [];
        payload["protocol"] ??= 1;
        return db.AgentAsync("agent_sync", at, ("p_token", agent.Token), ("p_payload", payload));
    }

    /// <summary>Oczekuje błędu bazy z danym SQLSTATE (np. 28000 – nieznany token).</summary>
    public static async Task<PostgresException> FailsAsync(string sqlState, Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<PostgresException>(action);
        Assert.Equal(sqlState, error.SqlState);
        return error;
    }
}

internal static class JsonAssertions
{
    public static bool Granted(this JsonElement lease) => lease.GetProperty("granted").GetBoolean();
    public static long Epoch(this JsonElement lease) => lease.GetProperty("epoch").GetInt64();
    public static string? Text(this JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static JsonElement Single(this JsonElement array, string property, string value) =>
        Assert.Single(array.EnumerateArray(), e => e.Text(property) == value);
}
