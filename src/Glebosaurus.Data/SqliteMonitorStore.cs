using System.Globalization;
using Glebosaurus.Core.Cloud;
using Glebosaurus.Core.Domain;
using Glebosaurus.Core.Monitoring;
using Microsoft.Data.Sqlite;

namespace Glebosaurus.Data;

/// <summary>
/// Magazyn w pliku SQLite z trybem WAL. Czasy zapisane jako unix-milisekundy UTC, temperatura jako dziesiąte części
/// stopnia (liczby całkowite zamiast zmiennoprzecinkowych). Każda operacja otwiera krótkie połączenie. Baza jest buforem
/// odczytów do wysłania (<see cref="IReadingOutbox"/>, kolumna <c>sync_state</c>) i kopią odczytów innych agentów
/// (<see cref="IReadingMirrorStore"/>, tabela <c>cloud_feed</c> z numerem ostatniego odczytu z chmury).
/// </summary>
public sealed class SqliteMonitorStore(string databasePath) : IMonitorStore, IReadingOutbox, IReadingMirrorStore
{
    private const int SchemaVersion = 1;

    // Stan wysłania odczytu do chmury (kolumna sync_state): 0 – do wysłania, 1 – przyjęty, 2 – odrzucony (bez ponowień).
    private const int Accepted = 1, Rejected = 2;

