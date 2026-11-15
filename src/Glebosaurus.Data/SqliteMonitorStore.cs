using System.Globalization;
using Glebosaurus.Core.Domain;
using Glebosaurus.Core.Monitoring;
using Microsoft.Data.Sqlite;

namespace Glebosaurus.Data;

/// <summary>
/// Magazyn w pliku SQLite z trybem WAL. Czasy zapisane jako unix-milisekundy UTC, temperatura jako dziesiąte części
/// stopnia (liczby całkowite zamiast zmiennoprzecinkowych). Każda operacja otwiera krótkie połączenie.
/// </summary>
public sealed class SqliteMonitorStore(string databasePath) : IMonitorStore
{
    private const int SchemaVersion = 1;

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
        if (version == SchemaVersion) return;

        await ExecuteAsync(connection, """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS readings (
                sensor_id     TEXT    NOT NULL,
                at            INTEGER NOT NULL,
                moisture      INTEGER NULL,
                temp_tenths   INTEGER NULL,
                battery       INTEGER NULL,
                battery_state INTEGER NULL,
                PRIMARY KEY (sensor_id, at)
            ) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS alerts (
                id               INTEGER PRIMARY KEY AUTOINCREMENT,
                sensor_id        TEXT    NOT NULL,
                kind             TEXT    NOT NULL,
                detail           TEXT    NOT NULL,
                opened_at        INTEGER NOT NULL,
                last_notified_at INTEGER NULL,
                resolved_at      INTEGER NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_alerts_open ON alerts (sensor_id, kind) WHERE resolved_at IS NULL;
            CREATE TABLE IF NOT EXISTS report_runs (
                key     TEXT    PRIMARY KEY,
                sent_at INTEGER NOT NULL
            );
            PRAGMA user_version = 1;
            """, cancellationToken);
    }

    public async Task AddReadingAsync(StoredReading reading, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(connection,
            "INSERT OR IGNORE INTO readings (sensor_id, at, moisture, temp_tenths, battery, battery_state) VALUES ($s, $at, $m, $t, $b, $bs);",
            cancellationToken,
            ("$s", reading.SensorId), ("$at", reading.At.ToUnixTimeMilliseconds()), ("$m", reading.MoisturePercent),
            ("$t", reading.SoilTemperatureCelsius is { } t ? (int)Math.Round(t * 10m, MidpointRounding.AwayFromZero) : null),
            ("$b", reading.BatteryPercent), ("$bs", reading.BatteryState));
    }

    public async Task<StoredReading?> GetLatestAsync(string sensorId, CancellationToken cancellationToken) =>
        (await GetRecentAsync(sensorId, 1, cancellationToken)).FirstOrDefault();

    public async Task<IReadOnlyList<StoredReading>> GetRecentAsync(string sensorId, int count, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection,
            "SELECT at, moisture, temp_tenths, battery, battery_state FROM readings WHERE sensor_id = $s ORDER BY at DESC LIMIT $n;",
            ("$s", sensorId), ("$n", count));
        return await ReadReadingsAsync(command, sensorId, cancellationToken);
    }

    public async Task<IReadOnlyList<StoredReading>> GetRangeAsync(string sensorId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection,
            "SELECT at, moisture, temp_tenths, battery, battery_state FROM readings WHERE sensor_id = $s AND at BETWEEN $from AND $to ORDER BY at;",
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
                Nullable(reader, 1), reader.IsDBNull(2) ? null : reader.GetInt32(2) / 10m, Nullable(reader, 3), Nullable(reader, 4)));
        return result;
    }

    public async Task<IReadOnlyList<AlertRecord>> GetOpenAlertsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection,
            "SELECT id, sensor_id, kind, detail, opened_at, last_notified_at FROM alerts WHERE resolved_at IS NULL ORDER BY id;");
        var result = new List<AlertRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetInt64(0), reader.GetString(1), Enum.Parse<AlertKind>(reader.GetString(2)), reader.GetString(3),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
                reader.IsDBNull(5) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)), null));
        return result;
    }

    public async Task<AlertRecord> OpenAlertAsync(string sensorId, AlertKind kind, string detail, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection,
            "INSERT INTO alerts (sensor_id, kind, detail, opened_at) VALUES ($s, $k, $d, $at); SELECT last_insert_rowid();",
            ("$s", sensorId), ("$k", kind.ToString()), ("$d", detail), ("$at", at.ToUnixTimeMilliseconds()));
        var id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        return new(id, sensorId, kind, detail, at, null, null);
    }

    public async Task ResolveAlertAsync(long alertId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(connection, "UPDATE alerts SET resolved_at = $at WHERE id = $id AND resolved_at IS NULL;", cancellationToken,
            ("$at", at.ToUnixTimeMilliseconds()), ("$id", alertId));
    }

    public async Task MarkNotifiedAsync(IReadOnlyCollection<long> alertIds, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var id in alertIds)
            await ExecuteAsync(connection, "UPDATE alerts SET last_notified_at = $at WHERE id = $id;", cancellationToken, transaction,
                ("$at", at.ToUnixTimeMilliseconds()), ("$id", id));
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> HasReportRunAsync(string key, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection, "SELECT 1 FROM report_runs WHERE key = $k;", ("$k", key));
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task RecordReportRunAsync(string key, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(connection, "INSERT OR REPLACE INTO report_runs (key, sent_at) VALUES ($k, $at);", cancellationToken,
            ("$k", key), ("$at", at.ToUnixTimeMilliseconds()));
    }

    public async Task PurgeAsync(DateTimeOffset olderThan, CancellationToken cancellationToken)
    {
        var limit = olderThan.ToUnixTimeMilliseconds();
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(connection, """
            DELETE FROM readings    WHERE at < $limit;
            DELETE FROM alerts      WHERE resolved_at IS NOT NULL AND resolved_at < $limit;
            DELETE FROM report_runs WHERE sent_at < $limit;
            """, cancellationToken, ("$limit", limit));
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
