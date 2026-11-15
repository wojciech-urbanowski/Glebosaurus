using System.Globalization;
using System.Text.Json;

namespace Glebosaurus.Core.Updates;

/// <summary>Numery wersji wydań: „v0.1.0”, „0.1.0”, „0.1.0+abc123” → 0.1.0. Wersje przedpremierowe („0.2.0-rc1”) są odrzucane.</summary>
public static class ReleaseVersion
{
    public static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var value = text.Trim();
        if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];
        var plus = value.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0) value = value[..plus];
        var parts = value.Split('.');
        if (parts.Length != 3) return false;
        var numbers = new int[3];
        for (var i = 0; i < 3; i++)
            if (parts[i].Length == 0 || !parts[i].All(char.IsAsciiDigit)
                || !int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
                return false;
        version = new Version(numbers[0], numbers[1], numbers[2]);
        return true;
    }

    public static string Format(Version version) => version.ToString(3);
}

/// <summary>Nazwy plików wydania – te same tworzy <c>deploy/package-windows.ps1</c> i czyta <c>deploy/install-windows.ps1</c>.</summary>
public static class ReleaseAssets
{
    /// <summary>Sumy SHA-256 wszystkich plików wydania w formacie <c>sha256sum</c>.</summary>
    public const string Checksums = "SHA256SUMS";

    /// <summary>Skrypt instalacji i aktualizacji na Windows.</summary>
    public const string WindowsInstaller = "install-windows.ps1";

    /// <summary>Paczka samodzielna (z .NET) dla Windows x64.</summary>
    public static string WindowsPackage(Version version) => $"Glebosaurus-{ReleaseVersion.Format(version)}-win-x64.zip";
}

public sealed record ReleaseAsset(string Name, Uri DownloadUrl);

/// <summary>Wydanie z GitHuba – tylko to, czego potrzebuje aktualizacja (odpowiedź <c>GET /repos/{repo}/releases/latest</c>).</summary>
public sealed record ReleaseInfo(string Tag, Version Version, bool IsDraft, bool IsPrerelease, IReadOnlyList<ReleaseAsset> Assets)
{
    public ReleaseAsset? FindAsset(string name) => Assets.FirstOrDefault(asset => string.Equals(asset.Name, name, StringComparison.Ordinal));

    /// <exception cref="InvalidDataException">Odpowiedź nie jest opisem wydania albo znacznik nie jest numerem wersji.</exception>
    public static ReleaseInfo Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!ReleaseVersion.TryParse(tag, out var version))
                throw new InvalidDataException($"Znacznik wydania „{tag}” nie jest numerem wersji (oczekiwano np. v0.1.0).");
            var assets = new List<ReleaseAsset>();
            if (root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray())
                {
                    if (item.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } fileName
                        && item.TryGetProperty("browser_download_url", out var url)
                        && Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri))
                        assets.Add(new(fileName, uri));
                }
            }
            return new(tag, version, Flag(root, "draft"), Flag(root, "prerelease"), assets);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidDataException($"Nieczytelny opis wydania z GitHuba: {e.Message}", e);
        }
    }

    private static bool Flag(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
