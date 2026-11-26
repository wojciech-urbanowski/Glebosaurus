using Microsoft.Extensions.Logging;

namespace Glebosaurus.Core.Logging;

/// <summary>
/// Ostrzeżenie Kestrela (panel WWW) „the heartbeat has been running for … This could be caused by thread pool starvation”
/// (zdarzenie 22, <c>HeartbeatSlow</c>). Zegar Kestrela ma własny wątek, a mierzy tylko swoje krótkie wywołania – więc to
/// postój całego procesu, nie głód puli wątków. Agent na Windows działa celowo z niskim priorytetem (zadanie Harmonogramu z
/// priorytetem 7 – ARCHITEKTURA.md, zasada 7) i przy zajętym komputerze stoi czasem 1–4 s; to oczekiwane, więc dziennik
/// zapisuje taki wpis jako informację (nie liczy się do ostrzeżeń w logu administratora). Ostrzeżeniem zostaje postój od
/// <see cref="WarningFrom"/>: tego sam niski priorytet nie tłumaczy – Windows podbija zagłodzony wątek po ok. 4 s.
/// </summary>
public static class KestrelHeartbeat
{
    public const string Category = "Microsoft.AspNetCore.Server.Kestrel";
    public const string EventName = "HeartbeatSlow";
    public static readonly TimeSpan WarningFrom = TimeSpan.FromSeconds(10);

    /// <summary>Poziom, z jakim zapisać wpis: obniżony dla krótkiego postoju, dla każdego innego wpisu bez zmian.</summary>
    public static LogLevel Level<TState>(string category, EventId eventId, LogLevel level, TState state)
    {
        if (level != LogLevel.Warning || eventId.Name != EventName || category != Category) return level;
        return Duration(state) is { } duration && duration < WarningFrom ? LogLevel.Information : level;
    }

    // Nieznany kształt wpisu (inna wersja Kestrela) – bez czasu postoju zostaje ostrzeżenie, żeby niczego nie ukryć.
    private static TimeSpan? Duration<TState>(TState state)
    {
        if (state is not IReadOnlyList<KeyValuePair<string, object?>> values) return null;
        foreach (var (key, value) in values)
            if (key == "heartbeatDuration" && value is TimeSpan duration) return duration;
        return null;
    }
}
