using Microsoft.AspNetCore.Http;

namespace Glebosaurus.Web;

/// <summary>Pliki strony panelu wbudowane w bibliotekę (wwwroot/*), wczytane raz przy starcie.</summary>
internal sealed class PanelAssets(IReadOnlyDictionary<string, (byte[] Content, string ContentType)> files)
{
    /// <summary>Strony – pod własnymi adresami („/” i „/sensor”).</summary>
    public static readonly string[] Pages = ["index.html", "sensor.html"];

    /// <summary>Skrypty (moduły ES), arkusz stylów i ikona – pod adresem „/nazwa”.</summary>
    public static readonly string[] Files = ["format.js", "common.js", "charts.js", "tile-order.js", "app.js", "sensor.js", "app.css", "favicon.svg"];

    public static PanelAssets Load()
    {
        var assembly = typeof(PanelAssets).Assembly;
        var files = new Dictionary<string, (byte[], string)>(StringComparer.Ordinal);
        foreach (var name in Pages.Concat(Files))
        {
            using var stream = assembly.GetManifestResourceStream("wwwroot/" + name)
                ?? throw new InvalidOperationException($"Brak wbudowanego pliku panelu wwwroot/{name}.");
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            files[name] = (buffer.ToArray(), ContentType(name));
        }
        return new(files);
    }

    public IResult Result(string name) => Results.Bytes(files[name].Content, files[name].ContentType);

    private static string ContentType(string name) => Path.GetExtension(name) switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".svg" => "image/svg+xml",
        _ => "application/octet-stream"
    };
}
