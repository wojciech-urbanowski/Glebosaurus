using System.Text.RegularExpressions;
using Glebosaurus.Core.Cloud;

namespace Glebosaurus.Tests;

/// <summary>
/// Strona właściciela w chmurze (faza 3, ARCHITEKTURA.md, zasada 10): odnośnik z adresem projektu i pliki <c>web/</c> –
/// zasady, których nie sprawdzi przeglądarka w CI: CSP bez skryptów inline, dane tylko jako tekst, wspólne pliki z
/// lokalnym panelem i migracja, której strona potrzebuje.
/// </summary>
public sealed partial class OwnerWebTests
{
    private const string Project = "https://abcdefghijklmnopqrst.supabase.co";

    private static readonly string Root = FindRepositoryRoot();
    private static readonly string Web = Path.Combine(Root, "web");

    // ---- odnośnik ------------------------------------------------------------------------------------------------

    [Fact]
    public void WebLink_CarriesProjectAndKeyInFragment()
    {
        var link = OwnerWebLink.Create(OwnerWebLink.DefaultPage, Project + "/", " sb_publishable_AbC-12_x ");

        Assert.Equal("https://wojciech-urbanowski.github.io/Glebosaurus/#url=https%3A%2F%2Fabcdefghijklmnopqrst.supabase.co&key=sb_publishable_AbC-12_x",
            link.ToUri());
    }

    [Fact]
    public void WebLink_LocalPreviewPage_IsAllowedOverHttp()
    {
        var link = OwnerWebLink.Create("http://127.0.0.1:8471/", Project, "sb_publishable_x");

        Assert.StartsWith("http://127.0.0.1:8471/#url=", link.ToUri());
    }

    [Theory]
    [InlineData("https://example.com/strona/", "https://example.com/", "sb_publishable_x")]         // nie projekt Supabase
    [InlineData("https://example.com/strona/", Project, "sb_secret_x")]                             // klucz tajny – nigdy
    [InlineData("http://example.com/strona/", Project, "sb_publishable_x")]                         // strona bez HTTPS
    [InlineData("https://example.com/strona/?a=1", Project, "sb_publishable_x")]
    [InlineData("https://user:haslo@example.com/", Project, "sb_publishable_x")]
    [InlineData("strona", Project, "sb_publishable_x")]
    public void WebLink_WithWrongData_IsRejected(string page, string project, string key)
    {
        Assert.Throws<InvalidDataException>(() => OwnerWebLink.Create(page, project, key));
    }

    // ---- pliki strony ---------------------------------------------------------------------------------------------

    public static TheoryData<string> Pages() => [.. Directory.GetFiles(Web, "*.html").Select(f => Path.GetFileName(f))];

    [Theory]
    [MemberData(nameof(Pages))]
    public void Page_HasStrictCspAndNoInlineCode(string page)
    {
        var html = File.ReadAllText(Path.Combine(Web, page));

        var csp = CspMeta().Match(html);
        Assert.True(csp.Success, "brak <meta http-equiv=\"Content-Security-Policy\">");
        var policy = csp.Groups[1].Value;
        Assert.Contains("default-src 'none'", policy);
        Assert.Contains("script-src 'self';", policy);
        Assert.Contains("base-uri 'none'", policy);
        Assert.Contains("form-action 'none'", policy);
        Assert.DoesNotContain("unsafe", policy);
        Assert.Contains("<meta name=\"referrer\" content=\"no-referrer\">", html);
        Assert.DoesNotMatch(new Regex("<script(?![^>]*\\bsrc=)", RegexOptions.IgnoreCase), html);
        Assert.DoesNotMatch(new Regex("\\son[a-z]+=", RegexOptions.IgnoreCase), html);
        Assert.DoesNotMatch(new Regex("style=", RegexOptions.IgnoreCase), html);
        Assert.Contains("<script type=\"module\" src=\"", html);
    }

    public static TheoryData<string> Scripts() => [.. Directory.GetFiles(Web, "*.js").Select(f => Path.GetFileName(f))];

