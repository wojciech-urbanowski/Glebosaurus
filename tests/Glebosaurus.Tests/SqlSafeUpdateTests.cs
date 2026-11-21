using System.Text.RegularExpressions;
using Glebosaurus.Cloud.Admin;

namespace Glebosaurus.Tests;

/// <summary>
/// Supabase ładuje pg_safeupdate dla roli authenticator (Data API: strona właściciela i agenci) – UPDATE i DELETE bez
/// WHERE kończą się błędem „UPDATE requires a WHERE clause”, także wewnątrz funkcji i wyzwalaczy. Testy SQL łączą się z
/// bazą wprost, bez tego rozszerzenia (nie da się go tam załadować), więc regułę pilnuje ten test na treści migracji:
/// w obowiązującej definicji każdej funkcji każde UPDATE/DELETE ma WHERE na najwyższym poziomie.
/// </summary>
public sealed partial class SqlSafeUpdateTests
{
    [Fact]
    public void EveryFunction_HasWhereInEachUpdateAndDelete()
    {
        var offenders = LatestFunctions(Migration.Embedded())
            .SelectMany(f => StatementsWithoutWhere(f.Value).Select(s => $"{f.Key}: {s}"))
            .ToList();

        Assert.True(offenders.Count == 0,
            "UPDATE/DELETE bez WHERE (Data API Supabase je odrzuca – dodaj warunek, np. „where id” dla settings):\n" +
            string.Join("\n", offenders));
    }

    [Fact]
    public void Scanner_FindsMissingWhere_AndIgnoresUpsertsAndSubqueries()
    {
        const string body = """
            begin
                update glebosaurus.settings set updated_at = now();
                update glebosaurus.settings set a = (select x from t where y) ;
                delete from glebosaurus.commands;
                update glebosaurus.sensors set name = 'a;b' where id = p_id;
                insert into t (a) values (1) on conflict (a) do update set a = excluded.a;
                -- update glebosaurus.settings set ignored = true;
                with gone as (delete from t where a returning a) select 1;
            end
            """;

        var found = StatementsWithoutWhere(body);

        Assert.Equal(3, found.Count);
        Assert.StartsWith("update glebosaurus.settings set updated_at", found[0]);
        Assert.StartsWith("update glebosaurus.settings set a =", found[1]);
        Assert.StartsWith("delete from glebosaurus.commands", found[2]);
    }

    [Fact]
    public void LatestDefinition_WinsAndDroppedFunctionsDisappear()
    {
        var migrations = new[]
        {
            new Migration("1", "a", "create function public.f() returns void language sql as $$ update t set a = 1; $$;\n" +
                                    "create function public.g() returns void language sql as $$ delete from t; $$;"),
            new Migration("2", "b", "create or replace function public.f() returns void language sql as $$ update t set a = 1 where id; $$;\n" +
                                    "drop function public.g();")
        };

        var latest = LatestFunctions(migrations);

        Assert.Equal(["public.f"], latest.Keys);
        Assert.Empty(StatementsWithoutWhere(latest["public.f"]));
    }

    /// <summary>Treść obowiązującej definicji każdej funkcji (ostatnie create w kolejności migracji, bez usuniętych).</summary>
    internal static Dictionary<string, string> LatestFunctions(IEnumerable<Migration> migrations)
    {
        var latest = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var migration in migrations)
        {
            foreach (Match statement in DefinitionOrDrop().Matches(migration.Sql))
            {
                if (statement.Groups["dropped"].Success) latest.Remove(statement.Groups["dropped"].Value);
                else latest[statement.Groups["name"].Value] = statement.Groups["body"].Value;
            }
        }
        return latest;
    }

    /// <summary>UPDATE/DELETE bez WHERE poza nawiasami (podzapytania się nie liczą); ON CONFLICT … DO UPDATE to INSERT.</summary>
    private static List<string> StatementsWithoutWhere(string body)
    {
        var code = StringLiteral().Replace(LineComment().Replace(body, ""), "''");
        var found = new List<string>();
        foreach (Match statement in UpdateOrDelete().Matches(code))
        {
            if (DoBefore().IsMatch(code[..statement.Index])) continue;
            var text = statement.Value;
            for (var previous = ""; previous != text;)
            {
                previous = text;
                text = Parenthesized().Replace(text, "()");
            }
            if (!Where().IsMatch(text)) found.Add(string.Join(' ', statement.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
        }
        return found;
    }

    [GeneratedRegex(@"create\s+(?:or\s+replace\s+)?function\s+(?<name>[\w.]+)\s*\(.*?\$\$(?<body>.*?)\$\$\s*;|drop\s+function\s+(?:if\s+exists\s+)?(?<dropped>[\w.]+)\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex DefinitionOrDrop();

    [GeneratedRegex(@"--[^\n]*")]
    private static partial Regex LineComment();

    [GeneratedRegex(@"'(?:[^']|'')*'")]
    private static partial Regex StringLiteral();

    [GeneratedRegex(@"(?<![\w.])(?:update|delete\s+from)\s+[\w.]+\b[^;]*", RegexOptions.IgnoreCase)]
    private static partial Regex UpdateOrDelete();

    [GeneratedRegex(@"\bdo\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex DoBefore();

    [GeneratedRegex(@"\([^()]*\)")]
    private static partial Regex Parenthesized();

    [GeneratedRegex(@"\bwhere\b", RegexOptions.IgnoreCase)]
    private static partial Regex Where();
}
