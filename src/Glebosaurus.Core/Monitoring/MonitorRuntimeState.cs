namespace Glebosaurus.Core.Monitoring;

/// <summary>
/// Migawka stanu zbieracza – to, czego nie ma w bazie (próby, ponowienia, sygnał). Zbieracz publikuje nową, niezmienną
/// migawkę po każdym kroku, a panel WWW i ekran telefonu ją tylko czytają, więc nie potrzeba blokad.
/// </summary>
public sealed record MonitorRuntimeState(
    DateTimeOffset? StartedAt,
    DateTimeOffset? LastCycleAt,
    string? LastCycleError,
    DateTimeOffset? LastCycleErrorAt,
    IReadOnlyList<string> ReadingNow,
    DateTimeOffset? ReadingSince,
    IReadOnlyDictionary<string, SensorRuntimeState> Sensors)
{
    public static readonly MonitorRuntimeState NotStarted =
        new(null, null, null, null, [], null, new Dictionary<string, SensorRuntimeState>());

    /// <summary>Czujniki zlecone do odczytu na żądanie, na których nowy odczyt panel jeszcze czeka.</summary>
    public IReadOnlyList<string> RequestedReads { get; init; } = [];
}

/// <param name="LastAttemptAt">Kiedy zbieracz ostatnio próbował odczytu (udanego lub nie); null = od startu nie próbował.</param>
/// <param name="LastResult">Wynik ostatniej próby (<see cref="SensorReadFailure.None"/> = udany odczyt).</param>
/// <param name="Failures">Nieudane próby z rzędu (0 po udanym odczycie).</param>
/// <param name="RetryAt">Najwcześniejsza kolejna próba po porażce (odstęp wykładniczy); null = bez wstrzymania.</param>
/// <param name="LastSignalDbm">Ostatnio usłyszany poziom sygnału (od startu agenta).</param>
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

/// <summary>Stan poczty nadawcy (panel WWW).</summary>
/// <param name="LastSentAt">Ostatnia udana wysyłka dowolnego maila (alert, raport, dziennik).</param>
/// <param name="RetryAt">Do tej chwili nadawca wstrzymuje wysyłkę po błędzie.</param>
public sealed record MailRuntimeState(DateTimeOffset? LastSentAt, string? LastError, DateTimeOffset? LastErrorAt, DateTimeOffset? RetryAt)
{
    public static readonly MailRuntimeState Unused = new(null, null, null, null);
}
