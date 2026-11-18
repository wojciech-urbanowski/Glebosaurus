using System.Globalization;
using Glebosaurus.Cloud.Admin;
using Glebosaurus.Core.Config;
using Glebosaurus.Core.Hosting;
using Npgsql;

namespace Glebosaurus.Agent;

/// <summary>
/// Narzędzia właściciela chmury (ARCHITEKTURA.md, zasada 15): migracje schematu, import konfiguracji trybu samodzielnego,
/// wspólne ustawienia (odstęp odczytów), agenci z kodami rejestracji i kopia zapasowa. Łączą się z bazą hasłem bazy z pliku administratora (pooler sesji,
/// VerifyFull). Bez --confirm każde polecenie zmieniające chmurę tylko pokazuje, co zrobi (kopia tylko czyta).
/// </summary>
internal static class CloudAdminCommands
{
    public static async Task<int> RunAsync(CliOptions options, CancellationToken cancellationToken) => options.Subcommand switch
    {
        "migrate" => await MigrateAsync(options, cancellationToken),
        "import" => await ImportAsync(options, cancellationToken),
        "settings" => await SettingsAsync(options, cancellationToken),
        "agent" => await AgentAsync(options, cancellationToken),
        "backup" => await BackupAsync(options, cancellationToken),
        _ => throw new ArgumentException("Podaj polecenie: glebosaurus cloud migrate|import|settings|agent|backup --admin PLIK (zob. --help).")
    };

    private static (CloudAdminSettings Settings, NpgsqlDataSource Data) Open(CliOptions options)
    {
        if (options.Admin is null)
            throw new ArgumentException("Podaj plik administratora projektu: --admin PLIK (np. local/cloud-admin.json – zawiera hasło bazy).");
        var settings = CloudAdminSettings.Load(Path.GetFullPath(options.Admin));
        Console.WriteLine($"Projekt Supabase: {settings.ProjectRef} (z {Path.GetFileName(options.Admin)}).");
        return (settings, settings.CreateDataSource("glebosaurus-cli"));
    }

    private static async Task<int> MigrateAsync(CliOptions options, CancellationToken cancellationToken)
    {
        var (_, data) = Open(options);
        await using var _ = data;
        var migrations = Migration.Embedded();
        var status = await MigrationRunner.StatusAsync(data, migrations, cancellationToken);
        foreach (var migration in status.Applied) Console.WriteLine($"  zastosowana:      {migration.Version}_{migration.Name}");
        foreach (var migration in status.Pending) Console.WriteLine($"  do zastosowania:  {migration.Version}_{migration.Name}");
        foreach (var version in status.Changed) Console.Error.WriteLine($"  ZMIENIONA:        {version} – treść inna niż zastosowana w bazie (zmiany schematu idą nową migracją)");
        foreach (var version in status.Unknown) Console.Error.WriteLine($"  NIEZNANA:         {version} – zastosowana przez nowszą wersję programu");
        if (status.Changed.Count > 0 || status.Unknown.Count > 0) return ExitCodes.Error;
        if (status.Pending.Count == 0)
        {
            Console.WriteLine("Schemat jest aktualny – nic do zrobienia.");
            return ExitCodes.Success;
        }
        if (!options.Confirm)
        {
            Console.WriteLine($"Do zastosowania: {status.Pending.Count}. Bez --confirm niczego nie zmieniam.");
            return ExitCodes.Success;
        }
        var applied = await MigrationRunner.ApplyAsync(data, migrations, line => Console.WriteLine("  " + line), cancellationToken);
        Console.WriteLine($"Zastosowano migracji: {applied.Count}.");
        return ExitCodes.Success;
    }

