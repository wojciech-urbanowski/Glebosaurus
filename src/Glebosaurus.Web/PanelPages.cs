namespace Glebosaurus.Web;

/// <summary>Strona logowania, składana na serwerze (bez skryptów). Reszta panelu to statyczna strona z danymi z API.</summary>
internal static class PanelPages
{
    public static string Login(string monitorName, string? message, bool isError) =>
        $$"""
        <!doctype html>
        <html lang="pl">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <meta name="referrer" content="same-origin">
        <title>Glebosaurus – logowanie</title>
        <link rel="icon" href="/favicon.svg" type="image/svg+xml">
        <link rel="stylesheet" href="/app.css">
        </head>
        <body>
        <main class="login">
          <div class="brand">GLEBOSAURUS</div>
          <h1>Panel monitora</h1>
          <p class="muted">{{Enc(monitorName)}}</p>
          {{(message is null ? "" : $"<p class=\"banner {(isError ? "tone-red" : "tone-green")}\" role=\"alert\">{Enc(message)}</p>")}}
          <form method="post" action="/login" class="card">
            <input type="text" name="username" value="glebosaurus" autocomplete="username" hidden readonly>
            <label for="password">Hasło</label>
            <input id="password" name="password" type="password" autocomplete="current-password" required autofocus maxlength="1024">
            <button type="submit">Zaloguj</button>
          </form>
          <p class="muted small">Hasło ustawia się na komputerze monitora poleceniem <code>glebosaurus web-password</code>.</p>
        </main>
        </body>
        </html>
        """;

    /// <summary>Koduje tylko znaki specjalne HTML (jak w mailach) – polskie litery zostają czytelne, strona jest w UTF-8.</summary>
    private static string Enc(string text) => text
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("'", "&#39;", StringComparison.Ordinal);
}