    private string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Private,
        Pooling = false
    }.ToString();

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        await using var connection = await OpenAsync(cancellationToken);
        var version = Convert.ToInt32(await ScalarAsync(connection, "PRAGMA user_version;", cancellationToken), CultureInfo.InvariantCulture);
        if (version > SchemaVersion)
            throw new InvalidOperationException($"Baza {databasePath} pochodzi z nowszej wersji programu (schemat {version}).");
        if (version < SchemaVersion) await CreateSchemaAsync(connection, cancellationToken);
        await UpgradeAsync(connection, cancellationToken);
    }

    private static async Task CreateSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS readings (
                sensor_id     TEXT    NOT NULL,
                at            INTEGER NOT NULL,
                moisture      INTEGER NULL,
                temp_tenths   INTEGER NULL,
                battery       INTEGER NULL,
                battery_state INTEGER NULL,
                sync_state    INTEGER NOT NULL DEFAULT 0,
                light_lux     INTEGER NULL,
                conductivity  INTEGER NULL,
                PRIMARY KEY (sensor_id, at)
            ) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS ix_readings_unsent ON readings (at) WHERE sync_state = 0;
            CREATE TABLE IF NOT EXISTS cloud_feed (source TEXT PRIMARY KEY, last_seq INTEGER NOT NULL);
            PRAGMA user_version = 1;
            """, cancellationToken);
    }

    /// <summary>
    /// Bazy z wersji sprzed 0.2.0 (bez chmury) dostają kolumnę <c>sync_state</c> – ich odczyty są „do wysłania”, więc historia
    /// trafia do chmury tą samą drogą – i tabelę <c>cloud_feed</c>. Ich tabele <c>alerts</c> i <c>report_runs</c> zostają
    /// nieużywane (stan alertów i raportów jest w chmurze). Bazy sprzed 0.4.0 dostają kolumny <c>light_lux</c> i
    /// <c>conductivity</c> (czujnik Flower Care) – bez zmiany <c>user_version</c>, bo poprzednia wersja programu (powrót po
    /// nieudanej aktualizacji) czyta i zapisuje wymienione kolumny, więc dodatkowe jej nie przeszkadzają.
    /// </summary>
    private static async Task UpgradeAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var present = Convert.ToInt64(await ScalarAsync(connection,
            "SELECT COUNT(*) FROM pragma_table_info('readings') WHERE name = 'sync_state';", cancellationToken), CultureInfo.InvariantCulture);
        if (present == 0)
            await ExecuteAsync(connection, """
                ALTER TABLE readings ADD COLUMN sync_state INTEGER NOT NULL DEFAULT 0;
                CREATE INDEX IF NOT EXISTS ix_readings_unsent ON readings (at) WHERE sync_state = 0;
                """, cancellationToken);
        await ExecuteAsync(connection, "CREATE TABLE IF NOT EXISTS cloud_feed (source TEXT PRIMARY KEY, last_seq INTEGER NOT NULL);",
            cancellationToken);
        foreach (var column in new[] { "light_lux", "conductivity" })
            if (Convert.ToInt64(await ScalarAsync(connection, $"SELECT COUNT(*) FROM pragma_table_info('readings') WHERE name = '{column}';", cancellationToken),
                    CultureInfo.InvariantCulture) == 0)
                await ExecuteAsync(connection, $"ALTER TABLE readings ADD COLUMN {column} INTEGER NULL;", cancellationToken);
    }

    public async Task AddReadingAsync(StoredReading reading, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(connection,
            "INSERT OR IGNORE INTO readings (sensor_id, at, moisture, temp_tenths, battery, battery_state, light_lux, conductivity) VALUES ($s, $at, $m, $t, $b, $bs, $l, $c);",
            cancellationToken,
            ("$s", reading.SensorId), ("$at", reading.At.ToUnixTimeMilliseconds()), ("$m", reading.MoisturePercent),
            ("$t", reading.SoilTemperatureCelsius is { } t ? (int)Math.Round(t * 10m, MidpointRounding.AwayFromZero) : null),
            ("$b", reading.BatteryPercent), ("$bs", reading.BatteryState), ("$l", reading.LightLux), ("$c", reading.ConductivityMicroSiemens));
    }

    public async Task<StoredReading?> GetLatestAsync(string sensorId, CancellationToken cancellationToken) =>
        (await GetRecentAsync(sensorId, 1, cancellationToken)).FirstOrDefault();

    public async Task<IReadOnlyList<StoredReading>> GetRecentAsync(string sensorId, int count, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection,
            "SELECT at, moisture, temp_tenths, battery, battery_state, light_lux, conductivity FROM readings WHERE sensor_id = $s ORDER BY at DESC LIMIT $n;",
            ("$s", sensorId), ("$n", count));
        return await ReadReadingsAsync(command, sensorId, cancellationToken);
    }

    public async Task<IReadOnlyList<StoredReading>> GetRangeAsync(string sensorId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection,
            "SELECT at, moisture, temp_tenths, battery, battery_state, light_lux, conductivity FROM readings WHERE sensor_id = $s AND at BETWEEN $from AND $to ORDER BY at;",
            ("$s", sensorId), ("$from", from.ToUnixTimeMilliseconds()), ("$to", to.ToUnixTimeMilliseconds()));
        return await ReadReadingsAsync(command, sensorId, cancellationToken);
    }

    public async Task<(DateTimeOffset First, DateTimeOffset Last)?> GetReadingBoundsAsync(string sensorId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection, "SELECT MIN(at), MAX(at) FROM readings WHERE sensor_id = $s;", ("$s", sensorId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0)) return null;
        return (DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)));
    }

    private static async Task<IReadOnlyList<StoredReading>> ReadReadingsAsync(SqliteCommand command, string sensorId, CancellationToken cancellationToken)
    {
        var result = new List<StoredReading>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(sensorId, DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)),
                Nullable(reader, 1), reader.IsDBNull(2) ? null : reader.GetInt32(2) / 10m, Nullable(reader, 3), Nullable(reader, 4))
            {
                LightLux = Nullable(reader, 5), ConductivityMicroSiemens = Nullable(reader, 6)
            });
        return result;
    }

    public async Task<IReadOnlyList<StoredReading>> GetUnsentAsync(int limit, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection, """
            SELECT sensor_id, at, moisture, temp_tenths, battery, battery_state, light_lux, conductivity FROM readings
             WHERE sync_state = 0 ORDER BY at, sensor_id LIMIT $n;
            """, ("$n", limit));
        var result = new List<StoredReading>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetString(0), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
                Nullable(reader, 2), reader.IsDBNull(3) ? null : reader.GetInt32(3) / 10m, Nullable(reader, 4), Nullable(reader, 5))
            {
                LightLux = Nullable(reader, 6), ConductivityMicroSiemens = Nullable(reader, 7)
            });
        return result;
    }

    public async Task<int> CountUnsentAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return Convert.ToInt32(await ScalarAsync(connection, "SELECT COUNT(*) FROM readings WHERE sync_state = 0;", cancellationToken),
            CultureInfo.InvariantCulture);
    }

    public async Task MarkSentAsync(IReadOnlyCollection<StoredReading> accepted, IReadOnlyCollection<StoredReading> rejected,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var (readings, state) in new[] { (accepted, Accepted), (rejected, Rejected) })
            foreach (var reading in readings)
                await ExecuteAsync(connection, "UPDATE readings SET sync_state = $state WHERE sensor_id = $s AND at = $at;", cancellationToken,
                    transaction, ("$state", state), ("$s", reading.SensorId), ("$at", reading.At.ToUnixTimeMilliseconds()));
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<long?> GetFeedCursorAsync(string source, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection, "SELECT last_seq FROM cloud_feed WHERE source = $src;", ("$src", source));
        return await command.ExecuteScalarAsync(cancellationToken) is long cursor ? cursor : null;
    }

    public async Task<int> AddFromCloudAsync(string source, IReadOnlyCollection<StoredReading> readings, long cursor,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var added = 0;
        await using (var insert = Command(connection, $"""
            INSERT OR IGNORE INTO readings (sensor_id, at, moisture, temp_tenths, battery, battery_state, light_lux, conductivity, sync_state)
            VALUES ($s, $at, $m, $t, $b, $bs, $l, $c, {Accepted});
            """, ("$s", null), ("$at", null), ("$m", null), ("$t", null), ("$b", null), ("$bs", null), ("$l", null), ("$c", null)))
        {
            insert.Transaction = transaction;
            foreach (var reading in readings)
            {
                insert.Parameters["$s"].Value = reading.SensorId;
                insert.Parameters["$at"].Value = reading.At.ToUnixTimeMilliseconds();
                insert.Parameters["$m"].Value = (object?)reading.MoisturePercent ?? DBNull.Value;
                insert.Parameters["$t"].Value = reading.SoilTemperatureCelsius is { } t ? (int)Math.Round(t * 10m, MidpointRounding.AwayFromZero) : DBNull.Value;
                insert.Parameters["$b"].Value = (object?)reading.BatteryPercent ?? DBNull.Value;
                insert.Parameters["$bs"].Value = (object?)reading.BatteryState ?? DBNull.Value;
                insert.Parameters["$l"].Value = (object?)reading.LightLux ?? DBNull.Value;
                insert.Parameters["$c"].Value = (object?)reading.ConductivityMicroSiemens ?? DBNull.Value;
                added += await insert.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        await ExecuteAsync(connection, "INSERT OR REPLACE INTO cloud_feed (source, last_seq) VALUES ($src, $seq);", cancellationToken, transaction,
            ("$src", source), ("$seq", cursor));
        await transaction.CommitAsync(cancellationToken);
        return added;
    }

    public async Task PurgeAsync(DateTimeOffset olderThan, CancellationToken cancellationToken)
    {
        var limit = olderThan.ToUnixTimeMilliseconds();
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(connection, "DELETE FROM readings WHERE at < $limit;", cancellationToken, ("$limit", limit));
    }

    private static int? Nullable(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(connection, sql, parameters);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken, SqliteTransaction transaction, params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(connection, sql, parameters);
        command.Transaction = transaction;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, sql);
        return await command.ExecuteScalarAsync(cancellationToken);
    }
}
