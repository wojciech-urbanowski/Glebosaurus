using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Glebosaurus.Cloud.Tests;

/// <summary>Odmowa z Data API: kod HTTP i błąd PostgREST (<c>code</c> to SQLSTATE z funkcji albo PGRSTnnn samego Data API).</summary>
public sealed class DataApiException(HttpStatusCode status, string? code, string? text)
    : Exception($"Data API {(int)status} {code}: {text}")
{
    public HttpStatusCode Status { get; } = status;
    public string? Code { get; } = code;
    public string Text { get; } = text ?? "";
}

/// <summary>
/// Właściciel strony na PRAWDZIWYM Supabase Auth i Data API próbnego projektu – ta sama droga co <c>web/supabase.js</c>:
/// konto w Auth (zakłada je admin API kluczem tajnym, bo samodzielne zakładanie kont jest w projekcie wyłączone), logowanie
/// hasłem, składnik TOTP z kodem liczonym tutaj (RFC 6238) – stąd token <c>aal2</c> – i wpis właściciela przez
/// <c>glebosaurus.admin_owner_add</c> (jak <c>cloud owner</c>: wymaga potwierdzonego TOTP). Funkcje <c>owner_*</c> idą przez
/// <c>/rest/v1/rpc</c> z kluczem publikowalnym i tokenem użytkownika, więc działają w środowisku Data API (rola
/// <c>authenticator</c> z <c>pg_safeupdate</c>, limity czasu ról, dopasowanie argumentów po nazwach, kody HTTP), którego
/// połączenie bezpośrednie (<see cref="CloudDatabase.OwnerAsync"/>) nie odtwarza. Jedno konto na przebieg; hasło i tokeny
/// żyją tylko w pamięci, konto znika przy sprzątaniu (<see cref="DisposeAsync"/>), a pozostałości po przerwanym przebiegu –
/// przy następnym logowaniu.
/// </summary>
public sealed class DataApiOwner : IAsyncDisposable
{
    /// <summary>Adresy kont testu – tylko takie konta sprzątanie usuwa (właściciela strony próbnego projektu nie rusza).</summary>
    public const string EmailPrefix = "glebosaurus-data-api-";
    public const string EmailDomain = "@example.com";

    private static readonly TimeSpan SchemaWait = TimeSpan.FromSeconds(60);

    private readonly CloudDatabase db;
    private readonly HttpClient http;
    private string aal1Token = "";
    private string aal2Token = "";

    private DataApiOwner(CloudDatabase db, string email)
    {
        this.db = db;
        Email = email;
        http = new HttpClient { BaseAddress = db.Settings.Url, Timeout = TimeSpan.FromSeconds(30) };
    }

    public string Email { get; }
    public Guid UserId { get; private set; }

    /// <summary>Żądania tokenu tego konta do wywołań przez połączenie bezpośrednie (porównanie obu dróg).</summary>
    public JsonObject Claims => CloudDatabase.Claims(UserId);

