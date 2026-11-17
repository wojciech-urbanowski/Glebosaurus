using System.Data;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Glebosaurus.Core;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Glebosaurus.Cloud.Admin;

/// <param name="Path">Plik kopii (ZIP).</param>
/// <param name="Tables">Tabela → liczba wierszy w kopii.</param>
/// <param name="Secrets">Ile sekretów z Vault (odszyfrowanych) jest w kopii.</param>
public sealed record CloudBackupResult(string Path, DateTimeOffset CreatedAt, IReadOnlyDictionary<string, long> Tables, int Secrets, long SizeBytes);

/// <summary>
/// Kopia zapasowa chmury (ARCHITEKTURA.md, zasada 10 – darmowy plan Supabase nie robi kopii). Zrzut robi sam program przez
/// połączenie z bazą (bez <c>pg_dump</c>, który musiałby być w wersji serwera): jedna transakcja tylko do odczytu z
/// migawką (REPEATABLE READ), więc wszystkie tabele są z tej samej chwili. Plik ZIP:
/// <list type="bullet">
/// <item><c>manifest.json</c> – projekt, czas, wersja programu, zastosowane migracje (z sumami), liczby wierszy;</item>
/// <item><c>tables/&lt;tabela&gt;.csv</c> – każda tabela schematu <c>glebosaurus</c> (COPY … CSV z nagłówkiem), bez
///   <c>agent_logs</c> (dzienniki diagnostyczne z 7 dni, do kilkudziesięciu MB);</item>
/// <item><c>secrets.json</c> – sekrety <c>glebosaurus:*</c> z Vault <b>odszyfrowane</b> (klucze Tuya, hasło Gmaila, adres
///   sygnału) – w surowym zrzucie Vault są zaszyfrowane kluczem projektu, który ginie razem z projektem.</item>
/// </list>
/// Plik jest więc sekretem jak <c>cloud-admin.json</c> (na Linuksie prawa 600). Nazwa: <c>glebosaurus-backup-&lt;id
/// projektu&gt;-&lt;RRRRMMDDTGGMMSSZ&gt;.zip</c>; zostaje <c>keep</c> najnowszych.
/// </summary>
public static class CloudBackup
{
    public const int FormatVersion = 1;
    public const string FilePrefix = "glebosaurus-backup-";

    /// <summary>Tabele pominięte w kopii: dzienniki agentów (diagnostyka z kilku dni, duże).</summary>
    public static readonly IReadOnlySet<string> SkippedTables = new HashSet<string>(StringComparer.Ordinal) { "agent_logs" };

    public static string FileName(string projectRef, DateTimeOffset createdAt) =>
        $"{FilePrefix}{projectRef}-{createdAt.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}.zip";

