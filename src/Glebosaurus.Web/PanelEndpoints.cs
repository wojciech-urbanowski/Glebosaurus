using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Glebosaurus.Core.Charts;
using Glebosaurus.Core.Logging;
using Glebosaurus.Core.Monitoring;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Glebosaurus.Web;

/// <summary>
/// Potok żądań panelu: filtr sieci → nagłówki bezpieczeństwa i blokada POST z obcych stron (a dla akcji API – wymagany
/// własny nagłówek) → sesja → strony i API.
/// </summary>
internal sealed class PanelEndpoints(WebPanelOptions options, ILogger logger)
{
    public const string CookieName = "__Host-glebosaurus";

    /// <summary>
    /// Nagłówek wymagany przy akcjach (POST /api/…). Formularz z obcej strony nie umie go dodać, a skrypt z obcej strony
    /// musiałby przejść zapytanie wstępne CORS, którego panel nie przepuszcza – trzecia warstwa obok SameSite=Strict i Sec-Fetch-Site.
    /// </summary>
    public const string ActionHeader = "X-Glebosaurus-Action";

    /// <summary>Ścieżki dostępne bez logowania (strona logowania i jej wygląd).</summary>
    private static readonly HashSet<string> PublicPaths = new(StringComparer.OrdinalIgnoreCase) { "/login", "/app.css", "/favicon.svg" };

    private const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self'; connect-src 'self'; " +
        "form-action 'self'; frame-ancestors 'none'; base-uri 'none'";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly NetworkAllowList allowList = new(options.Config.ParseAllowedNetworks());
    private readonly SessionStore sessions = new(TimeSpan.FromHours(options.Config.SessionHours));
    private readonly LoginThrottle throttle = new();

    /// <summary>Jedno liczenie PBKDF2 naraz – zalew prób logowania nie zajmie całego procesora (na Raspberry Pi to ważne).</summary>
    private readonly SemaphoreSlim hashing = new(1, 1);

    private readonly ConcurrentDictionary<IPAddress, DateTimeOffset> deniedLogged = new();
    private readonly PanelAssets assets = PanelAssets.Load();

    private DateTimeOffset Now => options.Clock.GetUtcNow();

    public void Map(WebApplication app)
    {
        app.Use(FilterNetworkAsync);
        app.Use(GuardAsync);
        app.Use(RequireSessionAsync);

        app.MapGet("/login", LoginPage);
        app.MapPost("/login", (Delegate)LoginAsync);
        app.MapPost("/logout", Logout);

        app.MapGet("/", () => assets.Result("index.html"));
        app.MapGet("/sensor", () => assets.Result("sensor.html"));
        foreach (var name in PanelAssets.Files) app.MapGet("/" + name, () => assets.Result(name));

        app.MapGet("/api/status", async (CancellationToken ct) => Results.Json(await options.Status(ct), Json));
        app.MapGet("/api/sensors/{id}/readings", async (string id, int? count, CancellationToken ct) =>
            await options.Readings(id, Math.Clamp(count ?? 96, 1, 1000), ct) is { } readings ? Results.Json(readings, Json) : Results.NotFound());
        app.MapGet("/api/sensors/{id}/series", ReadSeriesAsync);
        app.MapGet("/api/log", ReadLogAsync);

        app.MapPost("/api/sensors/{id}/read", async (string id, HttpContext context) =>
        {
            var result = await RequestReadAsync(context, [id]);
            return result.Items is [{ Status: ReadRequestStatus.Unknown }] ? Results.NotFound() : Results.Json(result, Json);
        });
        // Rzutowanie na Delegate: lambda z samym HttpContext pasowałaby do RequestDelegate i wynik (IResult) by przepadł.
        app.MapPost("/api/read-all", (Delegate)(async (HttpContext context) => Results.Json(await RequestReadAsync(context, null), Json)));
        app.MapPost("/api/report", (Delegate)(async (HttpContext context) => Results.Json(await RequestReportAsync(context), Json)));
    }

    /// <summary>Zleca raport mailowy (polecenie przez chmurę, z odstępem) i odnotowuje, kto to zrobił.</summary>
    private async Task<ReportRequestResult> RequestReportAsync(HttpContext context)
    {
        var result = await options.RequestReport(context.RequestAborted);
        logger.LogInformation("Panel WWW: raport na żądanie z {Address} – {Status}.", Client(context), result.Status switch
        {
            ReportRequestStatus.Queued => "zlecony",
            ReportRequestStatus.AlreadyQueued => "już czekał",
            ReportRequestStatus.TooSoon => "za wcześnie",
            _ => "chmura niedostępna"
        });
        return result;
    }

