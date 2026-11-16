using Glebosaurus.Core.Monitoring;

namespace Glebosaurus.Core.Cloud;

/// <summary>
/// Co agent wie o czujnikach z własnego radia – słyszenia z nasłuchu i odczytów, próby i porażki z rzędu. Źródło
/// słyszeń dla <see cref="CloudSync"/> i zasad przydziału. Sygnał wygładzony (średnia wykładnicza), żeby reguła „inny agent
/// słyszy o 6 dB lepiej przez 10 min” nie reagowała na pojedyncze skoki. Bezpieczne z wielu wątków.
/// </summary>
public sealed class SensorHearingTracker(TimeProvider clock) : ISensorHearingSource
{
    /// <summary>Waga nowej próbki w średniej wykładniczej.</summary>
    public const double Smoothing = 0.5;

    private readonly Lock gate = new();
    private readonly Dictionary<string, SensorObservation> observations = new(StringComparer.Ordinal);

    public void RecordHeard(string sensorId, int? rssi)
    {
        var now = clock.GetUtcNow();
        lock (gate)
        {
            var previous = observations.GetValueOrDefault(sensorId) ?? new(null, null, null, null, 0);
            var smoothed = rssi is { } value && SignalStats.IsValid(value)
                ? previous.Rssi is { } old ? (int)Math.Round(old + Smoothing * (value - old), MidpointRounding.AwayFromZero) : value
                : previous.Rssi;
            observations[sensorId] = previous with { LastHeardAt = now, Rssi = smoothed };
        }
    }

    public void RecordAttempt(string sensorId, bool success)
    {
        var now = clock.GetUtcNow();
        lock (gate)
        {
            var previous = observations.GetValueOrDefault(sensorId) ?? new(null, null, null, null, 0);
            observations[sensorId] = success
                ? previous with { LastAttemptAt = now, LastSuccessAt = now, ConsecutiveFailures = 0 }
                : previous with { LastAttemptAt = now, ConsecutiveFailures = previous.ConsecutiveFailures + 1 };
        }
    }

    public IReadOnlyDictionary<string, SensorObservation> Observations()
    {
        lock (gate) return new Dictionary<string, SensorObservation>(observations, StringComparer.Ordinal);
    }
}
