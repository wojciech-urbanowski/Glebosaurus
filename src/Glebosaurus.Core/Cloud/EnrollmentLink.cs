using Glebosaurus.Core.Config;

namespace Glebosaurus.Core.Cloud;

/// <summary>
/// Rejestracja agenta jednym kodem QR (agent Android, ARCHITEKTURA.md, zasada 4): adres projektu Supabase, klucz
/// publikowalny i jednorazowy kod rejestracji w odnośniku <c>glebosaurus://enroll?url=…&amp;key=…&amp;code=…</c>. Na
/// telefonie nie trzeba więc przepisywać długiego klucza. Klucz publikowalny nie jest sekretem (rola <c>anon</c>, same
/// funkcje agentów); kod rejestracji jest – jednorazowy i ważny 24 h, więc nie trafia do <see cref="ToString"/>.
/// </summary>
public sealed record EnrollmentLink(Uri ProjectUrl, string PublishableKey, string Code)
{
    public const string Scheme = "glebosaurus";
    public const string Prefix = Scheme + "://enroll?";

    /// <summary>Odnośnik do kodu QR (i do wklejenia, gdy skaner tylko pokazuje tekst).</summary>
    public string ToUri() =>
        $"{Prefix}url={Uri.EscapeDataString(ProjectUrl.GetLeftPart(UriPartial.Authority))}" +
        $"&key={Uri.EscapeDataString(PublishableKey)}&code={Uri.EscapeDataString(Code)}";

    /// <summary>Sekcja <c>cloud</c> agenta z tego odnośnika.</summary>
    public CloudAccessConfig ToCloudAccess() => new() { Url = ProjectUrl.GetLeftPart(UriPartial.Authority), PublishableKey = PublishableKey };

    public override string ToString() => $"EnrollmentLink {{ Project = {ProjectUrl.Host} }}";

    /// <summary>Tworzy odnośnik i sprawdza go tymi samymi zasadami co sekcja <c>cloud</c> w pliku konfiguracji.</summary>
    /// <exception cref="InvalidDataException">Zły adres, klucz tajny zamiast publikowalnego albo pusty kod.</exception>
    public static EnrollmentLink Create(string projectUrl, string publishableKey, string code)
    {
        var access = new CloudAccessConfig { Url = projectUrl.Trim().TrimEnd('/'), PublishableKey = publishableKey.Trim() };
        AgentConfigLoader.Validate(new AgentConfig { Cloud = access });
        var trimmed = code.Trim();
        if (trimmed.Length is 0 or > 200 || trimmed.Any(char.IsWhiteSpace))
            throw new InvalidDataException("Kod rejestracji jest pusty albo ma niedozwolone znaki.");
        return new(access.ProjectUrl, access.PublishableKey, trimmed);
    }

    /// <summary>Odczytuje odnośnik zeskanowany z kodu QR albo wklejony (białe znaki na brzegach są pomijane).</summary>
    /// <exception cref="InvalidDataException">To nie jest odnośnik rejestracji Glebosaurusa albo ma złe dane.</exception>
    public static EnrollmentLink Parse(string text)
    {
        var value = text.Trim();
        if (!value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("To nie jest kod rejestracji Glebosaurusa (oczekiwano odnośnika glebosaurus://enroll?…).");
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in value[Prefix.Length..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0) continue;
            fields[part[..separator]] = Uri.UnescapeDataString(part[(separator + 1)..]);
        }
        if (!fields.TryGetValue("url", out var url) || !fields.TryGetValue("key", out var key) || !fields.TryGetValue("code", out var code))
            throw new InvalidDataException("Kod rejestracji jest niepełny (brak adresu projektu, klucza albo kodu).");
        return Create(url, key, code);
    }
}
