using System.ComponentModel;
using Glebosaurus.Core;
using Glebosaurus.Core.Hosting;
using Glebosaurus.Core.Updates;
using Microsoft.Extensions.Logging;

namespace Glebosaurus.Service;

/// <summary>
/// Pętla usługi: sprawdza wydania (przy starcie, potem codziennie), uruchamia monitor, podnosi go po awarii z rosnącym
/// odstępem (<see cref="RestartBackoff"/>), a na prośbę o zatrzymanie albo przed aktualizacją zatrzymuje go czysto.
/// Decyzje (wersje, harmonogram, odstępy) są w Core i mają testy; tu jest tylko ich złożenie z procesami i czasem.
/// </summary>
internal sealed class Supervisor(ServicePaths paths, InstallSettings settings, Updater updater, ILogger logger, TimeProvider time)
{
    /// <summary>Ile czekać na czyste zatrzymanie monitora (odczyt trwa do ok. 15 s, okno nasłuchu do 60 s).</summary>
    private static readonly TimeSpan StopTimeout = TimeSpan.FromMinutes(4);

    /// <summary>Limit jednego sprawdzenia wydań – przy starcie nie chcemy długo opóźniać monitora.</summary>
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(45);

    private enum Outcome { Exited, Stopped, Updating, StoppedForUpdate }

