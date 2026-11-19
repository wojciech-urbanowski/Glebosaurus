using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Glebosaurus.Core.Charts;
using Glebosaurus.Core.Config;
using Glebosaurus.Core.Domain;
using Glebosaurus.Core.Monitoring;
using Glebosaurus.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Glebosaurus.Tests;

/// <summary>Warstwy ochrony panelu WWW osobno: sieci, hasło, sesje, limit prób, blokada POST z obcych stron, certyfikat.</summary>
public class WebPanelSecurityTests
{
    private static readonly DateTimeOffset T0 = Fixtures.T0;

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("192.168.1.20", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.20.1.1", true)]
    [InlineData("169.254.10.10", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fd00::12", true)]
    [InlineData("::ffff:192.168.1.5", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("::ffff:8.8.8.8", false)]
    [InlineData("2001:db8::1", false)]
    public void DefaultAllowListAcceptsOnlyLoopbackAndPrivateNetworks(string address, bool allowed) =>
        Assert.Equal(allowed, new NetworkAllowList(new WebConfig().ParseAllowedNetworks()).IsAllowed(IPAddress.Parse(address)));

    [Theory]
    [InlineData("192.168.1.77", true)]
    [InlineData("192.168.2.1", false)]
    [InlineData("10.0.0.5", false)]
    [InlineData("127.0.0.1", true)]
    public void CustomAllowListReplacesPrivateNetworksButKeepsLoopback(string address, bool allowed)
    {
        var web = new WebConfig { AllowedNetworks = ["192.168.1.0/24"] };
        Assert.Equal(allowed, new NetworkAllowList(web.ParseAllowedNetworks()).IsAllowed(IPAddress.Parse(address)));
    }

    [Fact]
    public void PasswordHashVerifiesOnlyTheRightPasswordAndUsesFreshSalt()
    {
        var hash = PanelPassword.Hash("zielona-monstera-42");

        Assert.StartsWith($"pbkdf2-sha256${PanelPassword.Iterations}$", hash);
        Assert.True(PanelPassword.Verify("zielona-monstera-42", hash));
        Assert.False(PanelPassword.Verify("zielona-monstera-43", hash));
        Assert.NotEqual(hash, PanelPassword.Hash("zielona-monstera-42", PanelPassword.Iterations));
        Assert.DoesNotContain("zielona", hash);
    }

    [Theory]
    [InlineData("")]
    [InlineData("haslo")]
    [InlineData("pbkdf2-sha256$1000$$")]
    [InlineData("pbkdf2-sha256$abc$AAAA$AAAA")]
    [InlineData("pbkdf2-sha256$999999999$AAAA$AAAA")]
    [InlineData("pbkdf2-sha256$1000$nie-base64$AAAA")]
    [InlineData("md5$1000$AAAA$AAAA")]
    public void MalformedStoredHashRejectsEveryPassword(string stored) =>
        Assert.False(PanelPassword.Verify("zielona-monstera-42", stored));

    [Theory]
    [InlineData("krotkie", true)]
    [InlineData("            ", true)]
    [InlineData("zielona-monstera-42", false)]
    public void PasswordMustBeLongEnough(string password, bool problem) =>
        Assert.Equal(problem, PanelPassword.Problem(password) is not null);

    [Fact]
    public void PasswordFileRoundTrips()
    {
        var directory = Directory.CreateTempSubdirectory("glebosaurus-web-");
        try
        {
            var path = Path.Combine(directory.FullName, "glebosaurus-web.password");
            Assert.Null(PanelPassword.Read(path));
            var hash = PanelPassword.Hash("zielona-monstera-42", 1000);
            PanelPassword.Write(path, hash);
            Assert.Equal(hash, PanelPassword.Read(path));
            Assert.Single(directory.GetFiles());                     // plik tymczasowy nie zostaje
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public void SessionIsValidUntilItExpiresOrIsRemoved()
    {
        var sessions = new SessionStore(TimeSpan.FromHours(12));
        var token = sessions.Create(T0);

        Assert.True(sessions.IsValid(token, T0 + TimeSpan.FromHours(11)));
        Assert.False(sessions.IsValid(token + "x", T0));
        Assert.False(sessions.IsValid(null, T0));
        Assert.False(sessions.IsValid(token, T0 + TimeSpan.FromHours(12)));
        Assert.Equal(0, sessions.Count);

        var other = sessions.Create(T0);
        sessions.Remove(other);
        Assert.False(sessions.IsValid(other, T0));
    }

    [Fact]
    public void TooManySessionsDropTheOldest()
    {
        var sessions = new SessionStore(TimeSpan.FromHours(12), maxSessions: 2);
        var first = sessions.Create(T0);
        var second = sessions.Create(T0 + TimeSpan.FromMinutes(1));
        var third = sessions.Create(T0 + TimeSpan.FromMinutes(2));

        Assert.False(sessions.IsValid(first, T0 + TimeSpan.FromMinutes(3)));
        Assert.True(sessions.IsValid(second, T0 + TimeSpan.FromMinutes(3)));
        Assert.True(sessions.IsValid(third, T0 + TimeSpan.FromMinutes(3)));
    }

    [Fact]
    public void AddressIsBlockedAfterFiveFailuresUntilTheBlockEnds()
    {
        var throttle = new LoginThrottle();
        var address = IPAddress.Parse("192.168.1.20");
        for (var i = 0; i < LoginThrottle.MaxFailures - 1; i++) throttle.Failure(address, T0);
        Assert.Null(throttle.BlockedUntil(address, T0));
        Assert.Null(throttle.BlockedUntil(IPAddress.Parse("192.168.1.21"), T0));

        throttle.Failure(IPAddress.Parse("::ffff:192.168.1.20"), T0);              // ten sam adres w zapisie IPv6
        Assert.Equal(T0 + LoginThrottle.BlockFor, throttle.BlockedUntil(address, T0 + TimeSpan.FromMinutes(1)));
        Assert.Null(throttle.BlockedUntil(address, T0 + LoginThrottle.BlockFor));
    }

    [Fact]
    public void FailuresOutsideTheWindowAndSuccessfulLoginResetTheCount()
    {
        var throttle = new LoginThrottle();
        var address = IPAddress.Parse("192.168.1.20");
        for (var i = 0; i < LoginThrottle.MaxFailures - 1; i++) throttle.Failure(address, T0);
        throttle.Failure(address, T0 + LoginThrottle.Window);                      // okno minęło – liczy od nowa
        Assert.Null(throttle.BlockedUntil(address, T0 + LoginThrottle.Window));

        for (var i = 0; i < LoginThrottle.MaxFailures - 2; i++) throttle.Failure(address, T0 + LoginThrottle.Window);
        throttle.Success(address);
        throttle.Failure(address, T0 + LoginThrottle.Window);
        Assert.Null(throttle.BlockedUntil(address, T0 + LoginThrottle.Window));
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData("https://panel.local:8470", null, false)]
    [InlineData("https://PANEL.local:8470", "same-origin", false)]
    [InlineData(null, "none", false)]
    [InlineData("null", "same-origin", false)]          // formularz z panelu przy restrykcyjnej polityce referrera
    [InlineData("https://evil.example", null, true)]
    [InlineData("null", null, true)]
    [InlineData("http://panel.local:8470", null, true)]
    [InlineData(null, "cross-site", true)]
    [InlineData(null, "same-site", true)]
    [InlineData("https://panel.local:8470", "cross-site", true)]
    public void PostFromAnotherSiteIsRecognized(string? origin, string? fetchSite, bool crossSite)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("panel.local:8470");
        if (origin is not null) context.Request.Headers.Origin = origin;
        if (fetchSite is not null) context.Request.Headers["Sec-Fetch-Site"] = fetchSite;
        Assert.Equal(crossSite, PanelEndpoints.IsCrossSite(context.Request));
    }

    [Fact]
    public void CertificateIsCreatedOnceReusedAndRenewedBeforeExpiry()
    {
        var directory = Directory.CreateTempSubdirectory("glebosaurus-web-");
        try
        {
            var path = Path.Combine(directory.FullName, "glebosaurus-web.pfx");
            var now = DateTimeOffset.UtcNow;
            var (first, created) = PanelCertificate.LoadOrCreate(path, "Raspberry Pi – salon", now);
            using (first)
            {
                Assert.True(created);
                Assert.True(first.HasPrivateKey);
                Assert.Contains("localhost", first.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single().EnumerateDnsNames());
                Assert.Contains(first.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages.Cast<Oid>(), o => o.Value == "1.3.6.1.5.5.7.3.1");
                Assert.Equal(64, PanelCertificate.Fingerprint(first).Count(char.IsAsciiHexDigit));

                var (again, createdAgain) = PanelCertificate.LoadOrCreate(path, "Raspberry Pi – salon", now + TimeSpan.FromDays(1));
                using (again)
                {
                    Assert.False(createdAgain);
                    Assert.Equal(first.Thumbprint, again.Thumbprint);
                }

                var (renewed, createdRenewed) = PanelCertificate.LoadOrCreate(path, "Raspberry Pi – salon", now + PanelCertificate.Validity - TimeSpan.FromDays(10));
                using (renewed)
                {
                    Assert.True(createdRenewed);
                    Assert.NotEqual(first.Thumbprint, renewed.Thumbprint);
                }
            }
        }
        finally { directory.Delete(recursive: true); }
    }
}

/// <summary>Panel na prawdziwym Kestrelu (loopback, HTTPS z certyfikatem z podpisem własnym, sprawdzanym po odcisku).</summary>
public class WebPanelIntegrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WithoutLoginOnlyTheLoginPageIsAvailable()
    {
        await using var panel = await TestPanel.StartAsync();
        using var client = panel.NewClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/status", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/log", Ct)).StatusCode);
        var root = await client.GetAsync("/", Ct);
        Assert.Equal(HttpStatusCode.Redirect, root.StatusCode);
        Assert.Equal("/login", root.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/app.js", Ct)).StatusCode);

        var login = await client.GetAsync("/login", Ct);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var html = await login.Content.ReadAsStringAsync(Ct);
        Assert.Contains("name=\"password\"", html);
        Assert.Contains("Test &lt;b&gt;", html);                                   // nazwa monitora zakodowana
        Assert.DoesNotContain("Test <b>", html);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/app.css", Ct)).StatusCode);
    }

    [Fact]
    public async Task EveryResponseCarriesSecurityHeaders()
    {
        await using var panel = await TestPanel.StartAsync();
        using var client = panel.NewClient();

        foreach (var path in new[] { "/login", "/api/status" })
        {
            var response = await client.GetAsync(path, Ct);
            Assert.Contains("default-src 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
            Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
            Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("same-origin", response.Headers.GetValues("Referrer-Policy").Single());
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.False(response.Headers.Contains("Server"));
        }
    }

    [Fact]
    public async Task LoginSetsAStrictSessionCookieAndOpensTheApi()
    {
        await using var panel = await TestPanel.StartAsync();
        using var client = panel.NewClient();

        var login = await panel.LoginAsync(client, TestPanel.Password);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/", login.Headers.Location?.OriginalString);
        var cookie = login.Headers.GetValues("Set-Cookie").Single().ToLowerInvariant();
        Assert.StartsWith("__host-glebosaurus=", cookie);
        foreach (var attribute in new[] { "secure", "httponly", "samesite=strict", "path=/" }) Assert.Contains(attribute, cookie);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/app.js", Ct)).StatusCode);

        var status = await client.GetAsync("/api/status", Ct);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        var json = await status.Content.ReadAsStringAsync(Ct);
        using var document = JsonDocument.Parse(json);
        Assert.Equal("Test", document.RootElement.GetProperty("monitor").GetProperty("name").GetString());
        Assert.Equal(55, document.RootElement.GetProperty("sensors")[0].GetProperty("latest").GetProperty("moisturePercent").GetInt32());
        Assert.DoesNotContain("a1b2c3d4e5f6", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("1234567890123456789012", json);
        Assert.DoesNotContain("pbkdf2", json);

        var readings = await client.GetAsync("/api/sensors/s1/readings?count=10", Ct);
        Assert.Equal(HttpStatusCode.OK, readings.StatusCode);
        Assert.Equal(1, JsonDocument.Parse(await readings.Content.ReadAsStringAsync(Ct)).RootElement.GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/sensors/nie-ma/readings", Ct)).StatusCode);
    }

    [Fact]
    public async Task LogApiReturnsTheTailOfTheChosenDay()
    {
        await using var panel = await TestPanel.StartAsync();
        using var client = panel.NewClient();
        await panel.LoginAsync(client, TestPanel.Password);

        using var today = JsonDocument.Parse(await client.GetStringAsync("/api/log?level=warn", Ct));
        Assert.Equal("2026-11-13", today.RootElement.GetProperty("day").GetString());
        Assert.True(today.RootElement.GetProperty("exists").GetBoolean());
        var entries = today.RootElement.GetProperty("entries");
        Assert.Equal(1, entries.GetArrayLength());
        Assert.Equal("WRN", entries[0].GetProperty("level").GetString());

        using var all = JsonDocument.Parse(await client.GetStringAsync("/api/log?level=all", Ct));
        Assert.Equal(2, all.RootElement.GetProperty("entries").GetArrayLength());

        using var yesterday = JsonDocument.Parse(await client.GetStringAsync("/api/log?daysAgo=1", Ct));
        Assert.False(yesterday.RootElement.GetProperty("exists").GetBoolean());

        using var filtered = JsonDocument.Parse(await client.GetStringAsync("/api/log?level=all&contains=" + Uri.EscapeDataString("nie znaleziono"), Ct));
        Assert.Equal("05:58:30", filtered.RootElement.GetProperty("entries").EnumerateArray().Single().GetProperty("time").GetString());
    }

    [Fact]
    public async Task SeriesApiReturnsTheLastThreeDaysByDefault()
    {
        await using var panel = await TestPanel.StartAsync();
        using var client = panel.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/sensors/s1/series", Ct)).StatusCode);
        await panel.LoginAsync(client, TestPanel.Password);

        using (var json = JsonDocument.Parse(await client.GetStringAsync("/api/sensors/s1/series", Ct)))
        {
            var root = json.RootElement;
            Assert.Equal("s1", root.GetProperty("sensorId").GetString());
            Assert.Equal(Fixtures.T0, root.GetProperty("to").GetDateTimeOffset());
            Assert.Equal(Fixtures.T0 - TimeSpan.FromHours(72), root.GetProperty("from").GetDateTimeOffset());
            Assert.Equal(0, root.GetProperty("bucketMinutes").GetInt32());
            Assert.Equal(Fixtures.T0 - TimeSpan.FromMinutes(3), root.GetProperty("firstAt").GetDateTimeOffset());
            var point = root.GetProperty("points").EnumerateArray().Single();
            Assert.Equal(55, point.GetProperty("moisture").GetDouble());
            Assert.Equal(22.5, point.GetProperty("temperature").GetDouble());
            Assert.False(point.GetProperty("gapBefore").GetBoolean());
        }

        var from = Uri.EscapeDataString((Fixtures.T0 - TimeSpan.FromDays(30)).ToString("O"));
        var to = Uri.EscapeDataString((Fixtures.T0 - TimeSpan.FromDays(10)).ToString("O"));
        using (var json = JsonDocument.Parse(await client.GetStringAsync($"/api/sensors/s1/series?from={from}&to={to}&points=50", Ct)))
            Assert.Equal(0, json.RootElement.GetProperty("points").GetArrayLength());

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/sensors/nie-ma/series", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/sensors/s1/series?from={to}&to={from}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/sensors/s1/series?from=wczoraj", Ct)).StatusCode);
    }

    [Fact]
    public async Task SensorPageAndScriptsNeedLogin()
    {
        await using var panel = await TestPanel.StartAsync();
        using var client = panel.NewClient();

        var page = await client.GetAsync("/sensor?id=s1", Ct);
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.Equal("/login", page.Headers.Location?.OriginalString);
        foreach (var script in new[] { "/common.js", "/charts.js", "/sensor.js" })
            Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync(script, Ct)).StatusCode);

        await panel.LoginAsync(client, TestPanel.Password);
        var html = await client.GetStringAsync("/sensor?id=s1", Ct);
        Assert.Contains("src=\"/sensor.js\"", html);
        foreach (var script in new[] { "/common.js", "/charts.js", "/app.js", "/sensor.js" })
        {
            var response = await client.GetAsync(script, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/javascript", response.Content.Headers.ContentType?.MediaType);
        }
    }

    [Fact]
    public async Task WrongPasswordsBlockTheAddressForAWhile()
    {
        await using var panel = await TestPanel.StartAsync();
        using var client = panel.NewClient();

        for (var i = 0; i < LoginThrottle.MaxFailures; i++)
        {
            var wrong = await panel.LoginAsync(client, "zle-haslo-" + i);
            Assert.Equal(HttpStatusCode.Redirect, wrong.StatusCode);
            Assert.Equal("/login?blad=1", wrong.Headers.Location?.OriginalString);
        }

        var blocked = await panel.LoginAsync(client, TestPanel.Password);         // nawet dobre hasło
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.Contains("Za dużo nieudanych prób", await blocked.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/status", Ct)).StatusCode);

        panel.Clock.Advance(LoginThrottle.BlockFor);
        Assert.Equal("/", (await panel.LoginAsync(client, TestPanel.Password)).Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/status", Ct)).StatusCode);
    }

    [Fact]
    public async Task LoginPostedFromAnotherSiteIsRejected()
    {
        await using var panel = await TestPanel.StartAsync();
        using var client = panel.NewClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await panel.LoginAsync(client, TestPanel.Password, origin: "https://evil.example")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/status", Ct)).StatusCode);

        var crossSite = await panel.LoginAsync(client, TestPanel.Password, fetchSite: "cross-site");
        Assert.Equal(HttpStatusCode.Forbidden, crossSite.StatusCode);

        var sameOrigin = await panel.LoginAsync(client, TestPanel.Password, origin: panel.BaseAddress.GetLeftPart(UriPartial.Authority));
        Assert.Equal("/", sameOrigin.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task LoginFromTheBrowserWithNullOriginIsAccepted()
    {
        // Tak wysyła formularz Chromium/Firefox, gdy strona ma restrykcyjną politykę referrera (błąd z próby na żywo 2026-11-14).
        await using var panel = await TestPanel.StartAsync();
        using var client = panel.NewClient();

        var login = await panel.LoginAsync(client, TestPanel.Password, origin: "null", fetchSite: "same-origin");

        Assert.Equal("/", login.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/status", Ct)).StatusCode);
    }

    [Fact]
    public async Task LogoutAndSessionExpiryEndAccess()
    {
        await using var panel = await TestPanel.StartAsync(new WebConfig { SessionHours = 1 });
        using var first = panel.NewClient();
        using var second = panel.NewClient();
        await panel.LoginAsync(first, TestPanel.Password);
        await panel.LoginAsync(second, TestPanel.Password);

        var logout = await first.PostAsync("/logout", null, Ct);
        Assert.Equal("/login?wylogowano=1", logout.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.Unauthorized, (await first.GetAsync("/api/status", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/api/status", Ct)).StatusCode);

        panel.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/api/status", Ct)).StatusCode);
    }

    [Fact]
    public async Task ReadOnRequestGoesToTheEngineQueue()
    {
        await using var panel = await TestPanel.StartAsync();
        using var client = panel.NewClient();
        await panel.LoginAsync(client, TestPanel.Password);

        var one = await panel.PostActionAsync(client, "/api/sensors/s1/read");
        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        using (var json = JsonDocument.Parse(await one.Content.ReadAsStringAsync(Ct)))
        {
            var item = json.RootElement.GetProperty("items")[0];
            Assert.Equal("s1", item.GetProperty("sensorId").GetString());
            Assert.Equal("queued", item.GetProperty("status").GetString());
        }

        Assert.Equal(HttpStatusCode.OK, (await panel.PostActionAsync(client, "/api/read-all")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await panel.PostActionAsync(client, "/api/sensors/nie-ma/read")).StatusCode);
        Assert.Equal([["s1"], null, ["nie-ma"]], panel.ReadRequests);
    }

    [Fact]
    public async Task ReadOnRequestNeedsLoginAndTheActionHeaderAndTheSameSite()
    {
        await using var panel = await TestPanel.StartAsync();
        using var client = panel.NewClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await panel.PostActionAsync(client, "/api/read-all")).StatusCode);
        await panel.LoginAsync(client, TestPanel.Password);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/read-all", null, Ct)).StatusCode);       // bez nagłówka
        Assert.Equal(HttpStatusCode.Forbidden, (await panel.PostActionAsync(client, "/api/read-all", fetchSite: "cross-site")).StatusCode);
        Assert.Empty(panel.ReadRequests);
    }

    [Fact]
    public async Task ReportOnRequestGoesToTheAgentAndItsAnswerComesBack()
    {
        await using var panel = await TestPanel.StartAsync();
        using var client = panel.NewClient();
        await panel.LoginAsync(client, TestPanel.Password);

        var response = await panel.PostActionAsync(client, "/api/report");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)))
            Assert.Equal("queued", json.RootElement.GetProperty("status").GetString());

        panel.ReportResult = new(ReportRequestStatus.TooSoon, Fixtures.T0 + TimeSpan.FromMinutes(1));
        using (var json = JsonDocument.Parse(await (await panel.PostActionAsync(client, "/api/report")).Content.ReadAsStringAsync(Ct)))
        {
            Assert.Equal("tooSoon", json.RootElement.GetProperty("status").GetString());
            Assert.Equal(Fixtures.T0 + TimeSpan.FromMinutes(1), json.RootElement.GetProperty("availableAt").GetDateTimeOffset());
        }
        Assert.Equal(2, panel.ReportRequests);
    }

    [Fact]
    public async Task ReportOnRequestNeedsLoginAndTheActionHeaderAndTheSameSite()
    {
        await using var panel = await TestPanel.StartAsync();
        using var client = panel.NewClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await panel.PostActionAsync(client, "/api/report")).StatusCode);
        await panel.LoginAsync(client, TestPanel.Password);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/report", null, Ct)).StatusCode);         // bez nagłówka
        Assert.Equal(HttpStatusCode.Forbidden, (await panel.PostActionAsync(client, "/api/report", fetchSite: "cross-site")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.GetAsync("/api/report", Ct)).StatusCode);
        Assert.Equal(0, panel.ReportRequests);
    }

    /// <summary>Panel z danymi testowymi: czujnik s1 z jednym odczytem, dziennik z dnia T0, hasło o małej liczbie iteracji.</summary>
    private sealed class TestPanel : IAsyncDisposable
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        public const string Password = "zielona-monstera-42";

        private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("glebosaurus-web-");
        private readonly TempStore db = new();
        private byte[] certificateHash = [];

        public ManualTimeProvider Clock { get; } = new(Fixtures.T0);

        /// <summary>Zlecenia odczytu, które panel przekazał agentowi (null = wszystkie).</summary>
        public List<IReadOnlyCollection<string>?> ReadRequests { get; } = [];

        /// <summary>Ile razy panel zlecił agentowi raport i co agent odpowiada.</summary>
        public int ReportRequests { get; private set; }
        public ReportRequestResult ReportResult { get; set; } = new(ReportRequestStatus.Queued);
        public Uri BaseAddress { get; private set; } = null!;
        private WebPanel Panel { get; set; } = null!;

        public static async Task<TestPanel> StartAsync(WebConfig? web = null)
        {
            var panel = new TestPanel();
            var config = Fixtures.Config();
            await panel.db.Store.AddReadingAsync(Fixtures.Reading("s1", Fixtures.T0 - TimeSpan.FromMinutes(3), moisture: 55), CancellationToken.None);

            var logs = new FakeLogArchive();
            logs.Days[new DateOnly(2026, 11, 13)] = """
                2026-11-13 05:58:00.000 [INF] Glebosaurus: Odczyt czujników: Monstera.
                2026-11-13 05:58:30.000 [WRN] Glebosaurus: Monstera: nie znaleziono w reklamach BLE.
                """;

            var (certificate, _) = PanelCertificate.LoadOrCreate(Path.Combine(panel.directory.FullName, "web.pfx"), "Test", DateTimeOffset.UtcNow);
            panel.certificateHash = certificate.GetCertHash(HashAlgorithmName.SHA256);
            var host = new MonitorHostInfo("Test BLE", "glebosaurus.config.json", "glebosaurus.db", "logs");
            var store = panel.db.Store;
            panel.Panel = await WebPanel.StartAsync(new WebPanelOptions
            {
                Config = web ?? new WebConfig(),
                MonitorName = "Test <b>",
                Certificate = certificate,
                PasswordHash = PanelPassword.Hash(Password, 1000),
                Status = ct => MonitorStatusBuilder.BuildAsync(config, store, MonitorRuntimeState.NotStarted, host, panel.Clock.GetUtcNow(), TimeZoneInfo.Utc, [], ct),
                Readings = async (id, count, ct) => config.Sensors.Any(s => s.Id == id) ? await MonitorStatusBuilder.RecentReadingsAsync(store, id, count, ct) : null,
                Series = (id, from, to, points, ct) => ReadingSeriesBuilder.LoadAsync(config, store, id, from, to, points, TimeZoneInfo.Utc, ct),
                Logs = logs,
                RequestRead = (ids, _) =>
                {
                    panel.ReadRequests.Add(ids);
                    return Task.FromResult(new ReadRequestResult((ids ?? ["s1"]).Select(id => id == "s1"
                        ? new ReadRequestItem(id, "Monstera", ReadRequestStatus.Queued)
                        : new ReadRequestItem(id, null, ReadRequestStatus.Unknown)).ToList()));
                },
                RequestReport = _ =>
                {
                    panel.ReportRequests++;
                    return Task.FromResult(panel.ReportResult);
                },
                Clock = panel.Clock,
                Zone = TimeZoneInfo.Utc,
                LoopbackOnEphemeralPort = true
            }, NullLoggerFactory.Instance, CancellationToken.None);
            panel.BaseAddress = new Uri($"https://127.0.0.1:{panel.Panel.Addresses[0].Port}/");
            return panel;
        }

        /// <summary>Klient jak przeglądarka: własne ciasteczka, bez podążania za przekierowaniami, ufa tylko certyfikatowi tego panelu.</summary>
        public HttpClient NewClient()
        {
            var expected = certificateHash;
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = true,
                CookieContainer = new CookieContainer(),
                SslOptions = { RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    certificate is not null && certificate.GetCertHash(HashAlgorithmName.SHA256).AsSpan().SequenceEqual(expected) }
            };
            return new HttpClient(handler) { BaseAddress = BaseAddress };
        }

        public async Task<HttpResponseMessage> LoginAsync(HttpClient client, string password, string? origin = null, string? fetchSite = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/login")
            {
                Content = new FormUrlEncodedContent([new("username", "glebosaurus"), new("password", password)])
            };
            if (origin is not null) request.Headers.Add("Origin", origin);
            if (fetchSite is not null) request.Headers.Add("Sec-Fetch-Site", fetchSite);
            return await client.SendAsync(request, Ct);
        }

        public async Task<HttpResponseMessage> PostActionAsync(HttpClient client, string path, string? fetchSite = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path);
            request.Headers.Add("X-Glebosaurus-Action", "1");
            if (fetchSite is not null) request.Headers.Add("Sec-Fetch-Site", fetchSite);
            return await client.SendAsync(request, Ct);
        }

        public async ValueTask DisposeAsync()
        {
            await Panel.DisposeAsync();
            db.Dispose();
            directory.Delete(recursive: true);
        }
    }
}