    /// <summary>Zleca odczyt na żądanie (polecenie przez chmurę) i odnotowuje, kto to zrobił.</summary>
    private async Task<ReadRequestResult> RequestReadAsync(HttpContext context, IReadOnlyCollection<string>? sensorIds)
    {
        var result = await options.RequestRead(sensorIds, context.RequestAborted);
        logger.LogInformation("Panel WWW: odczyt na żądanie z {Address}: {Items}.", Client(context),
            string.Join(", ", result.Items.Select(i => $"{i.SensorName ?? i.SensorId} – {StatusText(i.Status)}")));
        return result;
    }

    private static string StatusText(ReadRequestStatus status) => status switch
    {
        ReadRequestStatus.Queued => "zlecony",
        ReadRequestStatus.AlreadyQueued => "już czekał",
        ReadRequestStatus.Unavailable => "chmura niedostępna",
        _ => "nieznany czujnik"
    };

    /// <summary>Adres klienta do dziennika: IPv4 zamiast zapisu „::ffff:192.168.1.20”.</summary>
    private static IPAddress? Client(HttpContext context) =>
        context.Connection.RemoteIpAddress is { IsIPv4MappedToIPv6: true } mapped ? mapped.MapToIPv4() : context.Connection.RemoteIpAddress;

    // ---- warstwy ochrony ----------------------------------------------------------------------------------------