    /// <summary>Dane z bazy (nazwy czujników, statusy agentów) nie mogą stać się kodem: tylko textContent i append.</summary>
    [Theory]
    [MemberData(nameof(Scripts))]
    public void Script_NeverInsertsHtml(string script)
    {
        var code = File.ReadAllText(Path.Combine(Web, script));

        foreach (var forbidden in new[] { "innerHTML", "outerHTML", "insertAdjacentHTML", "document.write", "eval(", "new Function", "setAttribute('style'" })
            Assert.DoesNotContain(forbidden, code);
        foreach (Match import in ImportFrom().Matches(code))
            Assert.Matches("^\\./(vendor/)?[a-z-]+\\.js$", import.Groups[1].Value);
    }

    /// <summary>Biblioteka QR jest wgrana bez zmian (qrcode-generator 2.0.4, dist/qrcode.mjs) – THIRD-PARTY-NOTICES.md.</summary>
    [Fact]
    public void VendoredQrLibrary_IsUnmodified()
    {
        var text = File.ReadAllText(Path.Combine(Web, "vendor", "qrcode.js")).ReplaceLineEndings("\n");
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

        Assert.Equal("ea91d7118a5395289170da848b7c6758b996163bfbccf312591ab65a4911b7c0", hash);
        Assert.Contains("qrcode-generator", File.ReadAllText(Path.Combine(Root, "THIRD-PARTY-NOTICES.md")));
        Assert.True(File.Exists(Path.Combine(Root, "licenses", "qrcode-generator-MIT.txt")));
    }

    [Theory]
    [InlineData("format.js")]
    [InlineData("favicon.svg")]
    public void SharedFile_IsIdenticalToLocalPanel(string file)
    {
        var panel = File.ReadAllText(Path.Combine(Root, "src", "Glebosaurus.Web", "wwwroot", file)).ReplaceLineEndings("\n");
        var web = File.ReadAllText(Path.Combine(Web, file)).ReplaceLineEndings("\n");

        Assert.True(panel == web, $"web/{file} różni się od src/Glebosaurus.Web/wwwroot/{file} – zmiany rób w obu plikach.");
    }

    [Fact]
    public void RequiredMigration_Exists_AndDefinesEveryOwnerFunctionThePageCalls()
    {
        var shell = File.ReadAllText(Path.Combine(Web, "shell.js"));
        var required = RequiredMigration().Match(shell).Groups[1].Value;
        var migrations = Directory.GetFiles(Path.Combine(Root, "supabase", "migrations"), "*.sql").Order(StringComparer.Ordinal).ToList();
        var upToRequired = migrations.Where(m => string.CompareOrdinal(Path.GetFileName(m)[..14], required) <= 0).ToList();
        Assert.Contains(migrations, m => Path.GetFileName(m).StartsWith(required + "_", StringComparison.Ordinal));

        var defined = string.Concat(upToRequired.Select(File.ReadAllText));
        var called = Directory.GetFiles(Web, "*.js").SelectMany(f => RpcCall().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value)).Distinct().ToList();
        Assert.NotEmpty(called);
        foreach (var function in called)
        {
            Assert.StartsWith("owner_", function);
            Assert.Contains($"function public.{function}(", defined);
            Assert.Contains($"grant execute on function public.{function}(", defined);
        }
    }

    [Fact]
    public void PagesWorkflow_PublishesWebDirectory()
    {
        var workflow = File.ReadAllText(Path.Combine(Root, ".github", "workflows", "pages.yml"));

        Assert.Contains("path: web", workflow);
        Assert.Contains("tags: ['v*']", workflow);
        Assert.Contains("workflow_dispatch", workflow);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Glebosaurus.slnx")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Nie znaleziono katalogu repozytorium (Glebosaurus.slnx).");
    }

    [GeneratedRegex("<meta http-equiv=\"Content-Security-Policy\" content=\"([^\"]+)\">")]
    private static partial Regex CspMeta();

    [GeneratedRegex("from '([^']+)'")]
    private static partial Regex ImportFrom();

    [GeneratedRegex("REQUIRED_MIGRATION = '([0-9]{14})'")]
    private static partial Regex RequiredMigration();

    [GeneratedRegex("\\.rpc\\('([a-z_]+)'")]
    private static partial Regex RpcCall();
}
