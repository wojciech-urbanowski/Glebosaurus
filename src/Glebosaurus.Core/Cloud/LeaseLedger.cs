namespace Glebosaurus.Core.Cloud;

/// <summary>
/// Dzierżawy agenta liczone po jego stronie (ARCHITEKTURA.md, zasada 2): ważność biegnie od chwili <b>wysłania</b> prośby
/// na zegarze monotonicznym agenta (<see cref="TimeProvider.GetTimestamp"/>), więc wychodzi krótsza niż w bazie –
/// opóźnienie sieci i rozjechany zegar ścienny nie dadzą dwóch właścicieli naraz. Bez kolejnej udanej synchronizacji
/// ważność po prostu się kończy (praca bez sieci do końca dzierżawy).
/// </summary>
public sealed class LeaseLedger(TimeProvider clock)
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);

    private sealed record Entry(long Epoch, long ValidUntil);

    /// <summary>Znacznik czasu do zapamiętania tuż przed wysłaniem prośby o dzierżawę (synchronizacji).</summary>
    public long Now() => clock.GetTimestamp();

    /// <summary>
    /// Wyniki próśb wysłanych w chwili <paramref name="sentAt"/>: przyznane – epoka i ważność od tej chwili; odmowa albo błąd
    /// – dzierżawy nie ma (także jeśli agent sądził, że ją ma).
    /// </summary>
    public void Apply(long sentAt, IEnumerable<LeaseResult> results)
    {
        lock (gate)
        {
            foreach (var result in results)
            {
                if (result is { Granted: true, Epoch: { } epoch, ValidSeconds: { } seconds })
                    entries[result.Resource] = new(epoch, sentAt + (long)(seconds * clock.TimestampFrequency));
                else
                    entries.Remove(result.Resource);
            }
        }
    }

    /// <summary>Po oddaniu (albo gdy agent sam rezygnuje) – od tej chwili zasób nie jest jego.</summary>
    public void Forget(string resource)
    {
        lock (gate) entries.Remove(resource);
    }

    /// <summary>Ile ważności zostało; <see cref="TimeSpan.Zero"/>, gdy dzierżawy nie ma albo wygasła.</summary>
    public TimeSpan Remaining(string resource)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(resource, out var entry)) return TimeSpan.Zero;
            var left = clock.GetElapsedTime(clock.GetTimestamp(), entry.ValidUntil);
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    /// <summary>Czy agent ma dzierżawę ważną jeszcze co najmniej <paramref name="atLeast"/>.</summary>
    public bool Holds(string resource, TimeSpan atLeast) => Remaining(resource) is var left && left > TimeSpan.Zero && left >= atLeast;

    /// <summary>Epoka dzierżawy, którą agent uważa za swoją (także wygasłej, dopóki baza nie odmówi) – do oddania i do maili.</summary>
    public long? Epoch(string resource)
    {
        lock (gate) return entries.TryGetValue(resource, out var entry) ? entry.Epoch : null;
    }

    /// <summary>Zasoby, które agent uważa za swoje (ważne albo wygasłe, ale nieodebrane), z epokami.</summary>
    public IReadOnlyList<LeaseRelease> Known()
    {
        lock (gate) return [.. entries.Select(e => new LeaseRelease(e.Key, e.Value.Epoch)).OrderBy(e => e.Resource, StringComparer.Ordinal)];
    }
}
