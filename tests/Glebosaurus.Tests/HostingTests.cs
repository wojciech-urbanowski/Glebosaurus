using Glebosaurus.Core.Hosting;

namespace Glebosaurus.Tests;

public class SingleInstanceTests
{
    private static string UniqueName() => $@"Local\Glebosaurus.Test.{Guid.NewGuid():N}";

    [Fact]
    public void SecondAcquireFailsUntilTheFirstIsReleased()
    {
        var name = UniqueName();
        var first = SingleInstance.TryAcquire(name);
        Assert.NotNull(first);
        Assert.True(SingleInstance.IsHeld(name));
        Assert.Null(SingleInstance.TryAcquire(name));

        first.Dispose();

        Assert.False(SingleInstance.IsHeld(name));
        using var again = SingleInstance.TryAcquire(name);
        Assert.NotNull(again);
    }

    [Fact]
    public async Task CanBeReleasedFromAnotherThread()
    {
        var name = UniqueName();
        var instance = SingleInstance.TryAcquire(name)!;

        await Task.Run(instance.Dispose, TestContext.Current.CancellationToken);

        Assert.False(SingleInstance.IsHeld(name));
    }

    [Fact]
    public void NobodyHoldsAnUnusedName() => Assert.False(SingleInstance.IsHeld(UniqueName()));
}

public class StopSignalTests
{
    [Fact]
    public async Task SignalReachesTheListenerOnce()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Nazwane zdarzenia są tylko na Windows.");
        if (!OperatingSystem.IsWindows()) return;
        var name = $@"Local\Glebosaurus.Test.Stop.{Guid.NewGuid():N}";
        var calls = 0;
        var signalled = new TaskCompletionSource();
        using (StopSignal.Listen(name, () => { Interlocked.Increment(ref calls); signalled.TrySetResult(); }))
        {
            Assert.True(StopSignal.Signal(name));
            await signalled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.True(StopSignal.Signal(name));
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        Assert.Equal(1, calls);
        Assert.False(StopSignal.Signal(name));   // nikt już nie słucha – zdarzenie zniknęło
    }

    [Fact]
    public async Task ServiceStartClearsAStaleSignal()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Nazwane zdarzenia są tylko na Windows.");
        if (!OperatingSystem.IsWindows()) return;
        var name = $@"Local\Glebosaurus.Test.Stop.{Guid.NewGuid():N}";
        using var stale = StopSignal.Create(name);
        stale.Set();
        var signalled = false;

        using (StopSignal.Listen(name, () => signalled = true, reset: true))
            await Task.Delay(200, TestContext.Current.CancellationToken);

        Assert.False(signalled);
    }
}

public class RestartBackoffTests
{
    [Fact]
    public void GrowsUpToFifteenMinutesAndResetsAfterAHealthyHour()
    {
        var backoff = new RestartBackoff();
        var crash = TimeSpan.FromSeconds(5);

        Assert.Equal(TimeSpan.FromMinutes(1), backoff.Next(1, crash));
        Assert.Equal(TimeSpan.FromMinutes(2), backoff.Next(1, crash));
        Assert.Equal(TimeSpan.FromMinutes(5), backoff.Next(1, crash));
        Assert.Equal(TimeSpan.FromMinutes(15), backoff.Next(1, crash));
        Assert.Equal(TimeSpan.FromMinutes(15), backoff.Next(1, crash));
        Assert.Equal(TimeSpan.FromMinutes(1), backoff.Next(1, TimeSpan.FromHours(2)));
        Assert.Equal(TimeSpan.FromMinutes(2), backoff.Next(1, crash));
    }

    [Fact]
    public void AnotherMonitorRunningIsNotACrash()
    {
        var backoff = new RestartBackoff();

        Assert.Equal(TimeSpan.FromMinutes(5), backoff.Next(ExitCodes.AlreadyRunning, TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromMinutes(5), backoff.Next(ExitCodes.AlreadyRunning, TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromMinutes(1), backoff.Next(ExitCodes.Error, TimeSpan.Zero));
    }
}