    private static async Task<int> ImportAsync(CliOptions options, CancellationToken cancellationToken)
    {
        var configPath = Path.GetFullPath(options.Config ?? AgentRuntime.DefaultConfigFile);
        var standalone = AgentConfigLoader.Load(configPath);
        var (settings, data) = Open(options);
        await using var _ = data;
        if (!await SchemaReadyAsync(data, cancellationToken)) return ExitCodes.Error;

        var plan = CloudImporter.Plan(standalone, settings.HealthchecksPingUrl, options.TimeZone);
        var existing = (await CloudImporter.CloudSensorIdsAsync(data, cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var settingsText = plan.Settings;
        Console.WriteLine($"Import z {configPath}:");
        Console.WriteLine($"  ustawienia: strefa {settingsText["timeZone"]}, odczyt co {settingsText["readIntervalMinutes"]} min, raporty {settingsText["reportTimes"]?.ToJsonString()}, " +
                          $"przypomnienia co {settingsText["alertReminderHours"]} h, logi {settingsText["logRetentionDays"]} dni");
        if (settingsText["gmailAddress"] is { } sender)
            Console.WriteLine($"  poczta: nadawca {sender}, odbiorcy alertów {settingsText["alertRecipients"]!.AsArray().Count}, raportów " +
                              $"{settingsText["reportRecipients"]!.AsArray().Count}, administratora {settingsText["adminRecipients"]!.AsArray().Count}");
        foreach (var sensor in plan.Sensors)
            Console.WriteLine($"  czujnik {sensor.Id} „{sensor.Name}” ({sensor.Mac}) – {(existing.Contains(sensor.Id) ? "nadpiszę" : "nowy")}, z kluczem Tuya");
        foreach (var id in existing.Where(id => plan.Sensors.All(s => s.Id != id)))
            Console.WriteLine($"  czujnik {id} – jest tylko w chmurze, zostaje bez zmian");
        Console.WriteLine($"  hasło aplikacji Gmail: {(plan.SetsGmailPassword ? "ustawię (wartość nie jest wypisywana)" : "bez zmian")}");
        Console.WriteLine($"  adres sygnału healthchecks.io: {(plan.SetsHealthchecksPingUrl ? "ustawię (wartość nie jest wypisywana)" : "bez zmian")}");
        foreach (var note in plan.Notes) Console.WriteLine($"  uwaga: {note}");
        foreach (var problem in plan.Problems) Console.Error.WriteLine($"  PROBLEM: {problem}");
        Console.WriteLine("  historia odczytów: wyśle ją sam agent w trybie chmurowym ze swojej bazy (po tym imporcie).");
        if (plan.Problems.Count > 0)
        {
            Console.Error.WriteLine("Popraw problemy – nic nie zapisano.");
            return ExitCodes.Error;
        }
        if (!options.Confirm)
        {
            Console.WriteLine("Bez --confirm niczego nie zapisuję.");
            return ExitCodes.Success;
        }
        await CloudImporter.ApplyAsync(data, plan, cancellationToken);
        Console.WriteLine($"Zaimportowano jedną transakcją: ustawienia i czujniki (liczba: {plan.Sensors.Count}). Agenci zobaczą nową konfigurację przy najbliższej synchronizacji.");
        return ExitCodes.Success;
    }

    private static async Task<int> SettingsAsync(CliOptions options, CancellationToken cancellationToken)
    {
        if (options.Confirm && options.ReadInterval is null) throw new ArgumentException("--confirm bez zmiany – podaj --read-interval MINUTY.");
        var (_, data) = Open(options);
        await using var _ = data;
        if (!await SchemaReadyAsync(data, cancellationToken)) return ExitCodes.Error;
        var current = await CloudSettingsAdmin.GetAsync(data, cancellationToken);
        Console.WriteLine($"  odczyt co: {CloudSettingsAdmin.Minutes(current.ReadIntervalMinutes)} (okno skanu {current.ScanWindowSeconds} s)");
        Console.WriteLine($"  raporty: {string.Join(", ", current.ReportTimes)} ({current.TimeZone}), przypomnienia alertów co {current.AlertReminderHours.ToString(CultureInfo.InvariantCulture)} h, " +
                          $"logi {current.LogRetentionDays} dni, wersja konfiguracji {current.ConfigVersion}");
        Console.WriteLine($"  czujniki włączone: {current.Sensors.Count}, z własnym odstępem: " +
                          (current.Sensors.Any(s => s.ReadIntervalMinutes is not null)
                              ? string.Join(", ", current.Sensors.Where(s => s.ReadIntervalMinutes is not null).Select(s => $"{s.Id} ({CloudSettingsAdmin.Minutes(s.ReadIntervalMinutes!.Value)})"))
                              : "żaden"));
        if (options.ReadInterval is not { } minutes) return ExitCodes.Success;

        if (minutes == current.ReadIntervalMinutes)
        {
            Console.WriteLine($"Odstęp odczytów już wynosi {CloudSettingsAdmin.Minutes(minutes)} – nic do zmiany.");
            return ExitCodes.Success;
        }
        Console.WriteLine($"Zmiana: odczyt co {CloudSettingsAdmin.Minutes(current.ReadIntervalMinutes)} → co {CloudSettingsAdmin.Minutes(minutes)} (reszta ustawień bez zmian).");
        foreach (var note in CloudSettingsAdmin.ReadIntervalNotes(current, minutes)) Console.WriteLine($"  uwaga: {note}");
        if (!options.Confirm)
        {
            Console.WriteLine("Bez --confirm niczego nie zapisuję.");
            return ExitCodes.Success;
        }
        await CloudSettingsAdmin.SetReadIntervalAsync(data, minutes, cancellationToken);
        Console.WriteLine($"Zapisano: odczyt co {CloudSettingsAdmin.Minutes(minutes)}. Agenci przejmą zmianę przy najbliższej synchronizacji (ok. minuty); " +
                          "kolejny odczyt czujnika wypada po nowym odstępie od jego ostatniego odczytu.");
        return ExitCodes.Success;
    }

    private static async Task<int> AgentAsync(CliOptions options, CancellationToken cancellationToken)
    {
        if (options.Revoke && options.Name is null) throw new ArgumentException("--revoke wymaga --name NAZWA (którego agenta odciąć).");
        if (options.Revoke && options.SenderPriority is not null) throw new ArgumentException("--revoke nie zmienia roli – bez --sender-priority.");
        var (_, data) = Open(options);
        await using var _ = data;
        if (!await SchemaReadyAsync(data, cancellationToken)) return ExitCodes.Error;
        var agents = await CloudAgents.ListAsync(data, cancellationToken);

        if (options.Name is not { } name)
        {
            if (agents.Count == 0) Console.WriteLine("W chmurze nie ma jeszcze agentów.");
            foreach (var agent in agents)
                Console.WriteLine($"  {agent.Name,-20} {(agent.CanSendMail ? $"nadawca (priorytet {agent.SenderPriority})" : "zbieracz"),-24} " +
                                  $"{(agent.Enrolled ? "zarejestrowany" : "czeka na enroll"),-16} {(agent.Enabled ? "" : "WYŁĄCZONY ")}" +
                                  $"ostatnio: {(agent.LastSeenAt is { } seen ? seen.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "–")}");
            return ExitCodes.Success;
        }

        var current = agents.FirstOrDefault(a => a.Name == name);
        if (options.Revoke) return await RevokeAsync(data, name, current, options.Confirm, cancellationToken);
        var role = options.SenderPriority is { } priority ? $"zbiera i może wysyłać maile (priorytet {priority})" : "tylko zbiera";
        if (current is null) Console.WriteLine($"Nowy agent „{name}”: {role}.");
        else if (current.Enrolled)
            Console.WriteLine($"Agent „{name}” już jest – nowa rola: {role}. Działający agent przejmie ją przy najbliższej synchronizacji " +
                              "(ok. minuty), bez ponownej rejestracji; kod niżej przyda się tylko przy przenosinach na inne urządzenie " +
                              "(zastąpi obecny token).");
        else Console.WriteLine($"Agent „{name}” już jest (czeka na enroll) – rola: {role}.");
        if (!options.Confirm)
        {
            Console.WriteLine("Bez --confirm niczego nie zapisuję.");
            return ExitCodes.Success;
        }
        var code = await CloudAgents.CreateWithCodeAsync(data, name, options.SenderPriority, cancellationToken);
        Console.WriteLine($"Kod rejestracji (jednorazowy, ważny 24 h): {code}");
        Console.WriteLine($"Na urządzeniu agenta: glebosaurus enroll --code {code}   (albo bez --code – zapyta o kod)");
        return ExitCodes.Success;
    }

    private static async Task<int> RevokeAsync(NpgsqlDataSource data, string name, CloudAgentInfo? current, bool confirm,
        CancellationToken cancellationToken)
    {
        if (current is null)
        {
            Console.Error.WriteLine($"W chmurze nie ma agenta „{name}” – lista: glebosaurus cloud agent --admin PLIK.");
            return ExitCodes.Error;
        }
        Console.WriteLine(current.Enrolled
            ? $"Odetnę agenta „{name}”: token przestanie działać od razu (agent zobaczy odmowę przy najbliższej synchronizacji), " +
              "a jego niewykorzystane kody rejestracji przepadną."
            : $"Agent „{name}” nie ma ważnego tokenu – skasuję tylko jego niewykorzystane kody rejestracji.");
        if (current.Enrolled)
            Console.WriteLine("  Jego czujniki przejmą inni agenci po wygaśnięciu dzierżaw (najwyżej ok. 32 min), blokadę nadawcy – po ok. 7 min.");
        if (!confirm)
        {
            Console.WriteLine("Bez --confirm niczego nie zmieniam.");
            return ExitCodes.Success;
        }
        var result = await CloudAgents.RevokeAsync(data, name, cancellationToken);
        Console.WriteLine($"Agent „{name}” odcięty: token {(result.HadToken ? "unieważniony" : "– nie było")}, skasowane kody rejestracji: {result.CancelledCodes}.");
        Console.WriteLine($"Z powrotem: glebosaurus cloud agent --admin PLIK --name {name} [--sender-priority N] --confirm, potem enroll na urządzeniu i restart agenta.");
        return ExitCodes.Success;
    }

    private static async Task<int> BackupAsync(CliOptions options, CancellationToken cancellationToken)
    {
        var (settings, data) = Open(options);
        await using var _ = data;
        var status = await MigrationRunner.StatusAsync(data, Migration.Embedded(), cancellationToken);
        if (status.Applied.Count == 0)
        {
            Console.Error.WriteLine("W chmurze nie ma schematu Glebosaurusa – nie ma czego kopiować.");
            return ExitCodes.Error;
        }
        var directory = Path.GetFullPath(options.Output ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.Admin!))!, "backups"));
        var result = await CloudBackup.CreateAsync(data, settings.ProjectRef, directory, TimeProvider.System, cancellationToken);
        var removed = CloudBackup.Prune(directory, settings.ProjectRef, options.Keep ?? 14);
        Console.WriteLine($"Kopia: {result.Path} ({result.SizeBytes / 1024} KB).");
        Console.WriteLine($"  tabele: {string.Join(", ", result.Tables.Select(t => $"{t.Key} {t.Value}"))}");
        Console.WriteLine($"  sekrety z Vault (odszyfrowane, wartości nie są wypisywane): {result.Secrets}");
        Console.WriteLine($"  pominięte: {string.Join(", ", CloudBackup.SkippedTables)} (dzienniki agentów)");
        if (removed.Count > 0) Console.WriteLine($"  usunięte starsze kopie: {removed.Count}");
        Console.WriteLine("Plik zawiera klucze Tuya i hasło Gmaila – trzymaj go jak cloud-admin.json.");
        return ExitCodes.Success;
    }

    private static async Task<bool> SchemaReadyAsync(NpgsqlDataSource data, CancellationToken cancellationToken)
    {
        var status = await MigrationRunner.StatusAsync(data, Migration.Embedded(), cancellationToken);
        if (status.UpToDate) return true;
        Console.Error.WriteLine("Schemat w chmurze nie jest aktualny – najpierw: glebosaurus cloud migrate --admin PLIK --confirm.");
        return false;
    }
}
