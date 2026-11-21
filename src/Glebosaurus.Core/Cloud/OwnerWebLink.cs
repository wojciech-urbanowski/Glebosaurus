using Glebosaurus.Core.Config;

namespace Glebosaurus.Core.Cloud;

/// <summary>
/// Odnośnik do strony właściciela w chmurze (faza 3, ARCHITEKTURA.md, zasada 10): adres strony i – we fragmencie – adres
/// projektu Supabase i klucz publikowalny: <c>https://…/Glebosaurus/#url=…&amp;key=…</c>. Repozytorium jest publiczne, więc
/// strona nie ma adresu projektu w kodzie; dostaje go z tego odnośnika (fragment nie trafia do serwera strony) i
/// zapamiętuje w przeglądarce. Klucz publikowalny nie jest sekretem – dostęp chroni logowanie z TOTP i lista właścicieli.
/// </summary>
public sealed record OwnerWebLink(Uri Page, Uri ProjectUrl, string PublishableKey)
{
    /// <summary>Strona publikowana z tego repozytorium na GitHub Pages (<c>.github/workflows/pages.yml</c>).</summary>
    public const string DefaultPage = "https://wojciech-urbanowski.github.io/Glebosaurus/";

    public string ToUri() =>
        $"{Page.GetLeftPart(UriPartial.Path)}#url={Uri.EscapeDataString(ProjectUrl.GetLeftPart(UriPartial.Authority))}" +
        $"&key={Uri.EscapeDataString(PublishableKey)}";

    /// <summary>Sprawdza adres projektu i klucz tymi samymi zasadami co sekcja <c>cloud</c> pliku agenta; strona – HTTPS
    /// (albo HTTP na tym komputerze, do podglądu).</summary>
    /// <exception cref="InvalidDataException">Zły adres strony albo projektu, klucz tajny zamiast publikowalnego.</exception>
    public static OwnerWebLink Create(string page, string projectUrl, string publishableKey)
    {
        if (!Uri.TryCreate(page.Trim(), UriKind.Absolute, out var pageUri)
            || !(pageUri.Scheme == Uri.UriSchemeHttps || (pageUri.Scheme == Uri.UriSchemeHttp && pageUri.IsLoopback))
            || pageUri.Query.Length > 0 || pageUri.Fragment.Length > 0 || pageUri.UserInfo.Length > 0)
            throw new InvalidDataException("Adres strony: https://… bez parametrów (http tylko na tym komputerze, do podglądu).");
        var access = new CloudAccessConfig { Url = projectUrl.Trim().TrimEnd('/'), PublishableKey = publishableKey.Trim() };
        AgentConfigLoader.Validate(new AgentConfig { Cloud = access });
        return new(pageUri, access.ProjectUrl, access.PublishableKey);
    }
}