    public async Task<int> RunAsync(CancellationToken stop)
    {
        var schedule = new UpdateCheckSchedule(settings.DailyCheckTime, UpdateCheckSchedule.DefaultRetryAfterFailure, TimeZoneInfo.Local);
        logger.LogInformation("Usługa Glebosaurus {Version} startuje (katalog {Root}); aktualizacje: {Updates}.", AppInfo.Version, paths.Root,
            settings.AutoUpdate ? $"przy starcie i codziennie o {schedule.DailyAt:HH\\:mm}, z {settings.Repository}" : "wyłączone (install.json)");
        Directory.CreateDirectory(paths.Data);

        var nextCheck = settings.AutoUpdate ? time.GetUtcNow() : DateTimeOffset.MaxValue;
        var restartAt = time.GetUtcNow();
        var backoff = new RestartBackoff();
        while (!stop.IsCancellationRequested)
        {
            var now = time.GetUtcNow();
            if (now >= nextCheck)
            {
                var offer = await CheckAsync(schedule, next => nextCheck = next, stop);
                if (offer is not null && await updater.HandOffAsync(offer, stop)) return ExitCodes.Success;
                if (offer is not null) nextCheck = schedule.AfterFailure(time.GetUtcNow());
                continue;
            }
            if (now < restartAt)
            {
                await DelayAsync((restartAt < nextCheck ? restartAt : nextCheck) - now, stop);
                continue;
            }

            MonitorProcess monitor;
            try
            {
                monitor = MonitorProcess.Start(paths, logger);
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
            {
                var delay = backoff.Next(ExitCodes.Error, TimeSpan.Zero);
                logger.LogError("Nie udało się uruchomić monitora {Executable}: {Error}; ponowna próba za {Delay}.", paths.MonitorExecutable, e.Message, Describe(delay));
                restartAt = time.GetUtcNow() + delay;
                continue;
            }

            using (monitor)
            {
                var startedAt = time.GetUtcNow();
                logger.LogInformation("Monitor uruchomiony (proces {Pid}).", monitor.ProcessId);
                var outcome = await SuperviseAsync(monitor, schedule, () => nextCheck, next => nextCheck = next, stop);
                switch (outcome)
                {
                    case Outcome.Stopped:
                    case Outcome.Updating:
                        return ExitCodes.Success;
                    case Outcome.StoppedForUpdate:
                        restartAt = time.GetUtcNow();   // przekazanie aktualizacji się nie udało – monitor wraca od razu
                        break;
                    default:
                        var code = await monitor.Exited;
                        var ranFor = time.GetUtcNow() - startedAt;
                        var delay = backoff.Next(code, ranFor);
                        if (code == ExitCodes.AlreadyRunning)
                            logger.LogInformation("Na tym koncie działa już inny monitor (np. „run” z konsoli); ponowna próba za {Delay}.", Describe(delay));
                        else
                            logger.LogWarning("Monitor zakończył pracę (kod {Code}) po {RanFor}; ponowny start za {Delay}.", code, Describe(ranFor), Describe(delay));
                        restartAt = time.GetUtcNow() + delay;
                        break;
                }
            }
        }
        return ExitCodes.Success;
    }

    /// <summary>Czeka na koniec monitora, prośbę o zatrzymanie albo porę sprawdzenia wydań.</summary>
    private async Task<Outcome> SuperviseAsync(MonitorProcess monitor, UpdateCheckSchedule schedule,
        Func<DateTimeOffset> nextCheck, Action<DateTimeOffset> setNextCheck, CancellationToken stop)
    {
        while (true)
        {
            using (var wait = CancellationTokenSource.CreateLinkedTokenSource(stop))
            {
                var timer = Task.Delay(Clamp(nextCheck() - time.GetUtcNow()), time, wait.Token);
                await Task.WhenAny(monitor.Exited, timer);
                await wait.CancelAsync();
            }
            if (monitor.Exited.IsCompleted) return Outcome.Exited;
            if (stop.IsCancellationRequested)
            {
                await StopMonitorAsync(monitor, "zatrzymanie usługi");
                return Outcome.Stopped;
            }
            if (time.GetUtcNow() < nextCheck()) continue;

            var offer = await CheckAsync(schedule, setNextCheck, stop);
            if (offer is null) continue;
            if (!await StopMonitorAsync(monitor, $"aktualizacja do {ReleaseVersion.Format(offer.Version)}"))
            {
                setNextCheck(schedule.AfterFailure(time.GetUtcNow()));
                continue;
            }
            if (await updater.HandOffAsync(offer, stop)) return Outcome.Updating;
            setNextCheck(schedule.AfterFailure(time.GetUtcNow()));
            return Outcome.StoppedForUpdate;
        }
    }

    private async Task<bool> StopMonitorAsync(MonitorProcess monitor, string reason)
    {
        logger.LogInformation("Zatrzymuję monitor ({Reason}).", reason);
        if (await monitor.StopAsync(StopTimeout))
        {
            logger.LogInformation("Monitor zatrzymany (kod {Code}).", await monitor.Exited);
            return true;
        }
        logger.LogError("Monitor nie zatrzymał się w ciągu {Timeout} – zostawiam go (nie zabijam w trakcie pracy z czujnikiem).", Describe(StopTimeout));
        return false;
    }

    private async Task<UpdateOffer?> CheckAsync(UpdateCheckSchedule schedule, Action<DateTimeOffset> setNextCheck, CancellationToken stop)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(stop);
        limit.CancelAfter(CheckTimeout);
        try
        {
            var offer = await updater.CheckAsync(limit.Token);
            setNextCheck(schedule.NextDaily(time.GetUtcNow()));
            return offer;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception e) when (e is HttpRequestException or InvalidDataException or OperationCanceledException or IOException)
        {
            var next = schedule.AfterFailure(time.GetUtcNow());
            setNextCheck(next);
            logger.LogWarning("Nie udało się sprawdzić wydań ({Error}); kolejna próba o {Next:yyyy-MM-dd HH:mm}.",
                e is OperationCanceledException ? "przekroczony czas" : e.Message, next.ToLocalTime());
            return null;
        }
    }

    private async Task DelayAsync(TimeSpan delay, CancellationToken stop)
    {
        try { await Task.Delay(Clamp(delay), time, stop); }
        catch (OperationCanceledException) { }
    }

    /// <summary>Task.Delay przyjmuje najwyżej ok. 49 dni; dalsze terminy (np. wyłączone aktualizacje) i tak liczymy na nowo.</summary>
    private static TimeSpan Clamp(TimeSpan delay) =>
        delay <= TimeSpan.Zero ? TimeSpan.Zero : delay > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : delay;

    private static string Describe(TimeSpan span) =>
        span >= TimeSpan.FromHours(1) ? $"{span.TotalHours:0.#} godz." : span >= TimeSpan.FromMinutes(1) ? $"{span.TotalMinutes:0.#} min" : $"{span.TotalSeconds:0} s";
}
