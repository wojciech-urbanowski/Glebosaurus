namespace Glebosaurus.Core.Monitoring;

/// <summary>Co się stało z prośbą o raport mailowy poza harmonogramem (polecenie <c>report_now</c> w chmurze).</summary>
public enum ReportRequestStatus
{
    /// <summary>Polecenie zostawione w chmurze – wyśle go obecny nadawca.</summary>
    Queued,

    /// <summary>Raport na żądanie już czeka na wysyłkę – drugi nie powstaje.</summary>
    AlreadyQueued,

    /// <summary>Poprzedni raport na żądanie był przed chwilą – kolejny dopiero po <see cref="Cloud.CloudPanel.RequestCooldown"/>.</summary>
    TooSoon,

    /// <summary>Polecenia nie udało się zostawić w chmurze (brak sieci, chmura niedostępna).</summary>
    Unavailable
}

/// <param name="AvailableAt">Przy <see cref="ReportRequestStatus.TooSoon"/>: od kiedy można zlecić raport.</param>
public sealed record ReportRequestResult(ReportRequestStatus Status, DateTimeOffset? AvailableAt = null);
