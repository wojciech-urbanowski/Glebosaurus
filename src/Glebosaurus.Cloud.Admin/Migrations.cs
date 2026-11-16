using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Glebosaurus.Cloud.Admin;

/// <summary>Migracja schematu z <c>supabase/migrations/&lt;wersja&gt;_&lt;nazwa&gt;.sql</c> (nazwy jak w Supabase CLI).</summary>
public sealed record Migration(string Version, string Name, string Sql)
{
    /// <summary>SHA-256 treści (bez znaczenia końców linii) – zastosowanej migracji nie wolno później zmieniać.</summary>
    public string Checksum => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Sql.ReplaceLineEndings("\n"))));

    /// <summary>Migracje wbudowane w bibliotekę, od najstarszej.</summary>
    public static IReadOnlyList<Migration> Embedded()
    {
        const string prefix = "Glebosaurus.Migrations.";
        var assembly = typeof(Migration).Assembly;
        var result = new List<Migration>();
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var fileName = resource[prefix.Length..];
            var underscore = fileName.IndexOf('_');
            if (underscore != 14 || !fileName.EndsWith(".sql", StringComparison.Ordinal) || !fileName[..14].All(char.IsAsciiDigit))
                throw new InvalidDataException($"Migracja {fileName}: nazwa musi mieć postać RRRRMMDDGGMMSS_nazwa.sql.");
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            result.Add(new(fileName[..14], fileName[15..^4], reader.ReadToEnd()));
        }
        return [.. result.OrderBy(m => m.Version, StringComparer.Ordinal)];
    }
}

/// <summary>
/// Stosuje brakujące migracje po kolei, każdą w osobnej transakcji, pod blokadą doradczą (dwa uruchomienia naraz czekają
/// na siebie). Zastosowane migracje są w <c>glebosaurus.schema_migrations</c> z sumą kontrolną; zmieniona treść już
/// zastosowanej migracji zatrzymuje wszystko – zmiany schematu idą zawsze nową migracją.
/// </summary>
/// <summary>Stan migracji w bazie względem wbudowanych w program.</summary>
/// <param name="Changed">Zastosowane, ale o innej treści niż w programie – stop, zmiany schematu idą nową migracją.</param>
/// <param name="Unknown">Zastosowane w bazie, a nieznane programowi (nowsza wersja programu je zastosowała).</param>
public sealed record MigrationStatus(IReadOnlyList<Migration> Applied, IReadOnlyList<Migration> Pending, IReadOnlyList<string> Changed,
    IReadOnlyList<string> Unknown)
{
    public bool UpToDate => Pending.Count == 0 && Changed.Count == 0 && Unknown.Count == 0;
}

public static class MigrationRunner
{
    private const long AdvisoryLockKey = 0x676C6562_6F736175; // „glebosau”

    /// <summary>Co zastosowano, co czeka – bez żadnej zmiany w bazie (także gdy schematu jeszcze nie ma).</summary>
    public static async Task<MigrationStatus> StatusAsync(NpgsqlDataSource dataSource, IReadOnlyList<Migration> migrations,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var applied = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var exists = new NpgsqlCommand("select to_regclass('glebosaurus.schema_migrations') is not null", connection))
        {
            if ((bool)(await exists.ExecuteScalarAsync(cancellationToken))!)
            {
                await using var command = new NpgsqlCommand("select version, checksum from glebosaurus.schema_migrations", connection);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) applied[reader.GetString(0)] = reader.GetString(1);
            }
        }
        var known = migrations.Select(m => m.Version).ToHashSet(StringComparer.Ordinal);
        return new(
            [.. migrations.Where(m => applied.TryGetValue(m.Version, out var checksum) && checksum == m.Checksum)],
            [.. migrations.Where(m => !applied.ContainsKey(m.Version))],
            [.. migrations.Where(m => applied.TryGetValue(m.Version, out var checksum) && checksum != m.Checksum).Select(m => m.Version)],
            [.. applied.Keys.Where(v => !known.Contains(v)).Order(StringComparer.Ordinal)]);
    }

    public static async Task<IReadOnlyList<Migration>> ApplyAsync(NpgsqlDataSource dataSource, IReadOnlyList<Migration> migrations,
        Action<string>? log, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await ExecuteAsync(connection, "select pg_advisory_lock(@key)", cancellationToken, ("key", AdvisoryLockKey));
        try
        {
            await ExecuteAsync(connection, """
                create schema if not exists glebosaurus;
                create table if not exists glebosaurus.schema_migrations (
                    version    text primary key,
                    name       text not null,
                    checksum   text not null,
                    applied_at timestamptz not null default now()
                );
                alter table glebosaurus.schema_migrations enable row level security;
                revoke all on glebosaurus.schema_migrations from public, anon, authenticated, service_role;
                """, cancellationToken);

            var applied = new Dictionary<string, string>(StringComparer.Ordinal);
            await using (var command = new NpgsqlCommand("select version, checksum from glebosaurus.schema_migrations", connection))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                while (await reader.ReadAsync(cancellationToken))
                    applied[reader.GetString(0)] = reader.GetString(1);

            var known = migrations.Select(m => m.Version).ToHashSet(StringComparer.Ordinal);
            if (applied.Keys.FirstOrDefault(v => !known.Contains(v)) is { } unknown)
                throw new InvalidOperationException($"Baza ma migrację {unknown}, której ten program nie zna – jest nowsza wersja programu?");

            var done = new List<Migration>();
            foreach (var migration in migrations)
            {
                if (applied.TryGetValue(migration.Version, out var checksum))
                {
                    if (checksum != migration.Checksum)
                        throw new InvalidOperationException(
                            $"Migracja {migration.Version}_{migration.Name} różni się od zastosowanej w bazie – zmiany schematu idą nową migracją.");
                    continue;
                }

                log?.Invoke($"Migracja {migration.Version}_{migration.Name}…");
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                await using (var command = new NpgsqlCommand(migration.Sql, connection, transaction))
                {
                    command.CommandTimeout = 300;
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }
                await using (var record = new NpgsqlCommand(
                    "insert into glebosaurus.schema_migrations (version, name, checksum) values (@v, @n, @c)", connection, transaction))
                {
                    record.Parameters.AddWithValue("v", migration.Version);
                    record.Parameters.AddWithValue("n", migration.Name);
                    record.Parameters.AddWithValue("c", migration.Checksum);
                    await record.ExecuteNonQueryAsync(cancellationToken);
                }
                await transaction.CommitAsync(cancellationToken);
                done.Add(migration);
            }

            // Data API (PostgREST) odświeża opis schematu sam po DDL; powiadomienie przyspiesza to po migracji.
            if (done.Count > 0) await ExecuteAsync(connection, "notify pgrst, 'reload schema'", cancellationToken);
            return done;
        }
        finally
        {
            await ExecuteAsync(connection, "select pg_advisory_unlock(@key)", CancellationToken.None, ("key", AdvisoryLockKey));
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
