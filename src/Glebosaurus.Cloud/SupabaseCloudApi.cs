using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Glebosaurus.Core;
using Glebosaurus.Core.Cloud;
using Glebosaurus.Core.Domain;

namespace Glebosaurus.Cloud;

/// <summary>
/// <see cref="ICloudApi"/> przez Data API Supabase: <c>POST /rest/v1/rpc/&lt;funkcja&gt;</c>, klucz publikowalny w nagłówku
/// <c>apikey</c> (rola <c>anon</c>), token agenta w treści. Błędy funkcji (SQLSTATE w polu <c>code</c>) zamienia na
/// <see cref="CloudErrorKind"/>; token i treść żądań nie trafiają do komunikatów wyjątków.
/// </summary>
public sealed class SupabaseCloudApi : ICloudApi
{
    private readonly HttpClient http;
    private readonly Uri rpcBase;
    private readonly string publishableKey;
    private readonly string token;

    public SupabaseCloudApi(HttpClient http, Uri projectUrl, string publishableKey, string agentToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentToken);
        this.http = http;
        rpcBase = RpcBase(projectUrl);
        this.publishableKey = publishableKey;
        token = agentToken;
    }

    public override string ToString() => $"SupabaseCloudApi {{ {rpcBase.Host} }}";

    /// <summary>Jednorazowy kod rejestracji → stały token agenta (jedyna funkcja bez tokenu).</summary>
    public static Task<EnrollResult> EnrollAsync(HttpClient http, Uri projectUrl, string publishableKey, string code,
        CancellationToken cancellationToken) =>
        CallAsync(http, RpcBase(projectUrl), publishableKey, "enroll", new JsonObject { ["p_code"] = code },
            CloudJsonContext.Default.EnrollResult, cancellationToken);

    public Task<CloudConfig> GetConfigAsync(CancellationToken cancellationToken) =>
        Call("get_config", [], CloudJsonContext.Default.CloudConfig, cancellationToken);

    public Task<SyncResponse> SyncAsync(SyncRequest request, CancellationToken cancellationToken) =>
        Call("agent_sync", new() { ["p_payload"] = JsonSerializer.SerializeToNode(request, CloudJsonContext.Default.SyncRequest) },
            CloudJsonContext.Default.SyncResponse, cancellationToken);

    public Task<LeaseResult> ClaimAsync(string resource, CancellationToken cancellationToken) =>
        Call("lease_claim", new() { ["p_resource"] = resource }, CloudJsonContext.Default.LeaseResult, cancellationToken);

    public Task<bool> ReleaseAsync(string resource, long epoch, CancellationToken cancellationToken) =>
        Call("lease_release", new() { ["p_resource"] = resource, ["p_epoch"] = epoch }, CloudJsonContext.Default.Boolean, cancellationToken);

    public Task<MailClaimResult> MailClaimAsync(string key, long epoch, CancellationToken cancellationToken) =>
        Call("mail_claim", new() { ["p_key"] = key, ["p_epoch"] = epoch }, CloudJsonContext.Default.MailClaimResult, cancellationToken);

    public Task<bool> MailSentAsync(string key, string? messageId, CancellationToken cancellationToken) =>
        Call("mail_sent", new() { ["p_key"] = key, ["p_message_id"] = messageId }, CloudJsonContext.Default.Boolean, cancellationToken);

    public Task<bool> MailAbandonAsync(string key, CancellationToken cancellationToken) =>
        Call("mail_abandon", new() { ["p_key"] = key }, CloudJsonContext.Default.Boolean, cancellationToken);

    public Task<MailCredentials> MailCredentialsAsync(long epoch, CancellationToken cancellationToken) =>
        Call("mail_credentials", new() { ["p_epoch"] = epoch }, CloudJsonContext.Default.MailCredentials, cancellationToken);

    public async Task<IReadOnlyList<StoredReading>> ReadingsRangeAsync(IReadOnlyCollection<string> sensors, DateTimeOffset from,
        DateTimeOffset to, CancellationToken cancellationToken)
    {
        var readings = await Call("readings_range", new()
        {
            ["p_sensors"] = new JsonArray([.. sensors.Select(s => (JsonNode)JsonValue.Create(s)!)]),
            ["p_from"] = Timestamp(from), ["p_to"] = Timestamp(to)
        }, CloudJsonContext.Default.ListReadingReport, cancellationToken);
        return [.. readings.Select(r => r.ToStored())];
    }

    public async Task<IReadOnlyList<CloudAlert>> AlertListAsync(CancellationToken cancellationToken) =>
        await Call("alert_list", [], CloudJsonContext.Default.ListCloudAlert, cancellationToken);

    public Task<AlertOpenResult> AlertOpenAsync(long epoch, string sensor, AlertKind kind, string detail, CancellationToken cancellationToken) =>
        Call("alert_open", new() { ["p_epoch"] = epoch, ["p_sensor"] = sensor, ["p_kind"] = kind.ToString(), ["p_detail"] = detail },
            CloudJsonContext.Default.AlertOpenResult, cancellationToken);

    public Task<bool> AlertResolveAsync(long epoch, long alertId, CancellationToken cancellationToken) =>
        Call("alert_resolve", new() { ["p_epoch"] = epoch, ["p_id"] = alertId }, CloudJsonContext.Default.Boolean, cancellationToken);

    public Task<int> AlertNotifiedAsync(long epoch, IReadOnlyCollection<long> alertIds, CancellationToken cancellationToken) =>
        Call("alert_notified", new()
        {
            ["p_epoch"] = epoch, ["p_ids"] = new JsonArray([.. alertIds.Select(id => (JsonNode)JsonValue.Create(id))])
        }, CloudJsonContext.Default.Int32, cancellationToken);

    public Task<CommandCreateResult> CommandCreateAsync(string kind, string? sensor, CancellationToken cancellationToken) =>
        Call("command_create", new() { ["p_kind"] = kind, ["p_sensor"] = sensor }, CloudJsonContext.Default.CommandCreateResult, cancellationToken);

    public Task<bool> CommandDoneAsync(long commandId, JsonObject? result, CancellationToken cancellationToken) =>
        Call("command_done", new() { ["p_id"] = commandId, ["p_result"] = result?.DeepClone() }, CloudJsonContext.Default.Boolean, cancellationToken);

    public Task<LogPutResult> LogPutAsync(DateOnly day, byte[] gzipContent, CancellationToken cancellationToken) =>
        Call("log_put", new() { ["p_day"] = Day(day), ["p_content_gzip_base64"] = Convert.ToBase64String(gzipContent) },
            CloudJsonContext.Default.LogPutResult, cancellationToken);

    public async Task<IReadOnlyList<AgentLog>> LogGetAsync(long epoch, DateOnly day, CancellationToken cancellationToken) =>
        await Call("log_get", new() { ["p_epoch"] = epoch, ["p_day"] = Day(day) }, CloudJsonContext.Default.ListAgentLog, cancellationToken);

    private Task<T> Call<T>(string function, JsonObject args, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        args["p_token"] = token;
        return CallAsync(http, rpcBase, publishableKey, function, args, typeInfo, cancellationToken);
    }

    private static async Task<T> CallAsync<T>(HttpClient http, Uri rpcBase, string publishableKey, string function, JsonObject args,
        JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(rpcBase, function))
        {
            Content = new StringContent(args.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("apikey", publishableKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Glebosaurus", AppInfo.Version));

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException e)
        {
            throw new CloudApiException(CloudErrorKind.Unavailable, $"{function}: brak połączenia z chmurą ({e.Message})", inner: e);
        }
        catch (TaskCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CloudApiException(CloudErrorKind.Unavailable, $"{function}: chmura nie odpowiedziała w czasie", inner: e);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode) throw Failure(function, response.StatusCode, body);
            try
            {
                return JsonSerializer.Deserialize(body, typeInfo)
                    ?? throw new CloudApiException(CloudErrorKind.Protocol, $"{function}: pusta odpowiedź chmury");
            }
            catch (JsonException e)
            {
                throw new CloudApiException(CloudErrorKind.Protocol, $"{function}: odpowiedź chmury niezgodna z protokołem ({e.Message})", inner: e);
            }
        }
    }

    /// <summary>Błąd Data API: <c>{"code": "&lt;SQLSTATE albo PGRST…&gt;", "message": …}</c>.</summary>
    private static CloudApiException Failure(string function, HttpStatusCode status, string body)
    {
        string? code = null, message = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (document.RootElement.TryGetProperty("code", out var c)) code = c.ValueKind == JsonValueKind.String ? c.GetString() : c.ToString();
                if (document.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) message = m.GetString();
            }
        }
        catch (JsonException) { }

        var kind = code switch
        {
            "28000" => CloudErrorKind.Unauthorized,
            "42501" => CloudErrorKind.NotNotifier,
            "PGRST202" => CloudErrorKind.Protocol,   // brak funkcji – baza bez migracji albo starsza niż agent
            _ when status is HttpStatusCode.Unauthorized => CloudErrorKind.Unauthorized,   // zły klucz publikowalny
            _ when (int)status >= 500 || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests => CloudErrorKind.Unavailable,
            _ => CloudErrorKind.Invalid
        };
        var text = message ?? (body.Length > 200 ? body[..200] : body);
        return new(kind, $"{function}: {(int)status} {text}", code);
    }

    private static Uri RpcBase(Uri projectUrl) => new(new Uri(projectUrl.GetLeftPart(UriPartial.Authority)), "/rest/v1/rpc/");

    private static string Timestamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
