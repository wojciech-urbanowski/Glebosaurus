using System.Reflection;
using Glebosaurus.Core.Updates;

namespace Glebosaurus.Core;

/// <summary>Informacje o samej aplikacji. Wersja pochodzi z <c>Directory.Build.props</c> (jedno źródło dla wszystkich projektów).</summary>
public static class AppInfo
{
    /// <summary>Repozytorium na GitHubie, z którego wydań instaluje się i aktualizuje usługa (właściciel/nazwa).</summary>
    public const string Repository = "wojciech-urbanowski/Glebosaurus";

    /// <summary>Numer wersji bez sufiksu z identyfikatorem commita (np. „0.3.3”).</summary>
    public static string Version { get; } = Resolve();

    /// <summary>Ta sama wersja do porównań z wydaniami (major.minor.patch).</summary>
    public static Version ParsedVersion { get; } =
        ReleaseVersion.TryParse(Version, out var parsed) ? parsed : new Version(0, 0, 0);

    private static string Resolve()
    {
        var assembly = typeof(AppInfo).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3)
            ?? "?";
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? version[..plus] : version;
    }
}
