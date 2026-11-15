using System.Diagnostics;
using System.Text;
using Glebosaurus.Core.Hosting;
using Microsoft.Extensions.Logging;

namespace Glebosaurus.Service;

/// <summary>
/// Monitor (<c>glebosaurus.exe run</c>) uruchomiony przez usługę: bez okna konsoli (CreateNoWindow – działa także, gdy
/// domyślnym terminalem jest Windows Terminal), z prywatnym sygnałem czystego zatrzymania. Wyjście monitora trafia do
/// dziennika usługi (zwykle tylko komunikat błędu, bo monitor pisze własny dziennik, a z --quiet nie pisze na konsolę).
/// </summary>
internal sealed class MonitorProcess : IDisposable
{
    private readonly Process process;
    private readonly EventWaitHandle stopEvent;

    private MonitorProcess(Process process, EventWaitHandle stopEvent)
    {
        this.process = process;
        this.stopEvent = stopEvent;
        Exited = WaitForExitAsync();
    }

    public int ProcessId => process.Id;

    /// <summary>Kod wyjścia monitora (po przeczytaniu całego jego wyjścia).</summary>
    public Task<int> Exited { get; }

    public static MonitorProcess Start(ServicePaths paths, ILogger logger)
    {
        var eventName = InstanceNames.MonitorStopEvent(Environment.ProcessId);
        var stopEvent = StopSignal.Create(eventName);
        stopEvent.Reset();
        var info = new ProcessStartInfo(paths.MonitorExecutable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = paths.Data
        };
        foreach (var argument in new[] { "run", "--quiet", "--config", paths.Config, "--stop-event", eventName })
            info.ArgumentList.Add(argument);

        var process = new Process { StartInfo = info };
        process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) logger.LogInformation("monitor: {Line}", e.Data); };
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) logger.LogWarning("monitor: {Line}", e.Data); };
        try
        {
            process.Start();
        }
        catch
        {
            process.Dispose();
            stopEvent.Dispose();
            throw;
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return new MonitorProcess(process, stopEvent);
    }

    /// <summary>Prosi monitor o czyste zatrzymanie i czeka; false – nie skończył w czasie (nie jest zabijany: zabicie w trakcie
    /// rozłączania potrafi zawiesić następny odczyt czujnika).</summary>
    public async Task<bool> StopAsync(TimeSpan timeout)
    {
        stopEvent.Set();
        return await Task.WhenAny(Exited, Task.Delay(timeout)) == Exited;
    }

    private async Task<int> WaitForExitAsync()
    {
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    public void Dispose()
    {
        process.Dispose();
        stopEvent.Dispose();
    }
}
