using System.Text.Json;
using Glebosaurus.Core.Reports;

namespace Glebosaurus.Core.Updates;

/// <summary>
/// Ustawienia instalacji usługi na Windows: plik <c>install.json</c> w katalogu instalacji. Pisze go
/// <c>install-windows.ps1</c> (zapamiętuje swoje parametry na kolejne aktualizacje), czyta usługa. Brak pliku = domyślne.
/// </summary>
public sealed class InstallSettings
{
    public const string FileName = "install.json";
    public const string DefaultApiBase = "https://api.github.com";
    public const string DefaultTaskName = "Glebosaurus";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>Repozytorium GitHuba z wydaniami (właściciel/nazwa).</summary>
    public string Repository { get; set; } = AppInfo.Repository;

    /// <summary>Adres API GitHuba; inny tylko do prób (lokalny serwer z udawanym wydaniem).</summary>
    public string ApiBase { get; set; } = DefaultApiBase;

    /// <summary>Nazwa zadania Harmonogramu, które uruchamia usługę przy zalogowaniu.</summary>
    public string TaskName { get; set; } = DefaultTaskName;

    /// <summary>Czy usługa sama sprawdza i instaluje nowe wydania.</summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>Codzienna pora sprawdzania wydań (HH:mm, czas lokalny); puste = 03:30.</summary>
    public string? DailyUpdateCheck { get; set; }

    public TimeOnly DailyCheckTime => ReportSchedule.TryParseTime(DailyUpdateCheck ?? "", out var time) ? time : UpdateCheckSchedule.DefaultDailyAt;

    /// <exception cref="InvalidDataException">Plik istnieje, ale jest nieczytelny albo ma złe wartości.</exception>
    public static InstallSettings Load(string path)
    {
        if (!File.Exists(path)) return new InstallSettings();
        InstallSettings settings;
        try
        {
            settings = JsonSerializer.Deserialize<InstallSettings>(File.ReadAllText(path), JsonOptions) ?? new InstallSettings();
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"Nieprawidłowy JSON w {path}: {e.Message}", e);
        }
        if (string.IsNullOrWhiteSpace(settings.Repository) || settings.Repository.Count(c => c == '/') != 1)
            throw new InvalidDataException($"{path}: repository musi mieć postać właściciel/nazwa.");
        if (!Uri.TryCreate(settings.ApiBase, UriKind.Absolute, out var api) || api.Scheme is not ("https" or "http"))
            throw new InvalidDataException($"{path}: apiBase musi być adresem http(s).");
        if (!string.IsNullOrWhiteSpace(settings.DailyUpdateCheck) && !ReportSchedule.TryParseTime(settings.DailyUpdateCheck, out _))
            throw new InvalidDataException($"{path}: dailyUpdateCheck „{settings.DailyUpdateCheck}” nie jest godziną HH:mm.");
        if (string.IsNullOrWhiteSpace(settings.TaskName)) settings.TaskName = DefaultTaskName;
        return settings;
    }

    /// <summary>Adres opisu najnowszego wydania (GitHub pomija wydania robocze i przedpremierowe).</summary>
    public Uri LatestReleaseUri => new($"{ApiBase.TrimEnd('/')}/repos/{Repository}/releases/latest");
}
