namespace Glebosaurus.Core.Monitoring;

/// <summary>Co się stało z prośbą o odczyt jednego czujnika poza harmonogramem.</summary>
public enum ReadRequestStatus
{
    /// <summary>Dopisany do kolejki – silnik przeczyta go w najbliższym cyklu (pętla budzi się od razu).</summary>
    Queued,

    /// <summary>Już czekał w kolejce – nic się nie zmienia.</summary>
    AlreadyQueued,

    /// <summary>Odczyt tego czujnika właśnie trwa.</summary>
    InProgress,

    /// <summary>Ostatnia próba była przed chwilą – odczyt na żądanie dopiero po <see cref="MonitorEngine.ForcedReadCooldown"/> (bateria czujnika).</summary>
    TooSoon,

    /// <summary>Nie ma takiego czujnika w konfiguracji.</summary>
    Unknown
}

/// <param name="AvailableAt">Przy <see cref="ReadRequestStatus.TooSoon"/>: od kiedy można zlecić odczyt.</param>
public sealed record ReadRequestItem(string SensorId, string? SensorName, ReadRequestStatus Status, DateTimeOffset? AvailableAt = null);

public sealed record ReadRequestResult(IReadOnlyList<ReadRequestItem> Items);
