using System.Text.Json.Nodes;
using Glebosaurus.Core.Cloud;
using Glebosaurus.Core.Config;
using Glebosaurus.Core.Domain;
using Glebosaurus.Core.Monitoring;

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

    // ---- Strona nadawcy: kolejka maili, hasło, odczyty, alerty, logi (epoka blokady jak w bazie) ----

    public long NotifierEpoch { get; set; } = 5;
    public Dictionary<string, (bool Sent, string? MessageId)> Outbox { get; } = [];
    public MailCredentials Credentials { get; set; } = new("nadawca@gmail.com", "haslo-aplikacji", null);
    public int CredentialCalls { get; private set; }
    public List<StoredReading> CloudReadings { get; } = [];
    public List<CloudAlert> Alerts { get; } = [];
    public List<AgentLog> AgentLogs { get; } = [];

    private void RequireNotifier(long epoch, string function)
    {
        if (epoch != NotifierEpoch) throw new CloudApiException(CloudErrorKind.NotNotifier, $"{function}: to może tylko obecny nadawca", "42501");
    }

    public Task<MailClaimResult> MailClaimAsync(string key, long epoch, CancellationToken cancellationToken)
    {
        if (epoch != NotifierEpoch) return Task.FromResult(new MailClaimResult(false, "not_holder"));
        if (Outbox.TryGetValue(key, out var known))
            return Task.FromResult(new MailClaimResult(false, "already_claimed", known.Sent ? clock.GetUtcNow() : null));
        Outbox[key] = (false, null);
        return Task.FromResult(new MailClaimResult(true, "ok"));
    }

    public Task<bool> MailSentAsync(string key, string? messageId, CancellationToken cancellationToken)
    {
        if (!Outbox.TryGetValue(key, out var known) || known.Sent) return Task.FromResult(false);
        Outbox[key] = (true, messageId);
        return Task.FromResult(true);
    }

    public Task<bool> MailAbandonAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult(Outbox.TryGetValue(key, out var known) && !known.Sent && Outbox.Remove(key));

    public Task<MailCredentials> MailCredentialsAsync(long epoch, CancellationToken cancellationToken)
    {
        CredentialCalls++;
        RequireNotifier(epoch, "mail_credentials");
        return Task.FromResult(Credentials);
    }

    public Task<IReadOnlyList<StoredReading>> ReadingsRangeAsync(IReadOnlyCollection<string> sensors, DateTimeOffset from, DateTimeOffset to,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<StoredReading>>([.. CloudReadings.Where(r => sensors.Contains(r.SensorId) && r.At >= from && r.At <= to).OrderBy(r => r.At)]);

    public Task<IReadOnlyList<CloudAlert>> AlertListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CloudAlert>>([.. Alerts.Where(a => a.ResolvedAt is null)]);

    public Task<AlertOpenResult> AlertOpenAsync(long epoch, string sensor, AlertKind kind, string detail, CancellationToken cancellationToken)
    {
        RequireNotifier(epoch, "alert_open");
        if (Alerts.FirstOrDefault(a => a.Sensor == sensor && a.Kind == kind && a.ResolvedAt is null) is { } open)
            return Task.FromResult(new AlertOpenResult(false, open));
        var alert = new CloudAlert(Alerts.Count + 1, sensor, kind, detail, clock.GetUtcNow(), null, null);
        Alerts.Add(alert);
        return Task.FromResult(new AlertOpenResult(true, alert));
    }

    public Task<bool> AlertResolveAsync(long epoch, long alertId, CancellationToken cancellationToken)
    {
        RequireNotifier(epoch, "alert_resolve");
        var index = Alerts.FindIndex(a => a.Id == alertId && a.ResolvedAt is null);
        if (index < 0) return Task.FromResult(false);
        Alerts[index] = Alerts[index] with { ResolvedAt = clock.GetUtcNow() };
        return Task.FromResult(true);
    }

    public Task<int> AlertNotifiedAsync(long epoch, IReadOnlyCollection<long> alertIds, CancellationToken cancellationToken)
    {
        RequireNotifier(epoch, "alert_notified");
        var count = 0;
        for (var i = 0; i < Alerts.Count; i++)
            if (alertIds.Contains(Alerts[i].Id) && Alerts[i].ResolvedAt is null)
            {
                Alerts[i] = Alerts[i] with { LastNotifiedAt = clock.GetUtcNow() };
                count++;
            }
        return Task.FromResult(count);
    }

    public Task<CommandCreateResult> CommandCreateAsync(string kind, string? sensor, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
    public List<(long Id, JsonObject? Result)> Done { get; } = [];
    public Queue<CloudApiException> DoneFailures { get; } = new();

    public Task<bool> CommandDoneAsync(long commandId, JsonObject? result, CancellationToken cancellationToken)
    {
        if (DoneFailures.TryDequeue(out var failure)) throw failure;
        Done.Add((commandId, result));
        return Task.FromResult(true);
    }

    public List<(DateOnly Day, byte[] Gzip)> Logs { get; } = [];
    public Queue<CloudApiException> LogFailures { get; } = new();

    public Task<LogPutResult> LogPutAsync(DateOnly day, byte[] gzipContent, CancellationToken cancellationToken)
    {
        if (LogFailures.TryDequeue(out var failure)) throw failure;
        Logs.Add((day, gzipContent));
        return Task.FromResult(new LogPutResult(day, gzipContent.Length));
    }
    public Task<IReadOnlyList<AgentLog>> LogGetAsync(long epoch, DateOnly day, CancellationToken cancellationToken)
    {
        RequireNotifier(epoch, "log_get");
        return Task.FromResult<IReadOnlyList<AgentLog>>([.. AgentLogs.Where(l => l.Day == day)]);
    }
}

/// <summary>Sam nasłuch reklam sterowany z testu.</summary>
internal sealed class FakeListener : ISensorListener
{
    public Func<IReadOnlyList<SensorConfig>, IReadOnlyList<HeardSensor>> Script { get; set; } = _ => [];
    public List<string[]> Calls { get; } = [];

    public Task<IReadOnlyList<HeardSensor>> HearAsync(IReadOnlyList<SensorConfig> sensors, TimeSpan window, CancellationToken cancellationToken)
    {
        Calls.Add(sensors.Select(s => s.Id).ToArray());
        return Task.FromResult(Script(sensors));
    }
}

/// <summary>Nasłuch radia sterowany z testu.</summary>
internal sealed class FakeHearing : ISensorHearingSource
{
    public Dictionary<string, SensorObservation> Current { get; } = [];
    public IReadOnlyDictionary<string, SensorObservation> Observations() => Current;
}
