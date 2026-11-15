using System.Net;

namespace Glebosaurus.Web;

/// <summary>
/// Ograniczenie zgadywania hasła: po <see cref="MaxFailures"/> błędnych próbach z jednego adresu w ciągu
/// <see cref="Window"/> adres jest blokowany na <see cref="BlockFor"/>. Udane logowanie czyści licznik adresu.
/// </summary>
public sealed class LoginThrottle
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan BlockFor = TimeSpan.FromMinutes(15);

    private const int MaxTracked = 1000;

    private readonly Lock gate = new();
    private readonly Dictionary<IPAddress, Entry> entries = [];

    private sealed record Entry(int Failures, DateTimeOffset WindowStart, DateTimeOffset? BlockedUntil);

    /// <returns>Do kiedy adres jest zablokowany albo null, gdy może próbować.</returns>
    public DateTimeOffset? BlockedUntil(IPAddress address, DateTimeOffset now)
    {
        lock (gate)
            return entries.TryGetValue(Normalize(address), out var entry) && entry.BlockedUntil is { } until && until > now ? until : null;
    }

    public void Failure(IPAddress address, DateTimeOffset now)
    {
        address = Normalize(address);
        lock (gate)
        {
            if (entries.Count >= MaxTracked) Prune(now);
            var entry = entries.TryGetValue(address, out var existing) && now - existing.WindowStart < Window && existing.BlockedUntil is null
                ? existing with { Failures = existing.Failures + 1 }
                : new Entry(1, now, null);
            if (entry.Failures >= MaxFailures) entry = entry with { BlockedUntil = now + BlockFor };
            entries[address] = entry;
        }
    }

    public void Success(IPAddress address)
    {
        lock (gate) entries.Remove(Normalize(address));
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var stale in entries.Where(e => (e.Value.BlockedUntil ?? e.Value.WindowStart + Window) <= now).Select(e => e.Key).ToList())
            entries.Remove(stale);
        if (entries.Count >= MaxTracked) entries.Remove(entries.MinBy(e => e.Value.WindowStart).Key);
    }

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
