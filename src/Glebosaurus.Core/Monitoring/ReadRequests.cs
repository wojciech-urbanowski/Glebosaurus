namespace Glebosaurus.Core.Monitoring;

/// <summary>Co się stało z prośbą o odczyt jednego czujnika poza harmonogramem (polecenie <c>read_now</c> w chmurze).</summary>
public enum ReadRequestStatus
{
    /// <summary>Polecenie zostawione w chmurze – agent z dzierżawą czujnika przeczyta go przy najbliższej synchronizacji.</summary>
    Queued,

    /// <summary>Takie polecenie już czekało – nic się nie zmienia.</summary>
    AlreadyQueued,

    /// <summary>Nie ma takiego czujnika w konfiguracji.</summary>
    Unknown,

    /// <summary>Polecenia nie udało się zostawić w chmurze (brak sieci, chmura niedostępna).</summary>
    Unavailable
}

public sealed record ReadRequestItem(string SensorId, string? SensorName, ReadRequestStatus Status);

public sealed record ReadRequestResult(IReadOnlyList<ReadRequestItem> Items);
