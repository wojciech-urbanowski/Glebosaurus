using System.Globalization;

namespace Glebosaurus.Core.Monitoring;

/// <summary>
/// Moc sygnału (RSSI, dBm) zebrana z reklam jednego czujnika. Platformy podają „brak wartości” jako −127 (Windows) albo
/// 127; takie próbki nie wchodzą do statystyki, tylko do <see cref="Unavailable"/>, żeby nie udawać, że sygnał jest zerowy.
/// </summary>
public sealed class SignalStats
{
    private long sum;

    /// <summary>Ile próbek miało ważną wartość.</summary>
    public int Count { get; private set; }

    /// <summary>Ile próbek platforma oznaczyła jako „brak wartości”.</summary>
    public int Unavailable { get; private set; }

    public int? Min { get; private set; }
    public int? Max { get; private set; }

    /// <summary>Średnia z ważnych próbek (zaokrąglona do pełnych dBm); null, gdy żadna nie miała wartości.</summary>
    public int? Average => Count == 0 ? null : (int)Math.Round((double)sum / Count, MidpointRounding.AwayFromZero);

    public static bool IsValid(int dbm) => dbm is > -127 and < 127;

    public void Add(int dbm)
    {
        if (!IsValid(dbm))
        {
            Unavailable++;
            return;
        }
        Count++;
        sum += dbm;
        Min = Min is { } min ? Math.Min(min, dbm) : dbm;
        Max = Max is { } max ? Math.Max(max, dbm) : dbm;
    }

    /// <summary>„-81 dBm” (zawsze zwykły minus, niezależnie od kultury – dziennik ma być łatwy do przeszukania).</summary>
    public static string Format(int dbm) => string.Create(CultureInfo.InvariantCulture, $"{dbm} dBm");

    /// <summary>Do dziennika: wartość albo „nieznany”.</summary>
    public static string Format(int? dbm) => dbm is { } value ? Format(value) : "nieznany";

    /// <summary>Do <c>scan</c>: średnia, zakres i liczba próbek, np. „śr. -81 dBm (-83…-78, 29 próbek)”.</summary>
    public string Describe()
    {
        var missing = Unavailable > 0 ? string.Create(CultureInfo.InvariantCulture, $", {Unavailable} bez wartości") : "";
        return Count == 0
            ? string.Create(CultureInfo.InvariantCulture, $"brak wartości ({Unavailable} próbek bez RSSI)")
            : string.Create(CultureInfo.InvariantCulture, $"śr. {Format(Average)} ({Min}…{Max}, {Count} próbek{missing})");
    }
}
