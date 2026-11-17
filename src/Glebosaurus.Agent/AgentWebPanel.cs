using Glebosaurus.Agent.Ble;
using Glebosaurus.Core.Charts;
using Glebosaurus.Core.Cloud;
using Glebosaurus.Core.Config;
using Glebosaurus.Core.Monitoring;
using Glebosaurus.Web;
using Microsoft.Extensions.Logging;

namespace Glebosaurus.Agent;

/// <summary>Skąd panel bierze dane i dokąd kieruje akcje – inaczej w trybie samodzielnym, inaczej w chmurowym.</summary>
/// <param name="Config">Bieżąca konfiguracja (w trybie chmurowym zmienia się po odświeżeniu z chmury).</param>
internal sealed record PanelSources(
    string MonitorName,
    Func<AgentConfig> Config,
    Func<MonitorHostInfo, CancellationToken, Task<MonitorStatus>> Status,
    Func<IReadOnlyCollection<string>?, CancellationToken, Task<ReadRequestResult>> RequestRead,
    Func<CancellationToken, Task<ReportRequestResult>> RequestReport);

/// <summary>Uruchamia panel WWW obok silnika monitora. Każdy problem z panelem kończy się wpisem w dzienniku – monitor pracuje dalej.</summary>
internal static class AgentWebPanel
{
    /// <summary>Tryb samodzielny: dane z <see cref="MonitorEngine"/> i lokalnej bazy.</summary>
    public static Task<WebPanel?> TryStartAsync(
        LoadedConfig loaded, MonitorEngine engine, IMonitorStore store, ILoggerFactory loggers, ILogger logger, CancellationToken cancellationToken)
    {
        var config = loaded.Config;
        return TryStartAsync(loaded, new PanelSources(
            config.MonitorName,
            () => config,
            (host, ct) => MonitorStatusBuilder.BuildAsync(config, store, engine.State, host, DateTimeOffset.UtcNow, TimeZoneInfo.Local, ct),
            (ids, _) => Task.FromResult(engine.RequestRead(ids)),
            _ => Task.FromResult(engine.RequestReport())), store, loggers, logger, cancellationToken);
    }

    /// <summary>Tryb chmurowy: konfiguracja z chmury, dane z lokalnej bazy (z kopią odczytów innych agentów), akcje przez chmurę.</summary>
    public static Task<WebPanel?> TryStartAsync(
        LoadedConfig loaded, CloudConfigSource configSource, CloudPanel panel, IMonitorStore store, ILoggerFactory loggers, ILogger logger,
        CancellationToken cancellationToken)
    {
        AgentConfig Current() => configSource.Current?.Config ?? loaded.Config;
        return TryStartAsync(loaded, new PanelSources(
            Current().MonitorName,
            Current,
            panel.StatusAsync,
            panel.RequestReadAsync,
            panel.RequestReportAsync), store, loggers, logger, cancellationToken);
    }

    private static async Task<WebPanel?> TryStartAsync(
        LoadedConfig loaded, PanelSources sources, IMonitorStore store, ILoggerFactory loggers, ILogger logger, CancellationToken cancellationToken)
    {
        if (loaded.Config.Web is not { Enabled: true } web) return null;

        var hash = PanelPassword.Read(loaded.WebPasswordPath);
        if (string.IsNullOrEmpty(hash))
        {
            logger.LogError("Panel WWW nie wystartował: brak hasła ({Path}). Ustaw je poleceniem „glebosaurus web-password” i uruchom monitor ponownie; monitor pracuje dalej bez panelu.",
                loaded.WebPasswordPath);
            return null;
        }

        try
        {
            var (certificate, created) = PanelCertificate.LoadOrCreate(loaded.WebCertificatePath, sources.MonitorName, DateTimeOffset.UtcNow);
            var fingerprint = PanelCertificate.Fingerprint(certificate);
            if (created) logger.LogInformation("Panel WWW: utworzono nowy certyfikat HTTPS ({Path}).", loaded.WebCertificatePath);

            var host = new MonitorHostInfo(BleHost.Backend, loaded.ConfigPath, loaded.DatabasePath, loaded.LogDirectory);
            var panel = await WebPanel.StartAsync(new WebPanelOptions
            {
                Config = web,
                MonitorName = sources.MonitorName,
                Certificate = certificate,
                PasswordHash = hash,
                Status = ct => sources.Status(host, ct),
                Readings = async (id, count, ct) => sources.Config().Sensors.Any(s => s.Id == id)
                    ? await MonitorStatusBuilder.RecentReadingsAsync(store, id, count, ct)
                    : null,
                Series = (id, from, to, points, ct) => ReadingSeriesBuilder.LoadAsync(sources.Config(), store, id, from, to, points, TimeZoneInfo.Local, ct),
                Logs = loaded.LogFiles,
                RequestRead = sources.RequestRead,
                RequestReport = sources.RequestReport
            }, loggers, cancellationToken);

            logger.LogInformation("Panel WWW: {Urls} (certyfikat SHA-256 {Fingerprint}).", string.Join(", ", Urls(web.Port)), fingerprint);
            return panel;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Panel WWW nie wystartował na porcie {Port}; monitor pracuje dalej bez panelu.", web.Port);
            return null;
        }
    }

    /// <summary>Adresy, pod którymi panel powinien być widoczny w sieci lokalnej (nazwa komputera i jego adresy IPv4).</summary>
    public static IEnumerable<string> Urls(int port) =>
        PanelCertificate.LocalAddresses().Select(a => a.ToString())
            .Prepend(Environment.MachineName.ToLowerInvariant())
            .Select(host => $"https://{host}:{port}/");
}
