using System.Text.RegularExpressions;

namespace Glebosaurus.Tests;

/// <summary>
/// Zadanie Harmonogramu z <c>deploy/install-windows.ps1</c> (ARCHITEKTURA.md, zasada 7) – ustawienia, których nie sprawdzi
/// parser skryptu w CI. Skrypt rejestruje zadanie przy każdej instalacji i aktualizacji, więc zmiana trafia też do
/// działających usług z najbliższą samoaktualizacją.
/// </summary>
public sealed partial class InstallScriptTests
{
    private static readonly string Script = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "deploy", "install-windows.ps1"));

    [Fact]
    public void TaskRunsAtLowPriority()
    {
        // Decyzja użytkownika 2026-11-26: usługa w tle z niskim priorytetem (7 = CPU BelowNormal, pamięć 2, niski I/O –
        // dziedziczy je agent), choć przy zajętym komputerze agent stoi wtedy po 1–4 s (Kestrel: HeartbeatSlow, w dzienniku
        // jako informacja – KestrelHeartbeat). Wpisany wprost, żeby nikt go przypadkiem nie podniósł.
        var settings = Assert.Single(TaskSettings().Matches(Script)).Value;

        Assert.Matches(@"-Priority\s+7\b", settings);
    }

    [Fact]
    public void TaskIsRegisteredWithThoseSettings() =>
        Assert.Matches(@"Register-ScheduledTask\b[^\r\n]*-Settings\s+\$settings\b", Script);

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Glebosaurus.slnx")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Nie znaleziono katalogu repozytorium (Glebosaurus.slnx).");
    }

    // Całe polecenie razem z wierszami kontynuowanymi odwrotnym apostrofem.
    [GeneratedRegex(@"New-ScheduledTaskSettingsSet(?:[^\r\n]*`\r?\n)*[^\r\n]*")]
    private static partial Regex TaskSettings();
}
