namespace Glebosaurus.Core.Monitoring;

/// <summary>Co się stało z prośbą o raport mailowy poza harmonogramem (panel WWW).</summary>
public enum ReportRequestStatus
{
    /// <summary>Zlecony – silnik wyśle go w najbliższym cyklu (pętla budzi się od razu; trwający odczyt czujników kończy się najpierw).</summary>
    Queued,

    /// <summary>Raport na żądanie już czeka na wysyłkę – drugi nie powstaje.</summary>
    AlreadyQueued,

    /// <summary>Poprzedni raport na żądanie był przed chwilą – kolejny dopiero po <see cref="MonitorEngine.ReportOnRequestCooldown"/>.</summary>
    TooSoon,

    /// <summary>Poczta nie jest skonfigurowana – raportu nie ma czym wysłać.</summary>
    MailNotConfigured
}

/// <param name="AvailableAt">Przy <see cref="ReportRequestStatus.TooSoon"/>: od kiedy można zlecić raport.</param>
public sealed record ReportRequestResult(ReportRequestStatus Status, DateTimeOffset? AvailableAt = null);
