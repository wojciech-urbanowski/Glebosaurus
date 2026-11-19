namespace Glebosaurus.Core.Monitoring;

/// <summary>
/// Pilnuje, żeby skan BLE nie startował zbyt często. Android po cichu „głuchnie” aplikacji, która uruchomi skan więcej niż
/// 5 razy w ciągu 30 s – skan rusza bez błędu, ale nie zwraca żadnych reklam (ARCHITEKTURA.md, zasada 12). Na przemian
/// nasłuch i odczyt (<see cref="ListenThenReadSensorReader{TFound}"/>), nasłuch przydziału co kilka minut i ponowienia po
/// błędach startują skan wiele razy, więc radio Androida czeka tu na swoją kolej. Domyślnie z zapasem: 4 starty na 32 s.
/// Czas liczony na zegarze monotonicznym (<see cref="TimeProvider.GetTimestamp"/>) – zmiana zegara systemu nie wydłuża czekania.
/// </summary>
public sealed class ScanStartLimiter(TimeProvider clock, int maxStarts = ScanStartLimiter.DefaultMaxStarts, TimeSpan? window = null)
{
    public const int DefaultMaxStarts = 4;
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(32);

    private readonly TimeSpan span = window ?? DefaultWindow;
    private readonly Queue<long> starts = new();
    private readonly Lock gate = new();

    /// <summary>Rezerwuje start skanu i zwraca, ile trzeba przed nim odczekać (zero – można od razu).</summary>
    public TimeSpan Reserve()
    {
        lock (gate)
        {
            var now = clock.GetTimestamp();
            while (starts.Count > 0 && Elapsed(starts.Peek(), now) >= span) starts.Dequeue();
            var delay = TimeSpan.Zero;
            if (starts.Count >= maxStarts)
            {
                delay = span - Elapsed(starts.Dequeue(), now);
                if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
            }
            starts.Enqueue(now + (long)(delay.TotalSeconds * clock.TimestampFrequency));
            return delay;
        }
    }

    /// <summary>Czeka na swoją kolej (jeśli trzeba) i rezerwuje start skanu.</summary>
    public async Task WaitTurnAsync(CancellationToken cancellationToken)
    {
        var delay = Reserve();
        if (delay > TimeSpan.Zero) await Task.Delay(delay, clock, cancellationToken);
    }

    private TimeSpan Elapsed(long from, long now) => TimeSpan.FromSeconds((double)(now - from) / clock.TimestampFrequency);
}