    private async Task FilterNetworkAsync(HttpContext context, RequestDelegate next)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote is not null && allowList.IsAllowed(remote))
        {
            await next(context);
            return;
        }
        // Raz na godzinę na adres, żeby skanowanie z zewnątrz nie zalało dziennika.
        if (remote is not null && (!deniedLogged.TryGetValue(remote, out var logged) || Now - logged > TimeSpan.FromHours(1)))
        {
            if (deniedLogged.Count > 1000) deniedLogged.Clear();
            deniedLogged[remote] = Now;
            logger.LogWarning("Panel WWW: odrzucono połączenie spoza dozwolonych sieci ({Address}).", remote);
        }
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
    }

    /// <summary>Nagłówki bezpieczeństwa na każdej odpowiedzi; POST tylko z samego panelu (ochrona przed CSRF obok SameSite=Strict).</summary>
    private Task GuardAsync(HttpContext context, RequestDelegate next)
    {
        var headers = context.Response.Headers;
        headers.ContentSecurityPolicy = ContentSecurityPolicy;
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers.CacheControl = "no-store";
        // same-origin, nie no-referrer: przy no-referrer przeglądarka wysyła z formularza Origin: null.
        headers["Referrer-Policy"] = "same-origin";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";

        if (HttpMethods.IsPost(context.Request.Method) && IsCrossSite(context.Request))
        {
            logger.LogWarning("Panel WWW: odrzucono {Method} {Path} z {Address} jako wysłane z innej strony (Origin: {Origin}, Sec-Fetch-Site: {Site}).",
                context.Request.Method, context.Request.Path.Value, Client(context),
                context.Request.Headers.Origin.ToString() is { Length: > 0 } origin ? origin : "brak",
                context.Request.Headers["Sec-Fetch-Site"].ToString() is { Length: > 0 } site ? site : "brak");
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }
        if (HttpMethods.IsPost(context.Request.Method) && context.Request.Path.StartsWithSegments("/api")
            && context.Request.Headers[ActionHeader].ToString() != "1")
        {
            logger.LogWarning("Panel WWW: odrzucono {Method} {Path} z {Address} – brak nagłówka {Header}.",
                context.Request.Method, context.Request.Path.Value, Client(context), ActionHeader);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }
        return next(context);
    }

    /// <summary>
    /// Czy żądanie przyszło z innej strony. Rozstrzyga <c>Sec-Fetch-Site</c> (ustawia go przeglądarka, strona nie może go
    /// podrobić); bez niego (starsze przeglądarki) – <c>Origin</c>. Samemu <c>Origin</c> nie można ufać w drugą stronę:
    /// przy restrykcyjnej polityce referrera przeglądarka wysyła z formularza <c>Origin: null</c> nawet z tej samej strony.
    /// </summary>
    internal static bool IsCrossSite(HttpRequest request)
    {
        var site = request.Headers["Sec-Fetch-Site"].ToString();
        if (site.Length > 0) return site is not ("same-origin" or "none");
        var origin = request.Headers.Origin.ToString();
        return origin.Length > 0 && !string.Equals(origin, $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase);
    }

    private Task RequireSessionAsync(HttpContext context, RequestDelegate next)
    {
        if (PublicPaths.Contains(context.Request.Path.Value ?? "") || HasSession(context)) return next(context);
        if (context.Request.Path.StartsWithSegments("/api")) context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        else context.Response.Redirect("/login");
        return Task.CompletedTask;
    }

    private bool HasSession(HttpContext context) => sessions.IsValid(context.Request.Cookies[CookieName], Now);

    // ---- logowanie ----------------------------------------------------------------------------------------------

    private IResult LoginPage(HttpContext context, string? blad, string? wylogowano)
    {
        if (HasSession(context)) return Results.Redirect("/");
        var blocked = throttle.BlockedUntil(context.Connection.RemoteIpAddress!, Now);
        var (message, isError) = blocked is { } until ? (BlockedMessage(until), true)
            : blad is not null ? ("Nieprawidłowe hasło.", true)
            : wylogowano is not null ? ("Wylogowano.", false)
            : ((string?)null, false);
        return Html(PanelPages.Login(options.MonitorName, message, isError));
    }

    private async Task<IResult> LoginAsync(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress!;
        if (throttle.BlockedUntil(remote, Now) is { } until)
            return Html(PanelPages.Login(options.MonitorName, BlockedMessage(until), true), StatusCodes.Status429TooManyRequests);
        if (!context.Request.HasFormContentType) return Results.BadRequest();

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var password = form["password"].ToString();
        bool valid;
        await hashing.WaitAsync(context.RequestAborted);
        try { valid = password.Length is > 0 and <= 1024 && PanelPassword.Verify(password, options.PasswordHash); }
        finally { hashing.Release(); }

        if (!valid)
        {
            throttle.Failure(remote, Now);
            if (throttle.BlockedUntil(remote, Now) is not null)
                logger.LogWarning("Panel WWW: błędne hasło z {Address} – adres zablokowany na {Minutes} min.", Client(context), LoginThrottle.BlockFor.TotalMinutes);
            else
                logger.LogWarning("Panel WWW: błędne hasło z {Address}.", Client(context));
            return Results.Redirect("/login?blad=1");
        }

        throttle.Success(remote);
        context.Response.Cookies.Append(CookieName, sessions.Create(Now), new CookieOptions
        {
            Secure = true, HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/", MaxAge = sessions.Lifetime, IsEssential = true
        });
        logger.LogInformation("Panel WWW: zalogowano z {Address}.", Client(context));
        return Results.Redirect("/");
    }

    private IResult Logout(HttpContext context)
    {
        sessions.Remove(context.Request.Cookies[CookieName]);
        context.Response.Cookies.Delete(CookieName, new CookieOptions { Secure = true, HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/" });
        return Results.Redirect("/login?wylogowano=1");
    }

    private string BlockedMessage(DateTimeOffset until) =>
        $"Za dużo nieudanych prób. Spróbuj ponownie po {TimeZoneInfo.ConvertTime(until, options.Zone):HH:mm}.";

    // ---- dane ---------------------------------------------------------------------------------------------------

    /// <summary>Domyślne okno wykresu (gdy nie podano „from”): ostatnie 3 doby, jak na pulpicie.</summary>
    public static readonly TimeSpan DefaultSeriesWindow = TimeSpan.FromHours(72);

    /// <summary>Najdłuższe okno wykresu – z zapasem ponad każdą sensowną retencję odczytów.</summary>
    public static readonly TimeSpan MaxSeriesWindow = TimeSpan.FromDays(20 * 366);

    /// <summary>Odczyty do wykresu: okno [from, to] (domyślnie ostatnie 3 doby), najwyżej „points” punktów (uśrednianie robi Core).</summary>
    private async Task<IResult> ReadSeriesAsync(string id, DateTimeOffset? from, DateTimeOffset? to, int? points, CancellationToken ct)
    {
        var end = to ?? Now;
        var start = from ?? end - DefaultSeriesWindow;
        if (start >= end || end - start > MaxSeriesWindow) return Results.BadRequest();
        var series = await options.Series(id, start, end, Math.Clamp(points ?? 300, ReadingSeriesBuilder.MinPoints, ReadingSeriesBuilder.MaxPoints), ct);
        return series is null ? Results.NotFound() : Results.Json(series, Json);
    }

    /// <param name="contains">Tylko wpisy z tym tekstem w treści (strona czujnika filtruje po jego nazwie).</param>
    private async Task<IResult> ReadLogAsync(int? lines, string? level, int? daysAgo, string? contains, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(Now, options.Zone).DateTime);
        var day = today.AddDays(-Math.Clamp(daysAgo ?? 0, 0, 365));
        var filter = string.IsNullOrWhiteSpace(contains) ? null : contains.Length > 200 ? contains[..200] : contains;
        var text = await options.Logs.ReadDayAsync(day, ct);
        var entries = text is null ? [] : LogTail.Parse(text, Math.Clamp(lines ?? 200, 1, 2000), warningsOnly: level == "warn", filter);
        return Results.Json(new LogResponse(day, text is not null, entries), Json);
    }

    private sealed record LogResponse(DateOnly Day, bool Exists, IReadOnlyList<LogLine> Entries);

    private static IResult Html(string html, int status = StatusCodes.Status200OK) =>
        Results.Content(html, "text/html; charset=utf-8", System.Text.Encoding.UTF8, status);
}
