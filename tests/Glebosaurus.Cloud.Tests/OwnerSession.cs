using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;

namespace Glebosaurus.Cloud.Tests;

/// <summary>
/// Wywołania funkcji strony jako zalogowany właściciel w jednej transakcji wycofywanej na końcu – test kontraktowy
/// sprawdza dziesiątki zestawów ustawień, a nie może zmienić wspólnych ustawień innym testom.
/// </summary>
public sealed class OwnerSession : IAsyncDisposable
{
    private readonly NpgsqlConnection connection;
    private readonly NpgsqlTransaction transaction;
    private int savepoints;

    private OwnerSession(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        this.connection = connection;
        this.transaction = transaction;
    }

    public static async Task<OwnerSession> StartAsync(CloudDatabase db, JsonObject claims)
    {
        var connection = await db.DataSource.OpenConnectionAsync();
        var transaction = await connection.BeginTransactionAsync();
        await using (var setup = new NpgsqlCommand("select set_config('request.jwt.claims', @c, true); set local role authenticated;", connection, transaction))
        {
            setup.Parameters.AddWithValue("c", claims.ToJsonString());
            await setup.ExecuteNonQueryAsync();
        }
        return new(connection, transaction);
    }

    /// <summary>Wywołanie w punkcie zapisu: błąd cofa tylko to wywołanie, a sesja działa dalej.</summary>
    public async Task<JsonElement> CallAsync(string function, params (string Name, object? Value)[] args)
    {
        var savepoint = $"s{++savepoints}";
        await transaction.SaveAsync(savepoint);
        try
        {
            await using var command = new NpgsqlCommand(
                $"select public.{function}({string.Join(", ", args.Select(a => $"{a.Name} => @{a.Name}"))})::text", connection, transaction);
            foreach (var (name, value) in args)
                command.Parameters.Add(value switch
                {
                    null => new NpgsqlParameter(name, DBNull.Value),
                    JsonNode node => new NpgsqlParameter(name, NpgsqlDbType.Jsonb) { Value = node.ToJsonString() },
                    _ => new NpgsqlParameter(name, value)
                });
            var text = (string?)await command.ExecuteScalarAsync();
            await transaction.ReleaseAsync(savepoint);
            return text is null ? default : JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (PostgresException)
        {
            await transaction.RollbackAsync(savepoint);
            throw;
        }
    }

    public async Task<long> VersionAsync() => (await CallAsync("owner_config")).GetProperty("configVersion").GetInt64();

    public async ValueTask DisposeAsync()
    {
        await transaction.RollbackAsync();
        await transaction.DisposeAsync();
        await connection.DisposeAsync();
    }
}
