using System.Text.Json;
using Npgsql;

namespace Glebosaurus.Cloud.Admin;

/// <summary>Właściciel strony w chmurze: konto Supabase Auth z listy <c>glebosaurus.owners</c>.</summary>
/// <param name="Account">Konto wciąż istnieje w Supabase Auth.</param>
/// <param name="Totp">Konto ma potwierdzony drugi składnik TOTP (bez niego strona poprosi o dodanie przy logowaniu).</param>
public sealed record CloudOwnerInfo(string Email, Guid UserId, DateTimeOffset AddedAt, bool Account, bool Totp);

/// <summary>Właściciele strony w chmurze (faza 3, ARCHITEKTURA.md, zasada 4) – tylko przez połączenie z bazą.</summary>
public static class CloudOwners
{
    public static async Task<IReadOnlyList<CloudOwnerInfo>> ListAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("select glebosaurus.admin_owner_list()::text");
        using var document = JsonDocument.Parse((string)(await command.ExecuteScalarAsync(cancellationToken))!);
        return [.. document.RootElement.EnumerateArray().Select(o => new CloudOwnerInfo(
            o.GetProperty("email").GetString()!, o.GetProperty("userId").GetGuid(), o.GetProperty("addedAt").GetDateTimeOffset(),
            o.GetProperty("account").GetBoolean(), o.GetProperty("totp").GetBoolean()))];
    }

    /// <summary>Czy w Supabase Auth jest konto o tym adresie i czy ma potwierdzony TOTP (do planu przed <c>--confirm</c>).</summary>
    public static async Task<(bool Exists, bool Totp)> AccountAsync(NpgsqlDataSource dataSource, string email, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "select u.id is not null, exists (select 1 from auth.mfa_factors f where f.user_id = u.id and f.factor_type::text = 'totp' " +
            "and f.status::text = 'verified') from (select 1) one left join lateral (select id from auth.users " +
            "where lower(email) = lower(trim(@e)) and deleted_at is null order by created_at limit 1) u on true");
        command.Parameters.AddWithValue("e", email);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return (reader.GetBoolean(0), reader.GetBoolean(1));
    }

    /// <exception cref="PostgresException">SQLSTATE 22023 – w Supabase Auth nie ma takiego konta.</exception>
    public static async Task AddAsync(NpgsqlDataSource dataSource, string email, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("select glebosaurus.admin_owner_add(@e)");
        command.Parameters.AddWithValue("e", email);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <returns>Czy taki właściciel był na liście.</returns>
    public static async Task<bool> RemoveAsync(NpgsqlDataSource dataSource, string email, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("select glebosaurus.admin_owner_remove(@e)");
        command.Parameters.AddWithValue("e", email);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
}
