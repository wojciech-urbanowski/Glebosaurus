using Glebosaurus.Core.Config;
using Glebosaurus.Core.Mail;

namespace Glebosaurus.Core.Monitoring;

public static class SnapshotBuilder
{
    /// <summary>
    /// Ile historii trafia do raportu: wykres obejmuje 3 doby, a zapas pozwala wypełnić pierwszy przedział, który zaczyna się
    /// na pełnej godzinie przed tym oknem.
    /// </summary>
    public static readonly TimeSpan HistoryWindow = TimeSpan.FromHours(75);

    /// <summary>Stan każdego czujnika do raportu: ostatni odczyt, otwarte alerty i odczyty z ostatnich dób do wykresu.</summary>
    public static async Task<IReadOnlyList<SensorSnapshot>> BuildAsync(AgentConfig config, IMonitorStore store, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var open = await store.GetOpenAlertsAsync(cancellationToken);
        var snapshots = new List<SensorSnapshot>();
        foreach (var sensor in config.Sensors)
            snapshots.Add(new(sensor, await store.GetLatestAsync(sensor.Id, cancellationToken),
                open.Where(a => a.SensorId == sensor.Id).Select(a => a.Kind).ToList())
            {
                History = await store.GetRangeAsync(sensor.Id, now - HistoryWindow, now, cancellationToken)
            });
        return snapshots;
    }
}
