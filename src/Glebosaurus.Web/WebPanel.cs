using System.Net;
using System.Security.Cryptography.X509Certificates;
using Glebosaurus.Core.Charts;
using Glebosaurus.Core.Config;
using Glebosaurus.Core.Monitoring;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Glebosaurus.Web;

/// <summary>Wszystko, czego panel potrzebuje od agenta. Dane czyta wyłącznie przez te funkcje – nie dostaje konfiguracji z sekretami.</summary>
public sealed record WebPanelOptions
{
    public required WebConfig Config { get; init; }
    public required string MonitorName { get; init; }
    public required X509Certificate2 Certificate { get; init; }

    /// <summary>Skrót hasła z pliku (<see cref="PanelPassword"/>).</summary>
    public required string PasswordHash { get; init; }

    public required Func<CancellationToken, Task<MonitorStatus>> Status { get; init; }

    /// <summary>Ostatnie odczyty czujnika (id, liczba); null = nie ma takiego czujnika w konfiguracji.</summary>
    public required Func<string, int, CancellationToken, Task<IReadOnlyList<ReadingStatus>?>> Readings { get; init; }

    /// <summary>
    /// Odczyty czujnika z okna czasu przygotowane do wykresu (id, od, do, największa liczba punktów) – w agencie
    /// <see cref="ReadingSeriesBuilder.LoadAsync"/>; null = nie ma takiego czujnika w konfiguracji.
    /// </summary>
    public required Func<string, DateTimeOffset, DateTimeOffset, int, CancellationToken, Task<ReadingSeries?>> Series { get; init; }

    public required ILogArchive Logs { get; init; }

    /// <summary>Zlecenie odczytu poza harmonogramem (null = wszystkie czujniki) – w agencie polecenie przez chmurę
    /// (<c>CloudPanel.RequestReadAsync</c>).</summary>
    public required Func<IReadOnlyCollection<string>?, CancellationToken, Task<ReadRequestResult>> RequestRead { get; init; }

    /// <summary>Zlecenie raportu mailowego poza harmonogramem (prawdziwy mail) – w agencie polecenie dla nadawcy przez chmurę
    /// (<c>CloudPanel.RequestReportAsync</c>).</summary>
    public required Func<CancellationToken, Task<ReportRequestResult>> RequestReport { get; init; }

    public TimeProvider Clock { get; init; } = TimeProvider.System;
    public TimeZoneInfo Zone { get; init; } = TimeZoneInfo.Local;

    /// <summary>Tylko do testów: nasłuch wyłącznie na loopbacku, na wolnym porcie wybranym przez system (zamiast <see cref="WebConfig.Port"/>).</summary>
    public bool LoopbackOnEphemeralPort { get; init; }
}

/// <summary>
/// Panel diagnostyczny WWW działający w procesie monitora: Kestrel z HTTPS (certyfikat z podpisem własnym), dostęp tylko z
/// sieci lokalnej, logowanie hasłem; pulpit z wykresem ostatnich 3 dób przy każdym czujniku, strona czujnika z pełną
/// historią i dwie akcje przez polecenia w chmurze – odczyt czujników i raport mailowy na żądanie. Konfiguracji nie zmienia. Błąd panelu nie zatrzymuje monitora – agent go tylko odnotowuje.
/// </summary>
public sealed class WebPanel : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly X509Certificate2 certificate;

    private WebPanel(WebApplication app, X509Certificate2 certificate, IReadOnlyList<Uri> addresses)
    {
        this.app = app;
        this.certificate = certificate;
        Addresses = addresses;
    }

    /// <summary>Adresy, na których Kestrel faktycznie słucha (np. https://[::]:8470).</summary>
    public IReadOnlyList<Uri> Addresses { get; }

    /// <summary>Uruchamia panel. Od tej chwili panel jest właścicielem <see cref="WebPanelOptions.Certificate"/> i zwalnia go przy zamknięciu.</summary>
    public static async Task<WebPanel> StartAsync(WebPanelOptions options, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ApplicationName = typeof(WebPanel).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production
        });
        builder.WebHost.UseKestrelHttpsConfiguration();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new ForwardingLoggerProvider(loggerFactory));
        // Ctrl+C i SIGTERM obsługuje agent (czyste zatrzymanie monitora), nie host WWW.
        builder.Services.AddSingleton<IHostLifetime, PassiveLifetime>();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = 16 * 1024;
            kestrel.Limits.MaxRequestHeadersTotalSize = 32 * 1024;
            kestrel.Limits.MaxConcurrentConnections = 64;
            void Https(ListenOptions listen) => listen.UseHttps(options.Certificate);
            if (options.LoopbackOnEphemeralPort) kestrel.Listen(IPAddress.Loopback, 0, Https);
            else kestrel.ListenAnyIP(options.Config.Port, Https);
        });

        var app = builder.Build();
        try
        {
            new PanelEndpoints(options, loggerFactory.CreateLogger("Glebosaurus.Web")).Map(app);
            await app.StartAsync(cancellationToken);
        }
        catch
        {
            await app.DisposeAsync();
            options.Certificate.Dispose();
            throw;
        }

        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses
            .Select(a => new Uri(a)).ToList() ?? [];
        return new WebPanel(app, options.Certificate, addresses);
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await app.StopAsync(timeout.Token); }
        catch (OperationCanceledException) { /* połączenia, które nie zdążyły się zamknąć, zamknie Dispose */ }
        await app.DisposeAsync();
        certificate.Dispose();
    }

    /// <summary>Dziennik hosta WWW trafia do wspólnego dziennika agenta (z jego filtrami: „Microsoft” dopiero od ostrzeżeń).</summary>
    private sealed class ForwardingLoggerProvider(ILoggerFactory factory) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => factory.CreateLogger(categoryName);
        public void Dispose() { }
    }

    private sealed class PassiveLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