    public static async Task<DataApiOwner> SignInAsync(CloudDatabase db)
    {
        var owner = new DataApiOwner(db, EmailPrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)) + EmailDomain);
        try
        {
            await owner.RemoveLeftoverAccountsAsync();
            await owner.CreateAccountAsync();
            await db.AdminAsync("select glebosaurus.admin_owner_add(@e)::text", null, ("e", owner.Email));
            await owner.WaitForSchemaAsync();
            return owner;
        }
        catch
        {
            await owner.DisposeAsync();
            throw;
        }
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Data API
    // ------------------------------------------------------------------------------------------------------------------

    /// <summary>Funkcja strony jako zalogowany właściciel z TOTP (aal2) – jak <c>Cloud.rpc</c> w <c>web/supabase.js</c>.</summary>
    public Task<JsonElement> RpcAsync(string function, params (string Name, object? Value)[] args) =>
        CallAsync(function, aal2Token, args);

    /// <summary>To samo konto zaraz po haśle, przed kodem TOTP (token aal1).</summary>
    public Task<JsonElement> RpcWithoutTotpAsync(string function, params (string Name, object? Value)[] args) =>
        CallAsync(function, aal1Token, args);

    /// <summary>Sam klucz publikowalny, bez tokenu użytkownika (rola anon – tak jak agent).</summary>
    public Task<JsonElement> RpcAnonymousAsync(string function, params (string Name, object? Value)[] args) =>
        CallAsync(function, null, args);

    /// <summary>Dowolny napis w nagłówku Authorization (np. zepsuty token).</summary>
    public Task<JsonElement> RpcWithTokenAsync(string token, string function, params (string Name, object? Value)[] args) =>
        CallAsync(function, token, args);

    private async Task<JsonElement> CallAsync(string function, string? token, (string Name, object? Value)[] args)
    {
        var body = new JsonObject();
        foreach (var (name, value) in args)
            body[name] = value is JsonNode node ? node.DeepClone() : JsonSerializer.SerializeToNode(value);

        using var request = new HttpRequestMessage(HttpMethod.Post, "rest/v1/rpc/" + function)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("apikey", db.Settings.PublishableKey);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            string? code = null, message = text;
            try
            {
                using var error = JsonDocument.Parse(text);
                code = error.RootElement.Text("code");
                message = error.RootElement.Text("message") ?? text;
            }
            catch (JsonException)
            {
                // Treść spoza PostgREST (np. bramka) – zostaje jako napis.
            }
            throw new DataApiException(response.StatusCode, code, message);
        }
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>Po migracjach od zera Data API odświeża opis schematu z opóźnieniem – do tego czasu funkcji „nie ma” (PGRST202).</summary>
    private async Task WaitForSchemaAsync()
    {
        var deadline = DateTime.UtcNow + SchemaWait;
        while (true)
        {
            try
            {
                await RpcAsync("owner_info");
                return;
            }
            catch (DataApiException e) when (e.Code == "PGRST202" && DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Supabase Auth
    // ------------------------------------------------------------------------------------------------------------------

    private async Task CreateAccountAsync()
    {
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        var user = await AdminAsync(HttpMethod.Post, "auth/v1/admin/users",
            new JsonObject { ["email"] = Email, ["password"] = password, ["email_confirm"] = true });
        UserId = user.GetProperty("id").GetGuid();

        var session = await AuthAsync(HttpMethod.Post, "auth/v1/token?grant_type=password", null,
            new JsonObject { ["email"] = Email, ["password"] = password });
        aal1Token = session.GetProperty("access_token").GetString()!;

        var factor = await AuthAsync(HttpMethod.Post, "auth/v1/factors", aal1Token,
            new JsonObject { ["factor_type"] = "totp", ["friendly_name"] = "Glebosaurus test", ["issuer"] = "Glebosaurus" });
        var factorId = factor.GetProperty("id").GetString()!;
        var secret = factor.GetProperty("totp").GetProperty("secret").GetString()!;

        // Kod tuż przed końcem 30-sekundowego okna mógłby dotrzeć do Auth już nieważny.
        var now = DateTimeOffset.UtcNow;
        if (now.ToUnixTimeSeconds() % 30 > 25) await Task.Delay(TimeSpan.FromSeconds(30 - now.ToUnixTimeSeconds() % 30));
        var challenge = await AuthAsync(HttpMethod.Post, $"auth/v1/factors/{factorId}/challenge", aal1Token, new JsonObject());
        var verified = await AuthAsync(HttpMethod.Post, $"auth/v1/factors/{factorId}/verify", aal1Token, new JsonObject
        {
            ["challenge_id"] = challenge.GetProperty("id").GetString(), ["code"] = Totp.Code(secret, DateTimeOffset.UtcNow)
        });
        aal2Token = verified.GetProperty("access_token").GetString()!;
    }

    /// <summary>Konta testu z przerwanych przebiegów (tylko z adresem <see cref="EmailPrefix"/>…<see cref="EmailDomain"/>).</summary>
    private async Task RemoveLeftoverAccountsAsync()
    {
        var users = await AdminAsync(HttpMethod.Get, "auth/v1/admin/users?page=1&per_page=1000", null);
        foreach (var user in users.GetProperty("users").EnumerateArray())
        {
            var email = user.Text("email") ?? "";
            if (email.StartsWith(EmailPrefix, StringComparison.Ordinal) && email.EndsWith(EmailDomain, StringComparison.Ordinal))
                await AdminAsync(HttpMethod.Delete, "auth/v1/admin/users/" + user.GetProperty("id").GetString(), null);
        }
    }

    /// <summary>Auth admin API – klucz tajny tylko w nagłówku apikey (bramka Supabase zamienia go na rolę service_role).</summary>
    private Task<JsonElement> AdminAsync(HttpMethod method, string path, JsonObject? body) =>
        SendAuthAsync(method, path, db.Settings.SecretKey!, null, body);

    private Task<JsonElement> AuthAsync(HttpMethod method, string path, string? token, JsonObject? body) =>
        SendAuthAsync(method, path, db.Settings.PublishableKey!, token, body);

    private async Task<JsonElement> SendAuthAsync(HttpMethod method, string path, string apiKey, string? token, JsonObject? body)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("apikey", apiKey);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            // Bez treści odpowiedzi w komunikacie, gdyby niosła coś z żądania (hasło, kod) – wystarczy kod błędu Auth.
            string? code = null;
            try
            {
                using var error = JsonDocument.Parse(text);
                code = error.RootElement.Text("error_code") ?? error.RootElement.Text("code");
            }
            catch (JsonException)
            {
                // Odpowiedź bez JSON-a – sam kod HTTP.
            }
            throw new InvalidOperationException($"Supabase Auth {method} {path.Split('?')[0]}: {(int)response.StatusCode} {code}");
        }
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (UserId != Guid.Empty)
            {
                await db.AdminAsync("select to_jsonb(glebosaurus.admin_owner_remove(@e))::text", null, ("e", Email));
                await AdminAsync(HttpMethod.Delete, "auth/v1/admin/users/" + UserId, null);
            }
        }
        finally
        {
            http.Dispose();
        }
    }
}

/// <summary>Kod TOTP (RFC 6238: HMAC-SHA1, 30 s, 6 cyfr) z sekretu Base32 – jak aplikacja uwierzytelniająca właściciela.</summary>
public static class Totp
{
    public static string Code(string base32Secret, DateTimeOffset at)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, at.ToUnixTimeSeconds() / 30);
        var hash = HMACSHA1.HashData(Base32(base32Secret), counter);
        var offset = hash[^1] & 0x0F;
        var value = (BinaryPrimitives.ReadInt32BigEndian(hash.AsSpan(offset, 4)) & 0x7FFFFFFF) % 1_000_000;
        return value.ToString("D6", CultureInfo.InvariantCulture);
    }

    private static byte[] Base32(string text)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in text.TrimEnd('=').ToUpperInvariant())
        {
            var index = alphabet.IndexOf(c, StringComparison.Ordinal);
            if (index < 0) throw new FormatException("Sekret TOTP nie jest w Base32.");
            buffer = (buffer << 5) | index;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)(buffer >> bits));
                buffer &= (1 << bits) - 1;
            }
        }
        return [.. bytes];
    }
}
