using System.Globalization;
using Glebosaurus.Cloud.Admin;
using Glebosaurus.Core.Config;
using Glebosaurus.Core.Hosting;
using Npgsql;

namespace Glebosaurus.Agent;

/// <summary>
/// Narzędzia właściciela chmury (ARCHITEKTURA.md, zasada 15): migracje schematu, import konfiguracji trybu samodzielnego i
/// agenci z kodami rejestracji. Łączą się z bazą hasłem bazy z pliku administratora (pooler sesji, VerifyFull). Bez
/// --confirm każde polecenie tylko pokazuje, co zrobi.
/// </summary>
internal static class CloudAdminCommands
{
    public static async Task<int> RunAsync(CliOptions options, CancellationToken cancellationToken) => options.Subcommand switch
    {
        "migrate" => await MigrateAsync(options, cancellationToken),
        "import" => await ImportAsync(options, cancellationToken),
        "agent" => await AgentAsync(options, cancellationToken),
        _ => throw new ArgumentException("Podaj polecenie: glebosaurus cloud migrate|import|agent --admin PLIK (zob. --help).")
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

    private static async Task<int> AgentAsync(CliOptions options, CancellationToken cancellationToken)
    {
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
        var role = options.SenderPriority is { } priority ? $"zbiera i może wysyłać maile (priorytet {priority})" : "tylko zbiera";
        Console.WriteLine(current is null
            ? $"Nowy agent „{name}”: {role}."
            : $"Agent „{name}” już jest – rola: {role}; nowy kod zastąpi jego token przy rejestracji.");
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

    private static async Task<bool> SchemaReadyAsync(NpgsqlDataSource data, CancellationToken cancellationToken)
    {
        var status = await MigrationRunner.StatusAsync(data, Migration.Embedded(), cancellationToken);
        if (status.UpToDate) return true;
        Console.Error.WriteLine("Schemat w chmurze nie jest aktualny – najpierw: glebosaurus cloud migrate --admin PLIK --confirm.");
        return false;
    }
}
