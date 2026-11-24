using System.Text.Json.Nodes;
using Glebosaurus.Core.Cloud;
using Glebosaurus.Core.Config;
using Glebosaurus.Core.Domain;
using Glebosaurus.Core.Monitoring;
using Glebosaurus.Core.Pairing;
using Glebosaurus.Tuya;
using Microsoft.Extensions.Logging.Abstractions;

namespace Glebosaurus.Tests;

/// <summary>
/// Parowanie (ARCHITEKTURA.md, zasada 5; zdalne – faza 3d): skan pierwszy, nigdy nie zgadujemy przy kilku kandydatach, klucz
/// zapisany przed wysłaniem do czujnika, wznowienie tym samym kluczem, weryfikacja odczytem; zbieracz oddaje radio.
/// </summary>
public sealed class PairingTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private const ulong Mac = 0xDC2351CBF77D;

    private static TuyaIdentity Identity(bool bound = false) => new("07aaad78f40b0a74", "gvygg3m8", bound, 3);

    /// <summary>Radio do prób: zapisuje kolejność kroków (także zapis klucza z magazynu).</summary>
    private sealed class FakePairer(List<string> steps) : ISensorPairer
    {
        public List<PairCandidate> Candidates { get; } = [new(BleAddress.Format(Mac), Mac, Identity(), new object())];
        public Exception? BindFailure { get; set; }
        public SensorReadFailure VerifyFailure { get; set; } = SensorReadFailure.None;
        public TuyaCredentials? Existing { get; private set; }

        public Task<IReadOnlyList<PairCandidate>> FindCandidatesAsync(TuyaCredentials? existing, ulong? wantedMac, TimeSpan timeout, CancellationToken cancellationToken)
        {
            steps.Add("scan");
            Existing = existing;
            return Task.FromResult<IReadOnlyList<PairCandidate>>(Candidates);
        }

        public async Task BindAsync(PairCandidate candidate, TuyaCredentials credentials, Func<Task> beforeKeyLeaves, CancellationToken cancellationToken)
        {
            await beforeKeyLeaves();
            steps.Add("send-key");
            if (BindFailure is { } failure) throw failure;
        }

        public Task<SensorReadOutcome> VerifyAsync(SensorConfig sensor, TimeSpan timeout, CancellationToken cancellationToken)
        {
            steps.Add("verify");
            var reading = new SensorReading(DateTimeOffset.UtcNow, 41, 21.5m, 88, 2);
            return Task.FromResult(VerifyFailure == SensorReadFailure.None
                ? new SensorReadOutcome(sensor, reading, SensorReadFailure.None)
                : new SensorReadOutcome(sensor, null, VerifyFailure, "brak odpowiedzi"));
        }
    }

    private sealed class MemoryKeys(List<string> steps, TuyaCredentials? existing = null) : IPairingKeyStore
    {
        public List<TuyaCredentials> Saved { get; } = [];

        public Task<TuyaCredentials?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(existing);

        public Task SaveAsync(TuyaCredentials credentials, CancellationToken cancellationToken)
        {
            steps.Add(credentials.Verified ? "save-verified" : credentials.BindingAttempted ? "save-before-send" : "save-new");
            Saved.Add(credentials);
            return Task.CompletedTask;
        }
    }

    private static async Task<(PairResult Result, List<string> Steps, List<PairProgress> Progress, MemoryKeys Keys)> RunAsync(
        Action<FakePairer>? arrange = null, TuyaCredentials? existing = null)
    {
        var steps = new List<string>();
        var pairer = new FakePairer(steps);
        arrange?.Invoke(pairer);
        var keys = new MemoryKeys(steps, existing);
        var progress = new List<PairProgress>();
        var result = await new PairingProcedure(pairer, new ManualTimeProvider(Fixtures.T0))
            .RunAsync("nowy", "Nowy czujnik", null, TimeSpan.FromSeconds(150), keys, p =>
            {
                progress.Add(p);
                steps.Add("progress:" + p.Stage);
                return Task.CompletedTask;
            }, Ct);
        return (result, steps, progress, keys);
    }

    [Fact]
    public async Task NewSensor_ScanFirst_KeySavedBeforeItLeaves_ThenVerifiedByReading()
    {
        var (result, steps, progress, keys) = await RunAsync();

        Assert.True(result.Ok);
        Assert.Equal("DC:23:51:CB:F7:7D", result.Mac);
        Assert.Equal(41, result.Reading!.MoisturePercent);
        Assert.Equal(["progress:Scanning", "scan", "progress:Found", "save-new", "progress:Binding", "save-before-send", "send-key",
            "progress:Verifying", "verify", "save-verified"], steps);
        Assert.NotNull(progress[0].Until);                         // strona pokazuje, ile zostało na włożenie baterii
        Assert.Contains("włóż teraz baterie", progress[0].Text);
        Assert.All(keys.Saved, k => Assert.Equal("DC:23:51:CB:F7:7D", k.Mac));
        Assert.Single(keys.Saved.Select(k => k.LocalKeyHex).Distinct());   // jeden klucz od początku do końca
    }

    [Fact]
    public async Task SeveralCandidates_NothingIsSent_AddressesComeBack()
    {
        var (result, steps, _, keys) = await RunAsync(p => p.Candidates.Add(new("DC:23:51:00:00:02", 0xDC2351000002, Identity(), new object())));

        Assert.False(result.Ok);
        Assert.Equal(["DC:23:51:CB:F7:7D", "DC:23:51:00:00:02"], result.Candidates);
        Assert.Empty(keys.Saved);
        Assert.DoesNotContain("send-key", steps);
    }

    [Fact]
    public async Task NoCandidate_FailsWithAdvice()
    {
        var (result, _, _, keys) = await RunAsync(p => p.Candidates.Clear());

        Assert.False(result.Ok);
        Assert.Contains("włóż baterie ponownie", result.Error);
        Assert.Empty(keys.Saved);
    }

    [Fact]
    public async Task InterruptedPairing_ResumesWithTheSameKey()
    {
        var existing = TuyaCredentials.Create("DC:23:51:CB:F7:7D", Identity());
        existing.BindingAttempted = true;

        var (result, steps, progress, keys) = await RunAsync(existing: existing);

        Assert.True(result.Ok);
        Assert.DoesNotContain("save-new", steps);
        Assert.All(keys.Saved, k => Assert.Equal(existing.LocalKeyHex, k.LocalKeyHex));
        Assert.Contains("tym samym kluczem", progress[0].Text);
    }

    [Fact]
    public async Task AlreadyBoundSensor_GoesStraightToVerification()
    {
        var existing = TuyaCredentials.Create("DC:23:51:CB:F7:7D", Identity());
        var (result, steps, _, _) = await RunAsync(p => p.Candidates[0] = p.Candidates[0] with { Identity = Identity(bound: true) }, existing);

        Assert.True(result.Ok);
        Assert.DoesNotContain("send-key", steps);
    }

    [Fact]
    public async Task FailedBind_OrSilentSensor_KeepsTheKeyForAnotherTry()
    {
        var (bind, bindSteps, _, bindKeys) = await RunAsync(p => p.BindFailure = new TimeoutException("czujnik nie odpowiedział"));
        Assert.False(bind.Ok);
        Assert.Contains("klucz jest zapisany", bind.Error);
        Assert.Contains("save-before-send", bindSteps);
        Assert.DoesNotContain(bindKeys.Saved, k => k.Verified);

        var (verify, _, _, _) = await RunAsync(p => p.VerifyFailure = SensorReadFailure.NotFound);
        Assert.False(verify.Ok);
        Assert.Contains("nie pojawił się", verify.Error);
    }

    // ---- parowanie zdalne w zbieraczu ----------------------------------------------------------------------------

    [Fact]
    public async Task RemotePairing_ReportsProgress_StoresKeyInCloud_AndCompletes()
    {
        var clock = new ManualTimeProvider(Fixtures.T0);
        var api = new FakeCloudApi(clock);
        var steps = new List<string>();
        var radio = new RadioGate();
        var pairing = new RemotePairing(new FakePairer(steps), api, radio, clock, NullLogger.Instance);
        var command = new CloudCommand(77, "pair", null, new JsonObject { ["sensor"] = "nowy", ["name"] = "Nowy" }, Fixtures.T0, 900);

        await pairing.StartAsync(command, Ct);

        Assert.Equal(["waiting", "scanning", "found", "binding", "verifying"], api.Progress.Select(p => (string)p.Progress["stage"]!));
        Assert.True(api.PairKeys[77].Verified);
        var (id, result) = Assert.Single(api.PairCompleted);
        Assert.Equal(77, id);
        Assert.Equal(41, (int)result["moisture"]!);
        Assert.False(radio.Busy);                                   // radio wraca do zbieracza
        Assert.False(pairing.Running);
    }

    [Fact]
    public async Task RemotePairing_SeveralCandidates_FailsWithTheirAddresses()
    {
        var clock = new ManualTimeProvider(Fixtures.T0);
        var api = new FakeCloudApi(clock);
        var pairer = new FakePairer([]);
        pairer.Candidates.Add(new("DC:23:51:00:00:02", 0xDC2351000002, Identity(), new object()));
        var pairing = new RemotePairing(pairer, api, new RadioGate(), clock, NullLogger.Instance);

        await pairing.StartAsync(new CloudCommand(78, "pair", null, new JsonObject { ["sensor"] = "nowy" }, Fixtures.T0, 900), Ct);

        var (_, result) = Assert.Single(api.Done);
        Assert.False((bool)result!["ok"]!);
        Assert.Equal(2, result["candidates"]!.AsArray().Count);
        Assert.Empty(api.PairCompleted);
    }
}
