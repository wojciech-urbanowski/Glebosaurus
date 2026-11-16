using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Glebosaurus.Cloud.Admin;
using Npgsql;
using NpgsqlTypes;

[assembly: AssemblyFixture(typeof(Glebosaurus.Cloud.Tests.CloudDatabase))]
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

namespace Glebosaurus.Cloud.Tests;

/// <summary>
/// Próbna baza Supabase dla całego przebiegu testów: zabezpieczenia przed prawdziwym projektem, wyczyszczenie, migracje
/// od zera i znacznik bazy próbnej (włącza sterowanie zegarem przez <c>glebosaurus.test_now</c>).
/// </summary>
public sealed class CloudDatabase : IAsyncLifetime
{
    public const string TestFileName = "cloud-admin.test.json";
    public const string ProductionFileName = "cloud-admin.json";

    private static long timeSlot;

    public string? SkipReason { get; private set; }
    public CloudAdminSettings Settings { get; private set; } = null!;
    public NpgsqlDataSource DataSource { get; private set; } = null!;
    public string RepositoryRoot { get; } = FindRepositoryRoot();

    /// <summary>Testy na chmurze uruchamia się świadomie (sieć, ok. minuty, czyszczenie próbnej bazy): zmienna środowiska
    /// <c>GLEBOSAURUS_CLOUD_TESTS=1</c> i plik <c>local/cloud-admin.test.json</c>.</summary>
    public const string OptInVariable = "GLEBOSAURUS_CLOUD_TESTS";

    public async ValueTask InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable(OptInVariable) != "1")
        {
            SkipReason = $"Testy na próbnym projekcie Supabase uruchamia {OptInVariable}=1.";
            return;
        }
        var testFile = Path.Combine(RepositoryRoot, "local", TestFileName);
        if (!File.Exists(testFile))
        {
            SkipReason = $"Brak local/{TestFileName} – testy na próbnym projekcie Supabase pominięte.";
            return;
        }
        Settings = CloudAdminSettings.Load(testFile);

        var productionFile = Path.Combine(RepositoryRoot, "local", ProductionFileName);
        if (File.Exists(productionFile) && CloudAdminSettings.Load(productionFile).ProjectRef == Settings.ProjectRef)
            throw new InvalidOperationException($"local/{TestFileName} wskazuje prawdziwy projekt (to samo id co w {ProductionFileName}) – testy odmawiają.");

        var certificate = Path.Combine(RepositoryRoot, "supabase", "prod-ca-2021.crt");
        DataSource = Settings.CreateDataSource(File.Exists(certificate) ? certificate : null, "glebosaurus-tests");

        await GuardAndResetAsync();
        await MigrationRunner.ApplyAsync(DataSource, Migration.Embedded(), null, CancellationToken.None);
        await using var mark = DataSource.CreateCommand("insert into glebosaurus.environment (kind) values ('test')");
        await mark.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (DataSource is not null) await DataSource.DisposeAsync();
    }

    public void SkipIfUnavailable() => Assert.SkipWhen(SkipReason is not null, SkipReason ?? "");

    /// <summary>Nowa, nieużywana chwila w przyszłości (co dobę dalej) – każdy test ma własny odcinek czasu, więc wspólna
    /// blokada nadawcy z poprzedniego testu jest już dawno wygasła.</summary>
    public static DateTimeOffset NextTime()
    {
        var now = DateTimeOffset.UtcNow;
        var start = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, TimeSpan.Zero);
        return start.AddDays(Interlocked.Increment(ref timeSlot));
    }

    private async Task GuardAndResetAsync()
    {
        await using var connection = await DataSource.OpenConnectionAsync();
        var hasSchema = await ScalarAsync<bool>(connection, "select exists (select 1 from pg_namespace where nspname = 'glebosaurus')");
        if (hasSchema)
        {
            var hasMarker = await ScalarAsync<bool>(connection, "select to_regclass('glebosaurus.environment') is not null");
            var kind = hasMarker ? await ScalarAsync<string?>(connection, "select kind from glebosaurus.environment") : null;
            var otherTables = await ScalarAsync<long>(connection, """
                select count(*) from pg_tables where schemaname = 'glebosaurus' and tablename <> 'schema_migrations'
                """);
            if (kind != "test" && otherTables > 0)
                throw new InvalidOperationException(
                    "Baza ma tabele Glebosaurusa bez znacznika bazy próbnej – to może być prawdziwy projekt; testy odmawiają czyszczenia.");
        }

        await using var reset = new NpgsqlCommand("""
            drop schema if exists glebosaurus cascade;
            do $$
            declare r record;
            begin
                for r in select p.oid::regprocedure as signature from pg_proc p
                          where p.pronamespace = 'public'::regnamespace and obj_description(p.oid, 'pg_proc') = 'glebosaurus-api'
                loop
                    execute 'drop function ' || r.signature;
                end loop;
            end $$;
            delete from vault.secrets where name like 'glebosaurus:%';
            """, connection);
        await reset.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default! : (T)value;
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Wywołania
    // ------------------------------------------------------------------------------------------------------------------

    /// <summary>Wywołuje funkcję API jako agent (rola anon – jak przez Data API z kluczem publikowalnym).</summary>
    public Task<JsonElement> AgentAsync(string function, DateTimeOffset? at, params (string Name, object? Value)[] args) =>
        CallAsync("anon", $"select public.{function}({string.Join(", ", args.Select(a => $"{a.Name} => @{a.Name}"))})::text", at, args);

    /// <summary>Zapytanie jako postgres (właściciel: funkcje admin_*, podgląd tabel).</summary>
    public Task<JsonElement> AdminAsync(string sql, DateTimeOffset? at, params (string Name, object? Value)[] args) =>
        CallAsync(null, sql, at, args);

    /// <summary>Zapytanie jako dowolna rola (do testów uprawnień).</summary>
    public Task<JsonElement> AsRoleAsync(string role, string sql) => CallAsync(role, sql, null, []);

    private async Task<JsonElement> CallAsync(string? role, string sql, DateTimeOffset? at, (string Name, object? Value)[] args)
    {
        await using var connection = await DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        if (at is { } time)
        {
            await using var clock = new NpgsqlCommand("select set_config('glebosaurus.test_now', @now, true)", connection, transaction);
            clock.Parameters.AddWithValue("now", time.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            await clock.ExecuteNonQueryAsync();
        }
        if (role is not null)
        {
            await using var setRole = new NpgsqlCommand($"set local role {role}", connection, transaction);
            await setRole.ExecuteNonQueryAsync();
        }
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in args) command.Parameters.Add(Parameter(name, value));
        var result = await command.ExecuteScalarAsync();
        await transaction.CommitAsync();
        return result switch
        {
            null or DBNull or "" => default,
            string text => JsonDocument.Parse(text).RootElement.Clone(),
            var other => JsonSerializer.SerializeToElement(other)
        };
    }

    private static NpgsqlParameter Parameter(string name, object? value) => value switch
    {
        null => new NpgsqlParameter(name, DBNull.Value),
        JsonNode node => new NpgsqlParameter(name, NpgsqlDbType.Jsonb) { Value = node.ToJsonString() },
        DateTimeOffset time => new NpgsqlParameter(name, NpgsqlDbType.TimestampTz) { Value = time.ToUniversalTime() },
        string[] texts => new NpgsqlParameter(name, NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = texts },
        TimeSpan span => new NpgsqlParameter(name, NpgsqlDbType.Interval) { Value = span },
        _ => new NpgsqlParameter(name, value)
    };

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Glebosaurus.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Nie znaleziono katalogu repozytorium (Glebosaurus.slnx).");
    }
}
