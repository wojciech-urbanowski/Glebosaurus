namespace Glebosaurus.Core.Alerts;

/// <summary>
/// Okres ochronny alertu „brak odczytów” po starcie monitora i po przerwie w jego pracy (np. uśpienie komputera).
/// Cisza liczy się od ostatniego odczytu w bazie, także za czas, gdy monitor nie działał – bez tego okresu start po
/// dłuższym wyłączeniu komputera kończył się po ok. 2 min alertami dla czujników, których monitor nie zdążył jeszcze
/// odczytać (bestia5, 2026-11-17). Liczenie ciszy dopiero od startu odpada: przy komputerze włączanym na kilka godzin
/// dziennie martwy czujnik nie dałby alertu nigdy. W okresie ochronnym nowy alert o ciszy nie powstaje, a otwarty trwa
/// (zamyka go dopiero udany odczyt) – patrz <see cref="AlertEvaluator.Evaluate"/>.
/// </summary>
public sealed class SilenceGrace(TimeSpan grace, TimeSpan pauseAfter)
{
    /// <summary>Tyle monitor ma na odczyty po starcie albo przerwie (z odstępami ponowień 2, 5, 10 min to 5 prób).</summary>
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Dłuższa przerwa między kolejnymi przeliczeniami alertów to przestój (uśpienie, brak blokady nadawcy); zwykle
    /// przeliczenia idą co 5–6 min (<c>NotifierOptions.AlertEvery</c> plus obrót pętli). Fałszywe rozpoznanie przerwy tylko
    /// opóźnia alert o ciszy.
    /// </summary>
    public static readonly TimeSpan DefaultPauseAfter = TimeSpan.FromMinutes(10);

    private DateTimeOffset? lastSeen;

    public SilenceGrace() : this(DefaultGrace, DefaultPauseAfter) { }

    public TimeSpan Grace => grace;

    /// <summary>Koniec bieżącego okresu ochronnego (ważny po pierwszym <see cref="Observe"/>).</summary>
    public DateTimeOffset HoldUntil { get; private set; }

    /// <summary>
    /// Wołane przy każdym przeliczeniu alertów. Pierwsze wywołanie zaczyna okres ochronny po starcie; po przerwie dłuższej
    /// niż próg zaczyna się nowy, a wynikiem jest długość tej przerwy (do dziennika). Inaczej null.
    /// </summary>
    public TimeSpan? Observe(DateTimeOffset now)
    {
        TimeSpan? pause = null;
        if (lastSeen is not { } last)
        {
            HoldUntil = now + grace;
        }
        else if (now - last > pauseAfter)
        {
            pause = now - last;
            HoldUntil = now + grace;
        }
        lastSeen = now;
        return pause;
    }

    /// <summary>true, gdy nowego alertu „brak odczytów” jeszcze nie wolno otworzyć.</summary>
    public bool Holds(DateTimeOffset now) => now < HoldUntil;
}
