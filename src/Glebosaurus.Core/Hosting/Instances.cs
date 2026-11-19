using System.Runtime.Versioning;

namespace Glebosaurus.Core.Hosting;

/// <summary>
/// Nazwy obiektów systemowych, przez które monitor (<c>run</c>), usługa (<c>glebosaurus-service</c>), polecenie <c>stop</c>
/// i skrypt <c>deploy/install-windows.ps1</c> wiedzą o sobie nawzajem. „Local\” = sesja zalogowanego użytkownika (Windows).
/// Te same nazwy są wpisane w skrypt – zmieniaj je w obu miejscach.
/// </summary>
public static class InstanceNames
{
    /// <summary>Trzyma go działający monitor: drugi <c>run</c> (np. deweloperski obok usługi) nie wystartuje.</summary>
    public const string MonitorMutex = @"Local\Glebosaurus.Monitor";

    /// <summary>Trzyma go działająca usługa (nadzorca monitora).</summary>
    public const string ServiceMutex = @"Local\Glebosaurus.Service";

    /// <summary>Prośba o czyste zatrzymanie monitora i usługi (polecenie <c>stop</c>, skrypt przed aktualizacją).</summary>
    public const string StopEvent = @"Local\Glebosaurus.Stop";

    /// <summary>Prywatny sygnał zatrzymania monitora uruchomionego przez usługę (usługa sama nie musi się wtedy kończyć).</summary>
    public static string MonitorStopEvent(int serviceProcessId) => $@"Local\Glebosaurus.Stop.{serviceProcessId}";
}

/// <summary>Kody wyjścia <c>glebosaurus</c>, na które reaguje usługa i skrypt.</summary>
public static class ExitCodes
{
    public const int Success = 0;
    public const int Error = 1;
    public const int NotFound = 2;
    public const int NoData = 3;

    /// <summary>Monitor już działa na tym koncie (usługa albo inne <c>run</c>).</summary>
    public const int AlreadyRunning = 4;

    public const int Interrupted = 130;
}

/// <summary>
/// Blokada „jedna instancja” na nazwanym muteksie. Muteksem włada osobny wątek: zwolnić go może tylko wątek, który go
/// zajął, a kod asynchroniczny kończy się na dowolnym wątku. Gdy proces zginie, system i tak zwalnia muteks.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly ManualResetEventSlim release;
    private readonly Thread owner;

    private SingleInstance(ManualResetEventSlim release, Thread owner)
    {
        this.release = release;
        this.owner = owner;
    }

    /// <summary>Null, gdy muteks trzyma już ktoś inny.</summary>
    public static SingleInstance? TryAcquire(string name)
    {
        var acquired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new ManualResetEventSlim();
        var owner = new Thread(() =>
        {
            try
            {
                using var mutex = new Mutex(false, name);
                bool owned;
                try { owned = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { owned = true; }   // poprzedni właściciel zginął – teraz nasz
                acquired.TrySetResult(owned);
                if (!owned) return;
                release.Wait();
                mutex.ReleaseMutex();
            }
            catch (Exception e)
            {
                acquired.TrySetException(e);
            }
        })
        { IsBackground = true, Name = "Glebosaurus single instance" };
        owner.Start();
        if (acquired.Task.GetAwaiter().GetResult()) return new SingleInstance(release, owner);
        owner.Join();
        release.Dispose();
        return null;
    }

    /// <summary>Czy ktoś trzyma muteks (sprawdza, zajmując go na chwilę).</summary>
    public static bool IsHeld(string name)
    {
        using var probe = TryAcquire(name);
        return probe is null;
    }

    public void Dispose()
    {
        release.Set();
        owner.Join();
        release.Dispose();
    }
}

/// <summary>
/// „Jedna instancja” na Linuksie: wyłączna blokada pliku obok konfiguracji agenta (<c>FileShare.None</c> = <c>flock</c>).
/// Nazwany muteks .NET z „Local\” obejmuje na Linuksie tylko jedną sesję, a usługa systemd i polecenie wpisane w terminalu
/// (np. <c>sudo -u glebosaurus … run</c>) mają różne sesje – dwóch agentów dzieliłoby wtedy token i bazę. Blokadę zwalnia
/// system, gdy proces zginie.
/// </summary>
public sealed class InstanceLockFile : IDisposable
{
    public const string FileName = "glebosaurus.run.lock";

    private readonly FileStream stream;

    private InstanceLockFile(FileStream stream) => this.stream = stream;

    /// <summary>Null, gdy plik trzyma już inny proces.</summary>
    public static InstanceLockFile? TryAcquire(string path)
    {
        try
        {
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            stream.SetLength(0);
            var pid = System.Text.Encoding.ASCII.GetBytes(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
            stream.Write(pid);
            stream.Flush();
            return new InstanceLockFile(stream);
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Dispose() => stream.Dispose();
}

/// <summary>Nazwane zdarzenie „zatrzymaj się” (tylko Windows – na Linuksie zatrzymuje SIGTERM z systemd).</summary>
[SupportedOSPlatform("windows")]
public static class StopSignal
{
    /// <summary>
    /// Woła <paramref name="onSignal"/> (raz, na wątku puli), gdy ktoś ustawi zdarzenie. <paramref name="reset"/> czyści
    /// sygnał pozostały po poprzednim zatrzymaniu – używa tego usługa przy starcie.
    /// </summary>
    public static IDisposable Listen(string name, Action onSignal, bool reset = false)
    {
        var handle = new EventWaitHandle(false, EventResetMode.ManualReset, name);
        if (reset) handle.Reset();
        var registration = ThreadPool.RegisterWaitForSingleObject(handle, (_, _) => onSignal(), null, Timeout.Infinite, executeOnlyOnce: true);
        return new Listener(handle, registration);
    }

    /// <summary>Tworzy zdarzenie (do sygnalizowania później przez <see cref="EventWaitHandle.Set"/>).</summary>
    public static EventWaitHandle Create(string name) => new(false, EventResetMode.ManualReset, name);

    /// <summary>Ustawia zdarzenie, jeśli ktoś na nie czeka; false – nikt go nie utworzył.</summary>
    public static bool Signal(string name)
    {
        if (!EventWaitHandle.TryOpenExisting(name, out var handle)) return false;
        using (handle) handle.Set();
        return true;
    }

    private sealed class Listener(EventWaitHandle handle, RegisteredWaitHandle registration) : IDisposable
    {
        public void Dispose()
        {
            registration.Unregister(null);
            handle.Dispose();
        }
    }
}

/// <summary>
/// Odstęp przed ponownym uruchomieniem monitora po jego zakończeniu: 1, 2, 5, potem co 15 min; po godzinie spokojnej pracy
/// licznik się zeruje. „Monitor już działa” (np. deweloperski <c>run</c>) to nie awaria – ponowna próba co 5 min.
/// </summary>
public sealed class RestartBackoff
{
    private static readonly TimeSpan[] Steps =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)];

    public static readonly TimeSpan HealthyRun = TimeSpan.FromHours(1);
    public static readonly TimeSpan AlreadyRunningDelay = TimeSpan.FromMinutes(5);

    private int failures;

    public TimeSpan Next(int exitCode, TimeSpan ranFor)
    {
        if (exitCode == ExitCodes.AlreadyRunning) return AlreadyRunningDelay;
        if (ranFor >= HealthyRun) failures = 0;
        var delay = Steps[Math.Min(failures, Steps.Length - 1)];
        failures++;
        return delay;
    }
}
