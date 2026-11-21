using Glebosaurus.Core.Config;
using Glebosaurus.Core.Monitoring;
using Glebosaurus.Tuya;

namespace Glebosaurus.Core.Pairing;

/// <summary>Czujnik w trybie parowania widziany w reklamach: adres (MAC, gdy platforma go zna) i tożsamość Tuya.</summary>
/// <param name="Handle">Obiekt radia (np. urządzenie Shiny) – procedura go nie dotyka, tylko oddaje do <see cref="ISensorPairer"/>.</param>
public sealed record PairCandidate(string Address, ulong? Mac, TuyaIdentity Identity, object Handle);

/// <summary>Radio do parowania (ARCHITEKTURA.md, zasada 5): skan kandydatów, przypisanie klucza, weryfikacja odczytem.</summary>
public interface ISensorPairer
{
    /// <summary>
    /// Kandydaci (<see cref="PairingRules.IsCandidate"/>): niesparowany czujnik znanego typu albo – przy wznawianiu – ten o
    /// UUID z klucza; skan kończy się chwilę po pierwszym kandydacie (czy nie ma drugiego) albo po <paramref name="timeout"/>.
    /// </summary>
    Task<IReadOnlyList<PairCandidate>> FindCandidatesAsync(TuyaCredentials? existing, ulong? wantedMac, TimeSpan timeout,
        CancellationToken cancellationToken);

    /// <summary>Przypisanie klucza. <paramref name="beforeKeyLeaves"/> rusza tuż przed wysłaniem klucza do czujnika.</summary>
    Task BindAsync(PairCandidate candidate, TuyaCredentials credentials, Func<Task> beforeKeyLeaves, CancellationToken cancellationToken);

    /// <summary>Czeka na reklamę czujnika z tym kluczem (tuż po przypisaniu czujnik bywa cichy) i czyta go na nowym połączeniu.</summary>
    Task<SensorReadOutcome> VerifyAsync(SensorConfig sensor, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>Gdzie leży klucz parowania: plik (polecenie <c>pair</c>) albo Vault w chmurze (parowanie zdalne).</summary>
public interface IPairingKeyStore
{
    /// <summary>Klucz z przerwanego parowania albo czujnika o tym id (po resecie); null – nowy czujnik.</summary>
    Task<TuyaCredentials?> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(TuyaCredentials credentials, CancellationToken cancellationToken);
}

public enum PairStage
{
    /// <summary>Skan działa – teraz trzeba włożyć baterie (czujnik nadaje jako niesparowany ok. 20 s).</summary>
    Scanning,
    Found,
    Binding,
    Verifying
}

public sealed record PairProgress(PairStage Stage, string Text, DateTimeOffset? Until = null, string? Address = null);

/// <param name="Candidates">Przy kilku kandydatach – ich adresy (właściciel wskazuje MAC).</param>
public sealed record PairResult(bool Ok, string? Error, string? Mac, SoilReading? Reading, IReadOnlyList<string> Candidates)
{
    public static PairResult Fail(string error, IReadOnlyList<string>? candidates = null) => new(false, error, null, null, candidates ?? []);
}

/// <summary>
/// Parowanie krok po kroku, niezależnie od radia i od miejsca klucza (ARCHITEKTURA.md, zasada 5): skan rusza pierwszy (dopiero
/// wtedy postęp „wkładaj baterie”), przy kilku kandydatach – odmowa z listą adresów (nie zgadujemy, który czujnik), nowy
/// klucz zapisany PRZED wysłaniem do czujnika (przerwane parowanie wznawia się tym samym kluczem), przypisanie, chwila
/// spokoju, weryfikacja odczytem na nowym połączeniu. Używa jej parowanie zdalne ze strony w chmurze.
/// </summary>
public sealed class PairingProcedure(ISensorPairer pairer, TimeProvider clock)
{
    public static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(1.5);

    /// <summary>Sparowany czujnik reklamuje się rzadko (ok. 1 pakiet na 12 s), a tuż po przypisaniu bywa cichy.</summary>
    public static readonly TimeSpan VerifyTimeout = TimeSpan.FromSeconds(90);

    public async Task<PairResult> RunAsync(string sensorId, string name, ulong? wantedMac, TimeSpan scanTimeout, IPairingKeyStore keys,
        Func<PairProgress, Task> progress, CancellationToken cancellationToken)
    {
        var credentials = await keys.LoadAsync(cancellationToken);
        await progress(new(PairStage.Scanning, credentials is null
            ? "Skan działa – włóż teraz baterie do czujnika (nadaje jako niesparowany tylko ok. 20 s)."
            : "Skan działa – wznawiam parowanie tym samym kluczem; zresetuj czujnik albo włóż baterie ponownie.",
            clock.GetUtcNow() + scanTimeout));

        var candidates = await pairer.FindCandidatesAsync(credentials, wantedMac, scanTimeout, cancellationToken);
        if (candidates.Count == 0)
            return PairResult.Fail(credentials is null
                ? "nie znaleziono niesparowanego czujnika znanego typu – włóż baterie ponownie i spróbuj jeszcze raz"
                : "nie znaleziono czujnika z zapisanego klucza – zresetuj go albo włóż baterie ponownie i spróbuj jeszcze raz");
        if (candidates.Count > 1)
            return PairResult.Fail("w zasięgu jest kilka niesparowanych czujników – nie wybieram sam; podaj MAC właściwego",
                [.. candidates.Select(c => c.Address)]);

        var target = candidates[0];
        await progress(new(PairStage.Found,
            $"Znaleziono czujnik {target.Address} ({target.Identity.ProductId ?? "produkt ukryty"}, {(target.Identity.IsBound ? "sparowany" : "niesparowany")}).",
            Address: target.Address));

        if (credentials is null)
        {
            credentials = TuyaCredentials.Create(target.Mac is { } mac ? BleAddress.Format(mac) : "", target.Identity);
            await keys.SaveAsync(credentials, cancellationToken);
        }

        if (!target.Identity.IsBound)
        {
            await progress(new(PairStage.Binding, "Przypisuję klucz…", Address: target.Address));
            try
            {
                await pairer.BindAsync(target, credentials, async () =>
                {
                    credentials.BindingAttempted = true;
                    await keys.SaveAsync(credentials, cancellationToken);
                }, cancellationToken);
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested)
            {
                return PairResult.Fail($"przypisanie nie powiodło się ({e.Message}) – klucz jest zapisany; włóż baterie ponownie i spróbuj jeszcze raz (ten sam klucz)");
            }
            await Task.Delay(SettleDelay, clock, cancellationToken);
        }

        await progress(new(PairStage.Verifying, "Sprawdzam klucz odczytem na nowym połączeniu (do 90 s)…", Address: target.Address));
        var outcome = await pairer.VerifyAsync(new SensorConfig { Id = sensorId, Name = name, Tuya = credentials }, VerifyTimeout, cancellationToken);
        if (outcome.Failure != SensorReadFailure.None || outcome.Reading is not { } reading)
            return PairResult.Fail(outcome.Failure == SensorReadFailure.NotFound
                ? "czujnik nie pojawił się w reklamach po przypisaniu – klucz jest zapisany; spróbuj jeszcze raz"
                : $"weryfikacja nie powiodła się ({outcome.Failure}{(outcome.Error is null ? "" : ": " + outcome.Error)}) – klucz jest zapisany; spróbuj jeszcze raz");

        credentials.Verified = true;
        await keys.SaveAsync(credentials, cancellationToken);
        return new(true, null, credentials.Mac, reading, []);
    }
}