    /// <summary>Chwila utworzenia z nazwy pliku kopii tego projektu; null – plik nie jest taką kopią.</summary>
    public static DateTimeOffset? CreatedAt(string fileName, string projectRef)
    {
        var prefix = $"{FilePrefix}{projectRef}-";
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal) || !fileName.EndsWith(".zip", StringComparison.Ordinal)) return null;
        return DateTime.TryParseExact(fileName[prefix.Length..^4], "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)
            ? new DateTimeOffset(at, TimeSpan.Zero) : null;
    }

    /// <summary>Kopie tego projektu w katalogu, od najnowszej.</summary>
    public static IReadOnlyList<(string Path, DateTimeOffset CreatedAt)> List(string directory, string projectRef)
    {
        if (!Directory.Exists(directory)) return [];
        return [.. Directory.EnumerateFiles(directory, $"{FilePrefix}{projectRef}-*.zip")
            .Select(path => (Path: path, At: CreatedAt(Path.GetFileName(path), projectRef)))
            .Where(f => f.At is not null)
            .Select(f => (f.Path, f.At!.Value))
            .OrderByDescending(f => f.Item2)];
    }

    /// <summary>Usuwa starsze kopie tego projektu, zostawiając <paramref name="keep"/> najnowszych; zwraca usunięte.</summary>
    public static IReadOnlyList<string> Prune(string directory, string projectRef, int keep)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(keep, 1);
        var removed = new List<string>();
        foreach (var (path, _) in List(directory, projectRef).Skip(keep))
        {
            File.Delete(path);
            removed.Add(path);
        }
        return removed;
    }

    /// <summary>Robi kopię całej chmury do <paramref name="directory"/> (zapis atomowy: plik tymczasowy, potem zmiana nazwy).</summary>
    public static async Task<CloudBackupResult> CreateAsync(NpgsqlDataSource dataSource, string projectRef, string directory,
        TimeProvider clock, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var createdAt = clock.GetUtcNow();
        var path = Path.Combine(directory, FileName(projectRef, createdAt));
        var temporary = path + ".tmp";
        foreach (var stale in Directory.EnumerateFiles(directory, $"{FilePrefix}{projectRef}-*.zip.tmp")) File.Delete(stale);

        var tables = new Dictionary<string, long>(StringComparer.Ordinal);
        int secrets;
        try
        {
            await using (var file = CreatePrivateFile(temporary))
            {
                using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);
                await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
                await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
                await ExecuteAsync(connection, transaction, "set transaction read only", cancellationToken);
                var serverTime = (DateTime)(await ScalarAsync(connection, transaction, "select now()", cancellationToken))!;

                foreach (var table in await TablesAsync(connection, transaction, cancellationToken))
                {
                    tables[table] = (long)(await ScalarAsync(connection, transaction, $"select count(*) from glebosaurus.\"{table}\"", cancellationToken))!;
                    var entry = zip.CreateEntry($"tables/{table}.csv", CompressionLevel.SmallestSize);
                    await using var output = new StreamWriter(await entry.OpenAsync(cancellationToken), new UTF8Encoding(false));
                    using var export = await connection.BeginTextExportAsync(
                        $"copy (select * from glebosaurus.\"{table}\") to stdout with (format csv, header true)", cancellationToken);
                    var buffer = new char[64 * 1024];
                    int read;
                    while ((read = await export.ReadAsync(buffer, cancellationToken)) > 0) await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }

                var secretList = await SecretsAsync(connection, transaction, cancellationToken);
                secrets = secretList.Count;
                await WriteJsonAsync(zip, "secrets.json", secretList, cancellationToken);

                var migrations = await MigrationsAsync(connection, transaction, cancellationToken);
                await WriteJsonAsync(zip, "manifest.json", new JsonObject
                {
                    ["format"] = FormatVersion,
                    ["program"] = AppInfo.Version,
                    ["project"] = projectRef,
                    ["createdAt"] = createdAt.ToString("O", CultureInfo.InvariantCulture),
                    ["serverTime"] = new DateTimeOffset(DateTime.SpecifyKind(serverTime, DateTimeKind.Utc)).ToString("O", CultureInfo.InvariantCulture),
                    ["migrations"] = migrations,
                    ["tables"] = new JsonObject([.. tables.Select(t => new KeyValuePair<string, JsonNode?>(t.Key, t.Value))]),
                    ["skippedTables"] = new JsonArray([.. SkippedTables.Order().Select(t => (JsonNode)t)]),
                    ["secrets"] = secrets,
                    ["note"] = "Kopia Glebosaurusa: tabele schematu glebosaurus (CSV z COPY) i odszyfrowane sekrety z Vault (secrets.json) – plik jest sekretem."
                }, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            File.Move(temporary, path, overwrite: false);
        }
        catch
        {
            try { File.Delete(temporary); } catch (IOException) { }
            throw;
        }
        return new(path, createdAt, tables, secrets, new FileInfo(path).Length);
    }

    /// <summary>Plik tylko dla właściciela na Linuksie (prawa 600 od chwili utworzenia); na Windows – uprawnienia katalogu.</summary>
    private static FileStream CreatePrivateFile(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }

    private static async Task<IReadOnlyList<string>> TablesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("select tablename from pg_tables where schemaname = 'glebosaurus' order by tablename", connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var tables = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString(0);
            if (!SkippedTables.Contains(name) && name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_')) tables.Add(name);
        }
        return tables;
    }

    private static async Task<JsonArray> SecretsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            select name, description, decrypted_secret from vault.decrypted_secrets where name like 'glebosaurus:%' order by name
            """, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new JsonArray();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new JsonObject
            {
                ["name"] = reader.GetString(0),
                ["description"] = reader.IsDBNull(1) ? null : reader.GetString(1),
                ["value"] = reader.IsDBNull(2) ? null : reader.GetString(2)
            });
        return result;
    }

    private static async Task<JsonArray> MigrationsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("select version, name, checksum from glebosaurus.schema_migrations order by version", connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new JsonArray();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new JsonObject { ["version"] = reader.GetString(0), ["name"] = reader.GetString(1), ["checksum"] = reader.GetString(2) });
        return result;
    }

    private static async Task WriteJsonAsync(ZipArchive zip, string name, JsonNode content, CancellationToken cancellationToken)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.SmallestSize);
        await using var stream = await entry.OpenAsync(cancellationToken);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(content.ToJsonString(new JsonSerializerOptions { WriteIndented = true })), cancellationToken);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        return await command.ExecuteScalarAsync(cancellationToken);
    }
}

public sealed record DailyBackupOptions
{
    /// <summary>Pierwsza kopia nie wcześniej niż tyle po starcie agenta (start i pierwsze odczyty mają pierwszeństwo).</summary>
    public TimeSpan StartDelay { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Co ile sprawdzać, czy dzisiejsza kopia już jest (i ponawiać po porażce).</summary>
    public TimeSpan CheckEvery { get; init; } = TimeSpan.FromHours(1);
}

/// <summary>
/// Codzienna kopia w ramach <c>run</c> agenta z sekcją <c>cloud.backup</c> (najpierw bestia5, docelowo Raspberry Pi): raz
/// na dzień kalendarzowy (czas lokalny urządzenia), przy pierwszej okazji – komputer bywa w nocy wyłączony, więc bez stałej
/// godziny. Porażka (sieć, uśpiony projekt) – kolejna próba przy następnym sprawdzeniu; po udanej kopii usuwa nadmiarowe.
/// </summary>
public sealed class DailyCloudBackup(
    Func<CancellationToken, Task<CloudBackupResult>> backup, string directory, string projectRef, int keep,
    TimeZoneInfo zone, TimeProvider clock, ILogger logger, DailyBackupOptions? options = null)
{
    private readonly DailyBackupOptions settings = options ?? new();

    /// <summary>Ostatnia kopia (z plików w katalogu) i ostatni błąd – dla dziennika i statusu.</summary>
    public DateTimeOffset? LastBackupAt => CloudBackup.List(directory, projectRef) is [var newest, ..] ? newest.CreatedAt : null;

    public string? LastError { get; private set; }

    public bool IsDue(DateTimeOffset now) =>
        LastBackupAt is not { } last || Local(last) < Local(now);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(settings.StartDelay, clock, cancellationToken);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await TryBackupAsync(cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LastError = e.Message;
                logger.LogError(e, "Kopia zapasowa chmury zakończyła się nieoczekiwanym błędem; kolejna próba za {Minutes} min.", settings.CheckEvery.TotalMinutes);
            }
            await Task.Delay(settings.CheckEvery, clock, cancellationToken);
        }
    }

    /// <summary>Kopia, jeśli dziś jeszcze jej nie było. Publiczna, żeby testy mogły ją wołać na ręcznie przesuwanym czasie.</summary>
    /// <returns>true – zrobiono nową kopię.</returns>
    public async Task<bool> TryBackupAsync(CancellationToken cancellationToken)
    {
        if (!IsDue(clock.GetUtcNow())) return false;
        try
        {
            var result = await backup(cancellationToken);
            var removed = CloudBackup.Prune(directory, projectRef, keep);
            LastError = null;
            logger.LogInformation("Kopia zapasowa chmury: {File} ({Size} KB, odczytów {Readings}, sekretów {Secrets}); usunięto starszych: {Removed}.",
                Path.GetFileName(result.Path), result.SizeBytes / 1024, result.Tables.GetValueOrDefault("readings"), result.Secrets, removed.Count);
            return true;
        }
        catch (Exception e) when (e is NpgsqlException or IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException)
        {
            LastError = e.Message;
            logger.LogWarning("Kopia zapasowa chmury nie powiodła się: {Error}; kolejna próba za {Minutes} min.", e.Message, settings.CheckEvery.TotalMinutes);
            return false;
        }
    }

    private DateOnly Local(DateTimeOffset at) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);
}
