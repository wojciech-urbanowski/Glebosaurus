using System.Text.RegularExpressions;
using Glebosaurus.Cloud.Admin;

namespace Glebosaurus.Tests;

/// <summary>
/// Data API Supabase (PostgREST) ponawia transakcję po każdym SQLSTATE z klasy 40 (wycofanie transakcji, np. 40001
/// serialization_failure) – także zgłoszonym celowo przez funkcję. 2026-11-21 konflikt wersji konfiguracji (wtedy 40001)
/// kręcił się tak ok. 50 s, ok. 1000 ponowień na sekundę, i nie wracał do strony; testy SQL przez połączenie bezpośrednie
/// tego nie widziały (przez Data API – <c>DataApiOwnerTests</c> na próbnym projekcie). Dlatego obowiązująca definicja żadnej
/// funkcji nie zgłasza kodu z klasy 40; konflikt ma kod PT409 (Data API: HTTP 409).
/// </summary>
public sealed partial class SqlDataApiRulesTests
{
    [Fact]
    public void NoFunction_RaisesTransactionRollbackClass()
    {
        var offenders = SqlSafeUpdateTests.LatestFunctions(Migration.Embedded())
            .Where(f => RaisesClass40(f.Value))
            .Select(f => f.Key)
            .ToList();

        Assert.True(offenders.Count == 0,
            "Funkcje zgłaszają SQLSTATE z klasy 40 – Data API ponawia wtedy transakcję w pętli (użyj np. 22023 albo PTxyz):\n" +
            string.Join("\n", offenders));
    }

    [Theory]
    [InlineData("perform glebosaurus.fail('40001', 'conflict');", true)]
    [InlineData("raise exception using errcode = '40P01', message = 'x';", true)]
    [InlineData("raise sqlstate '40001';", true)]
    [InlineData("raise exception using errcode = 'serialization_failure';", true)]
    [InlineData("perform glebosaurus.fail('PT409', 'conflict');", false)]
    [InlineData("perform glebosaurus.fail('22023', 'za dużo: 40001 odczytów');", false)]
    [InlineData("-- dawniej fail('40001', …)\nperform glebosaurus.fail('PT409', 'conflict');", false)]
    public void Scanner_FindsClass40Codes(string body, bool expected) => Assert.Equal(expected, RaisesClass40(body));

    [Fact]
    public void ConfigConflict_UsesHttp409Code()
    {
        var body = SqlSafeUpdateTests.LatestFunctions(Migration.Embedded())["glebosaurus.require_config_version"];
        Assert.Contains("fail('PT409'", body);
    }

    private static bool RaisesClass40(string body) => Class40().IsMatch(LineComment().Replace(body, ""));

    [GeneratedRegex(@"(?:fail\s*\(\s*|errcode\s*=\s*|sqlstate\s+)'(?:40[0-9A-Z]{3}|transaction_rollback|serialization_failure|deadlock_detected|transaction_integrity_constraint_violation|statement_completion_unknown)'",
        RegexOptions.IgnoreCase)]
    private static partial Regex Class40();

    [GeneratedRegex(@"--[^\n]*")]
    private static partial Regex LineComment();
}
