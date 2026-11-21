using System.Globalization;
using System.Text.Json.Nodes;
using Glebosaurus.Core.Cloud;
using Glebosaurus.Tuya;
using Microsoft.Extensions.Logging;

namespace Glebosaurus.Core.Pairing;

/// <summary>
/// Radio na wyłączność: zbieracz bierze je na nasłuch i odczyty tylko, gdy jest wolne (inaczej pomija obrót), a parowanie
/// czeka, aż skończy się trwający odczyt – czujnik przyjmuje jedno połączenie, a skan i połączenie naraz psują jedno i drugie.
/// </summary>
public sealed class RadioGate
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public bool TryEnter() => gate.Wait(0);

    public Task EnterAsync(CancellationToken cancellationToken) => gate.WaitAsync(cancellationToken);

    public void Exit() => gate.Release();

    public bool Busy => gate.CurrentCount == 0;
}

/// <summary>Klucz parowania zdalnego w Vault (przez funkcje agenta <c>pair_key_get</c> / <c>pair_key_put</c>).</summary>
public sealed class CloudPairingKeyStore(ICloudApi api, long commandId) : IPairingKeyStore
{
    public Task<TuyaCredentials?> LoadAsync(CancellationToken cancellationToken) => api.PairKeyGetAsync(commandId, cancellationToken);

    public Task SaveAsync(TuyaCredentials credentials, CancellationToken cancellationToken) => api.PairKeyPutAsync(commandId, credentials, cancellationToken);
}

/// <summary>
/// Parowanie zdalne (faza 3d, ARCHITEKTURA.md, zasada 5): polecenie <c>pair</c> ze strony w chmurze, skierowane do tego
/// agenta. Czeka na wolne radio (odczyty stają), zgłasza postęp (<c>command_progress</c> – strona mówi „włóż baterie” dopiero
/// po „skan działa”), klucz trzyma w Vault, a po udanej weryfikacji woła <c>pair_complete</c> (baza zakłada czujnik).
/// Jedno parowanie naraz.
/// </summary>
public sealed class RemotePairing(ISensorPairer pairer, ICloudApi api, RadioGate radio, TimeProvider clock, ILogger logger)
{
    /// <summary>Skan kandydatów – z zapasem na to, że właściciel zobaczy „włóż baterie” po kilku sekundach od startu.</summary>
    public static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(150);

    private int running;

    public bool Running => Volatile.Read(ref running) == 1;

    /// <summary>Rusza parowanie w tle; zwraca zadanie (do testów). Drugie polecenie w trakcie – odmowa w wyniku.</summary>
    public Task StartAsync(CloudCommand command, CancellationToken stopping)
    {
        if (Interlocked.Exchange(ref running, 1) == 1)
            return DoneAsync(command.Id, Failure("ten agent już paruje inny czujnik – spróbuj po jego zakończeniu"), stopping);
        return Task.Run(() => RunAsync(command, stopping), CancellationToken.None);
    }

    private async Task RunAsync(CloudCommand command, CancellationToken stopping)
    {
        try
        {
            var sensorId = command.Params?["sensor"]?.GetValue<string>() ?? "";
            var name = command.Params?["name"]?.GetValue<string>() ?? sensorId;
            ulong? mac = command.Params?["mac"]?.GetValue<string>() is { } text && BleAddress.TryParse(text, out var parsed) ? parsed : null;
            logger.LogInformation("Polecenie z chmury: parowanie czujnika {Sensor}{Mac} – czekam na wolne radio.", sensorId,
                mac is { } m ? $" ({BleAddress.Format(m)})" : "");
            await ProgressAsync(command.Id, new PairProgress(PairStage.Scanning, "Agent kończy trwający odczyt i za chwilę uruchomi skan…"), stopping, starting: true);

            await radio.EnterAsync(stopping);
            try
            {
                var result = await new PairingProcedure(pairer, clock).RunAsync(sensorId, name, mac, ScanTimeout,
                    new CloudPairingKeyStore(api, command.Id), progress => ProgressAsync(command.Id, progress, stopping), stopping);
                if (result.Ok)
                {
                    var complete = await api.PairCompleteAsync(command.Id, Reading(result), stopping);
                    logger.LogInformation("Sparowano czujnik {Sensor} ({Mac}) – {What}.", complete.Sensor, complete.Mac,
                        complete.Created ? "czujnik dodany w chmurze" : "nowy klucz istniejącego czujnika");
                }
                else
                {
                    logger.LogWarning("Parowanie czujnika {Sensor} nie powiodło się: {Error}", sensorId, result.Error);
                    var failure = Failure(result.Error ?? "nieudane parowanie");
                    if (result.Candidates.Count > 0) failure["candidates"] = new JsonArray([.. result.Candidates.Select(c => (JsonNode)JsonValue.Create(c))]);
                    await DoneAsync(command.Id, failure, stopping);
                }
            }
            finally
            {
                radio.Exit();
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            logger.LogWarning("Parowanie przerwane zatrzymaniem agenta – klucz (jeśli powstał) jest w chmurze; parowanie można powtórzyć.");
        }
        catch (Exception e)
        {
            logger.LogError(e, "Parowanie zakończone błędem.");
            await DoneAsync(command.Id, Failure($"błąd agenta: {e.Message}"), CancellationToken.None);
        }
        finally
        {
            Volatile.Write(ref running, 0);
        }
    }

    private static JsonObject Reading(PairResult result) => new()
    {
        ["ok"] = true,
        ["moisture"] = result.Reading?.MoisturePercent,
        ["temperature"] = result.Reading?.SoilTemperatureCelsius is { } t ? JsonValue.Create(Math.Round((double)t, 1)) : null,
        ["battery"] = result.Reading?.BatteryPercent
    };

    private static JsonObject Failure(string error) => new() { ["ok"] = false, ["error"] = error };

    private async Task ProgressAsync(long commandId, PairProgress progress, CancellationToken cancellationToken, bool starting = false)
    {
        var body = new JsonObject
        {
            ["stage"] = starting ? "waiting" : progress.Stage.ToString().ToLowerInvariant(),
            ["text"] = progress.Text,
            ["until"] = progress.Until?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            ["address"] = progress.Address
        };
        try { await api.CommandProgressAsync(commandId, body, cancellationToken); }
        catch (CloudApiException e) { logger.LogWarning("Postęp parowania nie dotarł do chmury: {Error}", e.Message); }
    }

    private async Task DoneAsync(long commandId, JsonObject result, CancellationToken cancellationToken)
    {
        try { await api.CommandDoneAsync(commandId, result, cancellationToken); }
        catch (CloudApiException e) { logger.LogWarning("Wynik parowania nie dotarł do chmury: {Error}", e.Message); }
    }
}
