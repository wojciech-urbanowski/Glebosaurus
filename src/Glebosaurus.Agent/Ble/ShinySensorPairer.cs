using Glebosaurus.Core.Config;
using Glebosaurus.Core.Monitoring;
using Glebosaurus.Core.Pairing;
using Glebosaurus.Tuya;
using Shiny.BluetoothLE;

namespace Glebosaurus.Agent.Ble;

/// <summary>
/// Parowanie przez Shiny (ARCHITEKTURA.md, zasada 5) – kroki BLE polecenia <c>pair</c> i parowania zdalnego (faza 3d):
/// skan kandydatów z oknem na drugiego, przypisanie klucza w sesji Tuya, weryfikacja odczytem na nowym połączeniu.
/// </summary>
internal sealed class ShinySensorPairer(BleHost ble, int responseTimeoutSeconds = 12, Action<string>? trace = null) : ISensorPairer
{
    /// <summary>Po zobaczeniu pierwszego kandydata czekamy chwilę, czy nie ma drugiego – nie chcemy sparować niewłaściwego czujnika.</summary>
    private static readonly TimeSpan CompetitorWindow = TimeSpan.FromSeconds(1.5);

    public async Task<IReadOnlyList<PairCandidate>> FindCandidatesAsync(TuyaCredentials? existing, ulong? wantedMac, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var candidates = new Dictionary<string, SeenSensor>();
        DateTimeOffset? firstSeen = null;
        await BleScanner.ScanAsync(ble, timeout, sensor =>
        {
            if (IsCandidate(sensor, existing, wantedMac) && candidates.TryAdd(sensor.Peripheral.Uuid, sensor))
                firstSeen ??= DateTimeOffset.UtcNow;
            return firstSeen is { } first && DateTimeOffset.UtcNow - first >= CompetitorWindow;
        }, cancellationToken);
        return [.. candidates.Values.Select(c =>
        {
            ulong? mac = BleAddress.TryExtract(c.Peripheral.Uuid, out var address) ? address : null;
            return new PairCandidate(mac is { } known ? BleAddress.Format(known) : c.Peripheral.Uuid, mac, c.Identity!, c);
        })];
    }

    /// <summary>Opis kandydata do konsoli (polecenie <c>pair</c> przy kilku kandydatach).</summary>
    public static string Describe(PairCandidate candidate) => ((SeenSensor)candidate.Handle).Describe(verbose: false);

    private static bool IsCandidate(SeenSensor sensor, TuyaCredentials? existing, ulong? wantedMac)
    {
        ulong? address = BleAddress.TryExtract(sensor.Peripheral.Uuid, out var extracted) ? extracted : null;
        return PairingRules.IsCandidate(sensor.Identity, sensor.IsConnectable, address, existing, wantedMac);
    }

    public async Task BindAsync(PairCandidate candidate, TuyaCredentials credentials, Func<Task> beforeKeyLeaves, CancellationToken cancellationToken)
    {
        var peripheral = ((SeenSensor)candidate.Handle).Peripheral;
        try
        {
            await peripheral.ConnectAsync(new ConnectionConfig(false), cancellationToken, TimeSpan.FromSeconds(20));
            var session = new TuyaSession(new ShinyGattLink(peripheral), credentials, TimeSpan.FromSeconds(responseTimeoutSeconds), trace);
            // Klucz musi być zapisany (plik albo Vault), zanim opuści komputer.
            await session.BindAsync(_ => beforeKeyLeaves(), cancellationToken);
        }
        finally
        {
            await ShinySensorRadio.DisconnectQuietlyAsync(peripheral);
        }
    }

    public async Task<SensorReadOutcome> VerifyAsync(SensorConfig sensor, TimeSpan timeout, CancellationToken cancellationToken)
    {
        SeenSensor? target = null;
        await BleScanner.ScanAsync(ble, timeout, seen =>
        {
            if (!seen.Matches(sensor.Tuya)) return false;
            target = seen;
            return true;
        }, cancellationToken);
        if (target is null) return new(sensor, null, SensorReadFailure.NotFound, "czujnik nie pojawił się w reklamach po przypisaniu");
        return await ShinySensorRadio.ReadOneAsync(sensor, target, cancellationToken);
    }
}
