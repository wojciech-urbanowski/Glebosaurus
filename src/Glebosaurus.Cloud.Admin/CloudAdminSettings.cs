using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

namespace Glebosaurus.Cloud.Admin;

/// <summary>
/// Dane administratora projektu Supabase z pliku administratora (wzór: <c>examples/cloud-admin.json</c>). Zawiera
/// sekrety, więc <see cref="ToString"/> pokazuje tylko id projektu.
/// </summary>
public sealed partial class CloudAdminSettings
{
    /// <summary>Pooler sesji Supabase dla regionu Central EU (Frankfurt) – sprawdzony 2026-11-16 (IPv4; połączenie
    /// bezpośrednie db.&lt;id&gt;.supabase.co ma tylko IPv6).</summary>
    public const string DefaultDatabaseHost = "aws-1-eu-central-1.pooler.supabase.com";

    public required Uri Url { get; init; }
    public required string ProjectRef { get; init; }
    public string? PublishableKey { get; init; }
    public string? SecretKey { get; init; }
    public required string DatabasePassword { get; init; }
    public string DatabaseHost { get; init; } = DefaultDatabaseHost;

    public override string ToString() => $"projekt Supabase {ProjectRef}";

    public static CloudAdminSettings Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (!document.RootElement.TryGetProperty("supabase", out var supabase) || supabase.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{Path.GetFileName(path)}: brak sekcji „supabase”.");

        string? Text(string name) =>
            supabase.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
                ? text.Trim() : null;

        var url = Text("url") ?? throw new InvalidDataException($"{Path.GetFileName(path)}: brak supabase.url.");
        if (!Uri.TryCreate(url.TrimEnd('/'), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || ProjectHost().Match(uri.Host) is not { Success: true } match || uri.AbsolutePath != "/")
            throw new InvalidDataException($"{Path.GetFileName(path)}: supabase.url musi mieć postać https://<id projektu>.supabase.co.");

        return new CloudAdminSettings
        {
            Url = uri,
            ProjectRef = match.Groups[1].Value,
            PublishableKey = Text("publishableKey"),
            SecretKey = Text("secretKey"),
            DatabasePassword = Text("databasePassword") ?? throw new InvalidDataException($"{Path.GetFileName(path)}: brak supabase.databasePassword."),
            DatabaseHost = Text("databaseHost") ?? DefaultDatabaseHost
        };
    }

    /// <summary>
    /// Źródło połączeń z bazą jako <c>postgres</c> przez pooler sesji, zawsze z pełnym sprawdzeniem serwera (VerifyFull)
    /// względem certyfikatu głównego Supabase wbudowanego w bibliotekę – serwer ma certyfikat od własnego CA Supabase,
    /// którego systemowy magazyn nie zna, a hasła bazy nie wolno wysłać serwerowi, którego tożsamości nie sprawdzono.
    /// </summary>
    public NpgsqlDataSource CreateDataSource(string applicationName = "glebosaurus-admin")
    {
        var builder = new NpgsqlDataSourceBuilder(new NpgsqlConnectionStringBuilder
        {
            Host = DatabaseHost,
            Port = 5432,
            Username = "postgres." + ProjectRef,
            Password = DatabasePassword,
            Database = "postgres",
            SslMode = SslMode.VerifyFull,
            Timeout = 15,
            CommandTimeout = 60,
            ApplicationName = applicationName,
            MaxPoolSize = 4
        }.ConnectionString);
        builder.UseRootCertificate(SupabaseRootCertificate.Load());
        return builder.Build();
    }

    [GeneratedRegex("^([a-z0-9]{20})\\.supabase\\.co$")]
    private static partial Regex ProjectHost();
}
