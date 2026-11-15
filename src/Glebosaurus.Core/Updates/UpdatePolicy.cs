using System.Globalization;
using System.Text.Json;

namespace Glebosaurus.Core.Updates;

/// <summary>Kompletne wydanie, na które warto się zaktualizować: paczka, skrypt instalacji i sumy kontrolne.</summary>
public sealed record UpdateOffer(ReleaseInfo Release, ReleaseAsset Package, ReleaseAsset Installer, ReleaseAsset Checksums)
{
    public Version Version => Release.Version;
}

public enum UpdateVerdict
{
    /// <summary>Zainstalowana wersja jest najnowsza (albo nowsza niż wydanie).</summary>
    UpToDate,
    /// <summary>Jest nowsza wersja z kompletem plików.</summary>
    Available,
    /// <summary>Nowsza wersja niedawno nie dała się zainstalować (albo próba się nie skończyła) – kolejna próba później.</summary>
    Postponed,
    /// <summary>Nowsze wydanie nie ma wszystkich potrzebnych plików (np. jeszcze się publikuje).</summary>
    Incomplete
}

public sealed record UpdateDecision(UpdateVerdict Verdict, string Reason, UpdateOffer? Offer = null);

/// <summary>
/// Ślad niedokończonej aktualizacji bez nadzoru (plik <c>update-attempt.json</c> w katalogu <c>updates</c>). Usługa zapisuje
/// go tuż przed przekazaniem aktualizacji skryptowi <c>install-windows.ps1</c>; skrypt usuwa go po sukcesie, a przy porażce
/// dopisuje przyczynę. Bez niego zepsute wydanie albo skrypt, który w ogóle nie ruszył, dawałyby pętlę: usługa przekazuje
/// aktualizację, ta się nie udaje, usługa wraca i znów widzi to samo wydanie – a monitor przez cały czas stoi.
/// </summary>
public sealed record UpdateAttempt(Version Version, DateTimeOffset At, string? Reason)
{
    public const string FileName = "update-attempt.json";

    /// <summary>Null, gdy pliku nie ma albo jest nieczytelny (wtedy po prostu próbujemy ponownie).</summary>
    public static UpdateAttempt? Load(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            return ReleaseVersion.TryParse(Text(root, "version"), out var version)
                && DateTimeOffset.TryParse(Text(root, "at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                ? new UpdateAttempt(version, at, Text(root, "reason"))
                : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteString("version", ReleaseVersion.Format(Version));
        writer.WriteString("at", At.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
        if (Reason is not null) writer.WriteString("reason", Reason);
        writer.WriteEndObject();
    }

    private static string? Text(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in root.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                return property.Value.GetString();
        return null;
    }
}

public static class UpdatePolicy
{
    /// <summary>Po nieudanej (albo niedokończonej) aktualizacji ta sama wersja czeka tyle, zanim usługa spróbuje znowu.</summary>
    public static readonly TimeSpan RetryFailedAfter = TimeSpan.FromHours(24);

    public static UpdateDecision Decide(Version installed, ReleaseInfo latest, UpdateAttempt? lastAttempt, DateTimeOffset now)
    {
        var target = ReleaseVersion.Format(latest.Version);
        if (latest.IsDraft || latest.IsPrerelease)
            return new(UpdateVerdict.UpToDate, $"Wydanie {latest.Tag} jest robocze albo przedpremierowe – pomijam.");
        if (latest.Version <= installed)
            return new(UpdateVerdict.UpToDate, $"Zainstalowana wersja {ReleaseVersion.Format(installed)} jest aktualna (najnowsze wydanie: {target}).");
        if (lastAttempt is { } attempt && attempt.Version == latest.Version && now - attempt.At < RetryFailedAfter)
            return new(UpdateVerdict.Postponed,
                $"Aktualizacja do {target} z {attempt.At.ToLocalTime():yyyy-MM-dd HH:mm} się nie udała ({attempt.Reason ?? "skrypt jej nie potwierdził"}); " +
                $"kolejna próba po {(attempt.At + RetryFailedAfter).ToLocalTime():yyyy-MM-dd HH:mm}.");

        var packageName = ReleaseAssets.WindowsPackage(latest.Version);
        var package = latest.FindAsset(packageName);
        var installer = latest.FindAsset(ReleaseAssets.WindowsInstaller);
        var checksums = latest.FindAsset(ReleaseAssets.Checksums);
        if (package is null || installer is null || checksums is null)
        {
            var missing = new[] { (package, packageName), (installer, ReleaseAssets.WindowsInstaller), (checksums, ReleaseAssets.Checksums) }
                .Where(pair => pair.Item1 is null).Select(pair => pair.Item2);
            return new(UpdateVerdict.Incomplete, $"Wydanie {target} nie ma plików: {string.Join(", ", missing)}.");
        }
        return new(UpdateVerdict.Available, $"Dostępna nowa wersja {target} (zainstalowana: {ReleaseVersion.Format(installed)}).",
            new UpdateOffer(latest, package, installer, checksums));
    }
}
