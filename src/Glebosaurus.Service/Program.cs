using Glebosaurus.Core.Hosting;
using Glebosaurus.Core.Logging;
using Glebosaurus.Core.Updates;
using Glebosaurus.Service;
using Microsoft.Extensions.Logging;

// glebosaurus-service [--root KATALOG]
// Uruchamia go zadanie Harmonogramu „Glebosaurus” przy zalogowaniu (rejestruje je deploy/install-windows.ps1). Bez okna:
// cały ślad pracy jest w data\logs\service-RRRRMMDD.log (usługa) i glebosaurus-RRRRMMDD.log (monitor).
var paths = args is ["--root", var root] ? new ServicePaths(Path.GetFullPath(root)) : ServicePaths.FromExecutable();
using var logs = new FileLoggerProvider(new DailyLogFiles(paths.Logs, DailyLogFiles.ServiceName), keepDays: 7);
var logger = logs.CreateLogger("Usługa");

try
{
    using var instance = SingleInstance.TryAcquire(InstanceNames.ServiceMutex);
    if (instance is null)
    {
        logger.LogInformation("Usługa Glebosaurus już działa – ta kopia kończy pracę.");
        return ExitCodes.Success;
    }

    using var stop = new CancellationTokenSource();
    using var stopRequests = StopSignal.Listen(InstanceNames.StopEvent, () =>
    {
        logger.LogInformation("Prośba o zatrzymanie usługi.");
        try { stop.Cancel(); } catch (ObjectDisposedException) { }
    }, reset: true);

    var settings = InstallSettings.Load(paths.Settings);
    using var http = Updater.CreateClient();
    var updater = new Updater(paths, settings, http, logger, TimeProvider.System);
    var code = await new Supervisor(paths, settings, updater, logger, TimeProvider.System).RunAsync(stop.Token);
    logger.LogInformation("Usługa kończy pracę.");
    return code;
}
catch (Exception exception)
{
    logger.LogCritical(exception, "Usługa przerwana błędem.");
    return ExitCodes.Error;
}
