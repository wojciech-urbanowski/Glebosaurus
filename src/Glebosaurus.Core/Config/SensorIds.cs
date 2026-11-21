using System.Text.RegularExpressions;

namespace Glebosaurus.Core.Config;

/// <summary>
/// Identyfikator czujnika w chmurze – klucz w tabeli, w poleceniach i w kluczach maili, stąd wąski zapis. Tę samą regułę
/// sprawdza baza (<c>owner_sensor_add</c>, <c>owner_pair_start</c>); tu – zanim <c>pair</c> zacznie parować.
/// </summary>
public static partial class SensorIds
{
    public const string Rule = "małe litery, cyfry, „_” i „-” (do 40 znaków, na początku litera albo cyfra)";

    public static bool IsValid(string id) => Pattern().IsMatch(id);

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,39}$")]
    private static partial Regex Pattern();
}
