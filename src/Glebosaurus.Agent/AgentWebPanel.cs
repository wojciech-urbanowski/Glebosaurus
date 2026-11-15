using Glebosaurus.Agent.Ble;
using Glebosaurus.Core.Charts;
using Glebosaurus.Core.Monitoring;
using Glebosaurus.Web;
using Microsoft.Extensions.Logging;

namespace Glebosaurus.Agent;

/// <summary>Uruchamia panel WWW obok silnika monitora. Każdy problem z panelem kończy się wpisem w dzienniku – monitor pracuje dalej.</summary>
internal static class AgentWebPanel
{
    public static async Task<WebPanel?> TryStartAsync(
        LoadedConfig loaded, MonitorEngine engine, IMonitorStore store, ILoggerFactory loggers, ILogger logger, CancellationToken cancellationToken)
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
            var (certificate, created) = PanelCertificate.LoadOrCreate(loaded.WebCertificatePath, loaded.Config.MonitorName, DateTimeOffset.UtcNow);
            var fingerprint = PanelCertificate.Fingerprint(certificate);
            if (created) logger.LogInformation("Panel WWW: utworzono nowy certyfikat HTTPS ({Path}).", loaded.WebCertificatePath);

            var host = new MonitorHostInfo(BleHost.Backend, loaded.ConfigPath, loaded.DatabasePath, loaded.LogDirectory);
            var config = loaded.Config;
            var panel = await WebPanel.StartAsync(new WebPanelOptions
            {
                Config = web,
                MonitorName = config.MonitorName,
                Certificate = certificate,
                PasswordHash = hash,
                Status = ct => MonitorStatusBuilder.BuildAsync(config, store, engine.State, host, DateTimeOffset.UtcNow, TimeZoneInfo.Local, ct),
                Readings = async (id, count, ct) => config.Sensors.Any(s => s.Id == id)
                    ? await MonitorStatusBuilder.RecentReadingsAsync(store, id, count, ct)
                    : null,
                Series = (id, from, to, points, ct) => ReadingSeriesBuilder.LoadAsync(config, store, id, from, to, points, TimeZoneInfo.Local, ct),
                Logs = loaded.LogFiles,
                RequestRead = engine.RequestRead,
                RequestReport = engine.RequestReport
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
