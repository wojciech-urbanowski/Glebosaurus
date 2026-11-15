using Glebosaurus.Core.Updates;

namespace Glebosaurus.Service;

/// <summary>
/// Układ katalogu instalacji (domyślnie <c>%LOCALAPPDATA%\Glebosaurus</c>, tworzy go <c>install-windows.ps1</c>):
/// <c>app\</c> – program (zawsze ta sama ścieżka, więc jedna reguła Zapory dla panelu WWW wystarcza na wszystkie wersje),
/// <c>data\</c> – konfiguracja, klucze czujników, baza, dzienniki, sekrety panelu; <c>updates\</c> – pobrane wydania.
/// </summary>
internal sealed record ServicePaths(string Root)
{
    public string App => Path.Combine(Root, "app");
    public string Data => Path.Combine(Root, "data");
    public string Updates => Path.Combine(Root, "updates");
    public string Logs => Path.Combine(Data, "logs");
    public string Config => Path.Combine(Data, "glebosaurus.config.json");
    public string MonitorExecutable => Path.Combine(App, "glebosaurus.exe");
    public string Settings => Path.Combine(Root, InstallSettings.FileName);
    public string UpdateAttempt => Path.Combine(Updates, Core.Updates.UpdateAttempt.FileName);

    /// <summary>Usługa leży w <c>app\</c>, więc katalog instalacji to katalog wyżej.</summary>
    public static ServicePaths FromExecutable() => new(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..")));
}
