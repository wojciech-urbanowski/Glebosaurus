using System.Text.Json.Nodes;
using Glebosaurus.Core.Cloud;
using Glebosaurus.Core.Domain;

namespace Glebosaurus.Cloud.Tests;

/// <summary>Prawdziwy klient, któremu na żądanie „nie ma sieci” (przy synchronizacji albo pobieraniu konfiguracji).</summary>
internal sealed class FlakyApi(ICloudApi inner) : ICloudApi
{
    public ICloudApi Inner { get; } = inner;
    public int SyncFailuresLeft { get; set; }
    public int ConfigFailuresLeft { get; set; }

    private static CloudApiException Offline(string function) => new(CloudErrorKind.Unavailable, $"{function}: brak sieci (próba)");

    public Task<SyncResponse> SyncAsync(SyncRequest request, CancellationToken cancellationToken) =>
        SyncFailuresLeft-- > 0 ? throw Offline("agent_sync") : Inner.SyncAsync(request, cancellationToken);

    public Task<CloudConfig> GetConfigAsync(CancellationToken cancellationToken) =>
        ConfigFailuresLeft-- > 0 ? throw Offline("get_config") : Inner.GetConfigAsync(cancellationToken);

    public Task<LeaseResult> ClaimAsync(string resource, CancellationToken cancellationToken) => Inner.ClaimAsync(resource, cancellationToken);
    public Task<bool> ReleaseAsync(string resource, long epoch, CancellationToken cancellationToken) => Inner.ReleaseAsync(resource, epoch, cancellationToken);
    public Task<MailClaimResult> MailClaimAsync(string key, long epoch, CancellationToken cancellationToken) => Inner.MailClaimAsync(key, epoch, cancellationToken);
    public Task<bool> MailSentAsync(string key, string? messageId, CancellationToken cancellationToken) => Inner.MailSentAsync(key, messageId, cancellationToken);
    public Task<bool> MailAbandonAsync(string key, CancellationToken cancellationToken) => Inner.MailAbandonAsync(key, cancellationToken);
    public Task<MailCredentials> MailCredentialsAsync(long epoch, CancellationToken cancellationToken) => Inner.MailCredentialsAsync(epoch, cancellationToken);
    public Task<IReadOnlyList<StoredReading>> ReadingsRangeAsync(IReadOnlyCollection<string> sensors, DateTimeOffset from, DateTimeOffset to,
        CancellationToken cancellationToken) => Inner.ReadingsRangeAsync(sensors, from, to, cancellationToken);
    public Task<IReadOnlyList<CloudAlert>> AlertListAsync(CancellationToken cancellationToken) => Inner.AlertListAsync(cancellationToken);
    public Task<AlertOpenResult> AlertOpenAsync(long epoch, string sensor, AlertKind kind, string detail, CancellationToken cancellationToken) =>
        Inner.AlertOpenAsync(epoch, sensor, kind, detail, cancellationToken);
    public Task<bool> AlertResolveAsync(long epoch, long alertId, CancellationToken cancellationToken) => Inner.AlertResolveAsync(epoch, alertId, cancellationToken);
    public Task<int> AlertNotifiedAsync(long epoch, IReadOnlyCollection<long> alertIds, CancellationToken cancellationToken) =>
        Inner.AlertNotifiedAsync(epoch, alertIds, cancellationToken);
    public Task<CommandCreateResult> CommandCreateAsync(string kind, string? sensor, CancellationToken cancellationToken) =>
        Inner.CommandCreateAsync(kind, sensor, cancellationToken);
    public Task<bool> CommandDoneAsync(long commandId, JsonObject? result, CancellationToken cancellationToken) =>
        Inner.CommandDoneAsync(commandId, result, cancellationToken);
    public Task<LogPutResult> LogPutAsync(DateOnly day, byte[] gzipContent, CancellationToken cancellationToken) =>
        Inner.LogPutAsync(day, gzipContent, cancellationToken);
    public Task<IReadOnlyList<AgentLog>> LogGetAsync(long epoch, DateOnly day, CancellationToken cancellationToken) =>
        Inner.LogGetAsync(epoch, day, cancellationToken);
}
