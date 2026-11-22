using System.Text.Json;
using Jint;

namespace Glebosaurus.Tests;

/// <summary>
/// Kolejność kafelków pulpitu skróconego (<c>web/tile-order.js</c>, plik strony w silniku Jint): od najsuchszej gleby –
/// czujniki do podlania na początku; bez odczytu za nimi, wyłączone na końcu, przy równej wilgotności według nazwy. Tę
/// samą kolejność ma zestawienie w raporcie (<c>MailTemplatesTests</c>).
/// </summary>
public sealed class TileOrderTests
{
    private static readonly string Script = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "web", "tile-order.js"));

    [Fact]
    public void DriestFirst_ThenWithoutReading_DisabledLast_TiesByName()
    {
        var order = Order(
            ("Zioła", 92, true), ("Trawa", 21, true), ("Wyłączony", 5, false), ("Nowy", null, true),
            ("Cisy", 64, true), ("Alpinarium", 64, true), ("Laurowiśnia szafka", 49, true));

        Assert.Equal(["Trawa", "Laurowiśnia szafka", "Alpinarium", "Cisy", "Zioła", "Nowy", "Wyłączony"], order);
    }

    private static string[] Order(params (string Name, int? Moisture, bool Enabled)[] sensors)
    {
        var engine = new Engine();
        engine.Modules.Add("tile-order", Script);
        engine.SetValue("tileOrder", engine.Modules.Import("tile-order").Get("tileOrder"));
        var input = JsonSerializer.Serialize(sensors.Select(s => new
        {
            name = s.Name, enabled = s.Enabled, last = s.Moisture is null ? null : new { moisture = s.Moisture }
        }));
        var output = engine.Evaluate($"JSON.stringify(tileOrder({input}).map(s => s.name))").AsString();
        return JsonSerializer.Deserialize<string[]>(output)!;
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Glebosaurus.slnx")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Nie znaleziono katalogu repozytorium (Glebosaurus.slnx).");
    }
}
