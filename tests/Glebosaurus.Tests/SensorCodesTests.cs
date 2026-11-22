using System.Text.Json;
using Jint;

namespace Glebosaurus.Tests;

/// <summary>
/// Kody czujników na kafelkach pulpitu skróconego (<c>web/sensor-codes.js</c>): trzy znaki z nazwy, wielka litera na
/// początku słowa, różne bez względu na wielkość liter i polskie znaki, stałe przy dodawaniu innych czujników. Plik
/// strony działa tu w silniku JavaScript Jint – testujemy dokładnie ten kod, który dostaje przeglądarka.
/// </summary>
public sealed class SensorCodesTests
{
    private static readonly string Script = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "web", "sensor-codes.js"));

    private static readonly (string Id, string Name)[] AuthorSensors =
    [
        ("alpinarium", "Alpinarium"), ("cisy", "Cisy"), ("cyprysek", "Cyprysek"), ("hortensje_niskie", "Hortensje niskie"),
        ("laurowisnia_szafka", "Laurowiśnia szafka"), ("laurowisnia_wejscie", "Laurowiśnia wejście"), ("trawa", "Trawa"),
        ("wiszacy_szafka", "Wiszący szafka"), ("wiszacy_wejscie", "Wiszący wejście"), ("ziola", "Zioła")
    ];

    [Fact]
    public void AuthorSensors_GetTheirPrimaryCodes()
    {
        var codes = Codes(AuthorSensors);

        Assert.Equal(["Alp", "Cis", "Cyp", "HNi", "LSz", "LWe", "Tra", "WSz", "WWe", "Zio"], AuthorSensors.Select(s => codes[s.Id]));
    }

    [Theory]
    [InlineData("Alpinarium", "Alp")]                  // jedno słowo – trzy pierwsze litery
    [InlineData("BAZYLIA", "Baz")]                     // wielka tylko pierwsza litera słowa
    [InlineData("Laurowiśnia wejście", "LWe")]         // dwa słowa – litera pierwszego i dwie drugiego
    [InlineData("Pelargonie na balkonie", "PNB")]      // więcej słów – inicjały
    [InlineData("Pomidor 2", "Po2")]                   // liczba na końcu zostaje na końcu kodu
    [InlineData("Pomidor 12", "P12")]
    [InlineData("Laurowiśnia szafka 2", "LS2")]
    [InlineData("Doniczka 123", "D12")]                // trzy cyfry to już zwykłe słowo
    [InlineData("Grządka A", "GrA")]                   // krótkie słowo – brakujący znak z początku nazwy
    [InlineData("Ul", "Ul")]                           // nazwa krótsza niż kod
    [InlineData("Żurawka", "Żur")]                     // polskie litery zostają
    [InlineData("Łąka", "Łąk")]
    [InlineData("Mięta (balkon)", "MBa")]              // znaki inne niż litery i cyfry dzielą słowa
    public void PrimaryCode_FollowsTheNameRules(string name, string code)
    {
        Assert.Equal(code, Codes(("czujnik", name))["czujnik"]);
    }

    [Fact]
    public void NameWithoutLettersOrDigits_TakesCodeFromId()
    {
        Assert.Equal("Do3", Codes(("doniczka_3", "★ ★"))["doniczka_3"]);
    }

    [Fact]
    public void SamePrimaryCode_LowestIdKeepsIt_OtherTakesFirstFreeFallback()
    {
        var codes = Codes(("trawa", "Trawa"), ("trawnik", "Trawnik"));
        Assert.Equal("Tra", codes["trawa"]);
        Assert.Equal("Trw", codes["trawnik"]);

        // O kodzie decyduje identyfikator, nie kolejność na liście ani nazwa.
        var reversed = Codes(("trawa", "Trawa"), ("a_trawnik", "Trawnik"));
        Assert.Equal("Tra", reversed["a_trawnik"]);
        Assert.Equal("Trw", reversed["trawa"]);
    }

    [Fact]
    public void NewSensor_ChangesOnlyCodesThatCollideWithIt()
    {
        var before = Codes(AuthorSensors);
        var after = Codes([.. AuthorSensors, ("trawnik", "Trawnik"), ("wiszacy_szafka_2", "Wiszący szafka 2")]);

        foreach (var (id, _) in AuthorSensors)
            Assert.Equal(before[id], after[id]);
        Assert.Equal("Trw", after["trawnik"]);
        Assert.Equal("WS2", after["wiszacy_szafka_2"]);
    }

    [Fact]
    public void Codes_DifferIgnoringCaseAndDiacritics()
    {
        var codes = Codes(("slimak", "Ślimak"), ("slipy", "Slipy"), ("laurowisnia_wejscie", "Laurowiśnia wejście"), ("lwela", "Lwela"));

        Assert.Equal("Śli", codes["slimak"]);
        Assert.Equal("Slp", codes["slipy"]);
        Assert.Equal("LWe", codes["laurowisnia_wejscie"]);
        Assert.Equal("Lwl", codes["lwela"]);
    }

    [Fact]
    public void SameNames_GetDistinctCodes_EndingWithNumberWhenLettersRunOut()
    {
        var codes = Codes(("cis_1", "Cis"), ("cis_2", "Cis"), ("cis_3", "Cis"));
        Assert.Equal(["Cis", "Ci2", "Ci3"], new[] { codes["cis_1"], codes["cis_2"], codes["cis_3"] });

        var many = Codes([.. Enumerable.Range(1, 40).Select(i => ($"pomidor_{i:D2}", "Pomidor"))]);
        Assert.Equal(40, many.Values.Select(Fold).Distinct().Count());
        Assert.All(many.Values, code => Assert.StartsWith("P", code, StringComparison.Ordinal));
    }

    private static Dictionary<string, string> Codes(params (string Id, string Name)[] sensors)
    {
        var engine = new Engine();
        engine.Modules.Add("sensor-codes", Script);
        engine.SetValue("sensorCodes", engine.Modules.Import("sensor-codes").Get("sensorCodes"));
        var input = JsonSerializer.Serialize(sensors.Select(s => new { id = s.Id, name = s.Name }));
        var output = engine.Evaluate($"JSON.stringify([...sensorCodes({input})])").AsString();
        var pairs = JsonSerializer.Deserialize<string[][]>(output)!;
        Assert.Equal(sensors.Length, pairs.Length);
        return pairs.ToDictionary(p => p[0], p => p[1]);
    }

    private static string Fold(string code) =>
        new string([.. code.Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)])
            .Replace('ł', 'l').Replace('Ł', 'l').ToLowerInvariant();

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Glebosaurus.slnx")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Nie znaleziono katalogu repozytorium (Glebosaurus.slnx).");
    }
}
