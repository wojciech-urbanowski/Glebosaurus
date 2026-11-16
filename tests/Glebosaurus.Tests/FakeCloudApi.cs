using System.Text.Json.Nodes;
using Glebosaurus.Core.Cloud;
using Glebosaurus.Core.Domain;

namespace Glebosaurus.Tests;

/// <summary>
/// Chmura w pamięci dla testów synchronizacji: odczyty idempotentne po (czujnik, czas), nieznane czujniki odrzucane,
/// prośby o dzierżawy przyznawane (chyba że zasób jest na liście odmów), opóźnienie i awarie na żądanie.
/// </summary>
internal sealed class FakeCloudApi(ManualTimeProvider clock) : ICloudApi
{
    public HashSet<string> KnownSensors { get; } = ["s1", "s2"];
    public HashSet<(string Sensor, DateTimeOffset At)> Stored { get; } = [];
    public HashSet<string> Denied { get; } = [];
    public List<SyncRequest> Requests { get; } = [];
    public Queue<CloudApiException> Failures { get; } = new();
    public AgentIdentity Identity { get; set; } = new("bestia5", false, null);
    public IReadOnlyList<CloudCommand> Commands { get; set; } = [];
    public long ConfigVersion { get; set; } = 1;
    public TimeSpan Latency { get; set; }

    /// <summary>Awaria po zapisaniu – baza przyjęła paczkę, ale agent nie dostał odpowiedzi.</summary>
    public bool LoseNextResponse { get; set; }

    private readonly Dictionary<string, long> epochs = [];

    public Task<SyncResponse> SyncAsync(SyncRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        clock.Advance(Latency);
        if (Failures.TryDequeue(out var failure)) throw failure;

        var rejected = new List<RejectedReading>();
        var inserted = 0;
        foreach (var reading in request.Readings)
        {
            if (!KnownSensors.Contains(reading.Sensor)) rejected.Add(new(reading.Sensor, reading.At, "unknown_sensor"));
            else if (Stored.Add((reading.Sensor, reading.At))) inserted++;
        }
        var leases = request.Claims.Select(claim =>
        {
            if (Denied.Contains(claim)) return new LeaseResult(claim, false) { Holder = "inny", RetryInSeconds = 600 };
            var epoch = epochs.TryGetValue(claim, out var known) ? known : epochs[claim] = epochs.Count + 1;
            return new LeaseResult(claim, true) { Epoch = epoch, ValidSeconds = claim == CloudProtocol.NotifierResource ? 300 : 1800 };
        }).ToList();

        if (LoseNextResponse)
        {
            LoseNextResponse = false;
            throw new CloudApiException(CloudErrorKind.Unavailable, "agent_sync: połączenie zerwane po wysłaniu");
        }
        return Task.FromResult(new SyncResponse(1, clock.GetUtcNow(), ConfigVersion, Identity)
        {
            Readings = new(request.Readings.Count, inserted, rejected),
            Released = [.. request.Releases.Select(r => new ReleaseResult(r.Resource, true))],
            Leases = leases,
            Commands = Commands
        });
    }

    public CloudConfig? Config { get; set; }
    public Queue<CloudApiException> ConfigFailures { get; } = new();
    public int ConfigCalls { get; private set; }

    public Task<CloudConfig> GetConfigAsync(CancellationToken cancellationToken)
    {
        ConfigCalls++;
        if (ConfigFailures.TryDequeue(out var failure)) throw failure;
        return Task.FromResult(Config ?? throw new InvalidOperationException("Test nie ustawił konfiguracji."));
    }
    public Task<LeaseResult> ClaimAsync(string resource, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<bool> ReleaseAsync(string resource, long epoch, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<MailClaimResult> MailClaimAsync(string key, long epoch, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<bool> MailSentAsync(string key, string? messageId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<bool> MailAbandonAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<MailCredentials> MailCredentialsAsync(long epoch, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<IReadOnlyList<StoredReading>> ReadingsRangeAsync(IReadOnlyCollection<string> sensors, DateTimeOffset from, DateTimeOffset to,
        CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<IReadOnlyList<CloudAlert>> AlertListAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<AlertOpenResult> AlertOpenAsync(long epoch, string sensor, AlertKind kind, string detail, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
    public Task<bool> AlertResolveAsync(long epoch, long alertId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<int> AlertNotifiedAsync(long epoch, IReadOnlyCollection<long> alertIds, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
    public Task<CommandCreateResult> CommandCreateAsync(string kind, string? sensor, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
    public Task<bool> CommandDoneAsync(long commandId, JsonObject? result, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<LogPutResult> LogPutAsync(DateOnly day, byte[] gzipContent, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<IReadOnlyList<AgentLog>> LogGetAsync(long epoch, DateOnly day, CancellationToken cancellationToken) => throw new NotSupportedException();
}

/// <summary>Nasłuch radia sterowany z testu.</summary>
internal sealed class FakeHearing : ISensorHearingSource
{
    public Dictionary<string, SensorObservation> Current { get; } = [];
    public IReadOnlyDictionary<string, SensorObservation> Observations() => Current;
}
