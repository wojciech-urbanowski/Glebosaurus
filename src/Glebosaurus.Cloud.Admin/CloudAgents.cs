using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;

namespace Glebosaurus.Cloud.Admin;

/// <summary>Agent w chmurze z punktu widzenia właściciela.</summary>
public sealed record CloudAgentInfo(string Name, bool CanSendMail, int? SenderPriority, bool Enabled, bool Enrolled, DateTimeOffset? LastSeenAt);

/// <summary>Agenci: lista i nowy agent z jednorazowym kodem rejestracji (ARCHITEKTURA.md, zasada 4).</summary>
public static partial class CloudAgents
{
    public static async Task<IReadOnlyList<CloudAgentInfo>> ListAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            select name, can_send_mail, sender_priority, enabled, token_hash is not null, last_seen_at from glebosaurus.agents order by name
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var agents = new List<CloudAgentInfo>();
        while (await reader.ReadAsync(cancellationToken))
            agents.Add(new(reader.GetString(0), reader.GetBoolean(1), reader.IsDBNull(2) ? null : reader.GetInt32(2), reader.GetBoolean(3),
                reader.GetBoolean(4), reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5)));
        return agents;
    }

    /// <summary>Tworzy (albo zmienia rolę) agenta i daje kod rejestracji ważny 24 h. Kod pokazuje się tylko raz.</summary>
    /// <param name="senderPriority">null – agent tylko zbiera; 1–9 – kandydat na nadawcę (1 najważniejszy).</param>
    public static async Task<string> CreateWithCodeAsync(NpgsqlDataSource dataSource, string name, int? senderPriority, CancellationToken cancellationToken)
    {
        if (!AgentName().IsMatch(name))
            throw new ArgumentException("Nazwa agenta: małe litery, cyfry, „_” i „-” (do 40 znaków, na początku litera albo cyfra).");
        if (senderPriority is < 1 or > 9) throw new ArgumentException("Priorytet nadawcy: od 1 (najważniejszy) do 9.");
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var upsert = new NpgsqlCommand("select glebosaurus.admin_agent_upsert(@n, @m, @p)", connection, transaction))
        {
            upsert.Parameters.AddWithValue("n", name);
            upsert.Parameters.AddWithValue("m", senderPriority is not null);
            upsert.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Integer) { Value = (object?)senderPriority ?? DBNull.Value });
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }
        string code;
        await using (var create = new NpgsqlCommand("select glebosaurus.admin_enrollment_code(@n)", connection, transaction))
        {
            create.Parameters.AddWithValue("n", name);
            code = (string)(await create.ExecuteScalarAsync(cancellationToken))!;
        }
        await transaction.CommitAsync(cancellationToken);
        return code;
    }

    /// <summary>
    /// Odcina agenta od chmury (zgubione albo skradzione urządzenie, wyciek tokenu): unieważnia token od razu i kasuje
    /// ważne, niewykorzystane kody rejestracji tego agenta (kod mógł wyciec razem z urządzeniem albo z ekranu). Agent zostaje na
    /// liście z rolą – nowy kod (<see cref="CreateWithCodeAsync"/>) i <c>enroll</c> dopuszczają go z powrotem. Dzierżaw nie
    /// zabiera: wygasają same (czujniki po ok. 32 min, blokada nadawcy po ok. 7 min), tak jak przy nagłym zniknięciu.
    /// </summary>
    /// <exception cref="ArgumentException">Nie ma takiego agenta.</exception>
    public static async Task<CloudAgentRevocation> RevokeAsync(NpgsqlDataSource dataSource, string name, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        bool hadToken;
        await using (var find = new NpgsqlCommand("select token_hash is not null from glebosaurus.agents where name = @n for update", connection, transaction))
        {
            find.Parameters.AddWithValue("n", name);
            hadToken = await find.ExecuteScalarAsync(cancellationToken) is bool enrolled
                ? enrolled
                : throw new ArgumentException($"W chmurze nie ma agenta „{name}” (lista: glebosaurus cloud agent --admin PLIK).");
        }
        await using (var revoke = new NpgsqlCommand("select glebosaurus.admin_agent_revoke(@n)", connection, transaction))
        {
            revoke.Parameters.AddWithValue("n", name);
            await revoke.ExecuteNonQueryAsync(cancellationToken);
        }
        int cancelledCodes;
        await using (var codes = new NpgsqlCommand("""
            delete from glebosaurus.enrollment_codes
             where used_at is null and expires_at > glebosaurus.clock()
               and agent_id = (select id from glebosaurus.agents where name = @n)
            """, connection, transaction))
        {
            codes.Parameters.AddWithValue("n", name);
            cancelledCodes = await codes.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new(hadToken, cancelledCodes);
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,39}$")]
    private static partial Regex AgentName();
}

/// <summary>Wynik <see cref="CloudAgents.RevokeAsync"/>: czy agent miał ważny token i ile ważnych, niewykorzystanych kodów
/// rejestracji skasowano.</summary>
public sealed record CloudAgentRevocation(bool HadToken, int CancelledCodes);
