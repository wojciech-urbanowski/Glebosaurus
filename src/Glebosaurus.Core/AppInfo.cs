using System.Reflection;

namespace Glebosaurus.Core;

/// <summary>Informacje o samej aplikacji. Wersja pochodzi z <c>Directory.Build.props</c> (jedno źródło dla wszystkich projektów).</summary>
public static class AppInfo
{
    /// <summary>Numer wersji bez sufiksu z identyfikatorem commita (np. „0.3.3”).</summary>
    public static string Version { get; } = Resolve();

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
