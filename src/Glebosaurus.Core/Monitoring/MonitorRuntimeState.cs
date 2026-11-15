namespace Glebosaurus.Core.Monitoring;

/// <summary>
/// Migawka stanu pracującego silnika – to, czego nie ma w bazie (próby, ponowienia, sygnał, stan poczty). Silnik
/// publikuje nową, niezmienną migawkę po każdym kroku, a panel WWW ją tylko czyta, więc nie potrzeba blokad.
/// </summary>
public sealed record MonitorRuntimeState(
    DateTimeOffset? StartedAt,
    DateTimeOffset? LastCycleAt,
    string? LastCycleError,
    DateTimeOffset? LastCycleErrorAt,
    IReadOnlyList<string> ReadingNow,
    DateTimeOffset? ReadingSince,
    IReadOnlyDictionary<string, SensorRuntimeState> Sensors,
    MailRuntimeState Mail)
{
    public static readonly MonitorRuntimeState NotStarted =
        new(null, null, null, null, [], null, new Dictionary<string, SensorRuntimeState>(), MailRuntimeState.Unused);

    /// <summary>Czujniki zlecone do odczytu na żądanie, które czekają na najbliższy cykl.</summary>
    public IReadOnlyList<string> RequestedReads { get; init; } = [];

    /// <summary>Raport mailowy na żądanie: czy czeka na wysyłkę i jak skończyła się ostatnia próba.</summary>
    public ReportOnRequestState ReportOnRequest { get; init; } = ReportOnRequestState.None;
}

/// <param name="RequestedAt">Kiedy zlecono raport, który czeka na wysyłkę; null = nic nie czeka.</param>
/// <param name="LastAttemptAt">Ostatnia próba wysłania raportu na żądanie (od startu monitora).</param>
/// <param name="LastError">Błąd ostatniej próby; null = raport wysłany.</param>
public sealed record ReportOnRequestState(DateTimeOffset? RequestedAt, DateTimeOffset? LastAttemptAt, string? LastError)
{
    public static readonly ReportOnRequestState None = new(null, null, null);
}

/// <param name="LastAttemptAt">Kiedy silnik ostatnio próbował odczytu (udanego lub nie); null = od startu nie próbował.</param>
/// <param name="LastResult">Wynik ostatniej próby (<see cref="SensorReadFailure.None"/> = udany odczyt).</param>
/// <param name="Failures">Nieudane próby z rzędu (0 po udanym odczycie).</param>
/// <param name="RetryAt">Najwcześniejsza kolejna próba po porażce (odstęp wykładniczy); null = bez wstrzymania.</param>
/// <param name="LastSignalDbm">Ostatnio usłyszany poziom sygnału (od startu monitora).</param>
public sealed record SensorRuntimeState(
    DateTimeOffset? LastAttemptAt,
    SensorReadFailure? LastResult,
    string? LastError,
    int Failures,
    DateTimeOffset? RetryAt,
    int? LastSignalDbm,
    DateTimeOffset? LastSignalAt)
{
    public static readonly SensorRuntimeState Unknown = new(null, null, null, 0, null, null, null);
}

/// <param name="LastSentAt">Ostatnia udana wysyłka dowolnego maila (alert, raport, dziennik).</param>
/// <param name="RetryAt">Do tej chwili silnik wstrzymuje wysyłkę alertów i raportów po błędzie.</param>
public sealed record MailRuntimeState(DateTimeOffset? LastSentAt, string? LastError, DateTimeOffset? LastErrorAt, DateTimeOffset? RetryAt)
{
    public static readonly MailRuntimeState Unused = new(null, null, null, null);
}
