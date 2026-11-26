using Glebosaurus.Core.Logging;
using Microsoft.Extensions.Logging;

namespace Glebosaurus.Tests;

/// <summary>
/// Ostrzeżenie Kestrela o postoju zegara (HeartbeatSlow) przy celowo niskim priorytecie agenta na Windows: krótki postój
/// trafia do dziennika jako informacja (nie liczy się do ostrzeżeń w logu administratora), długi zostaje ostrzeżeniem.
/// </summary>
public sealed class KestrelHeartbeatTests : IDisposable
{
    // Wiernie jak w Kestrelu (KestrelTrace.GeneralLog.HeartbeatSlow, .NET 10/11): poziom, zdarzenie, szablon i kolejność
    // argumentów – sprawdzone 2026-11-26 wywołaniem prawdziwej metody Kestrela (stan LogValues`3 z kluczem heartbeatDuration).
    private static readonly Action<ILogger, DateTimeOffset, TimeSpan, TimeSpan, Exception?> HeartbeatSlow =
        LoggerMessage.Define<DateTimeOffset, TimeSpan, TimeSpan>(LogLevel.Warning, new EventId(22, "HeartbeatSlow"),
            @"As of ""{now}"", the heartbeat has been running for ""{heartbeatDuration}"" which is longer than ""{interval}"". " +
            "This could be caused by thread pool starvation.");

    private static readonly Action<ILogger, string, Exception?> InvalidResponseHeaderRemoved =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(41, "InvalidResponseHeaderRemoved"),
            @"The response header ""{name}"" was removed.");

    private readonly string directory = Path.Combine(Path.GetTempPath(), $"glebosaurus-kestrel-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void ShortStallIsWrittenAsInformation()
    {
        var line = Assert.Single(Write(logger => HeartbeatSlow(logger, Now, TimeSpan.FromSeconds(2.42), TimeSpan.FromSeconds(1), null)));

        Assert.Contains("[INF] Microsoft.AspNetCore.Server.Kestrel: As of", line);
        Assert.Contains("00:00:02.4200000", line);
    }

    [Fact]
    public void LongStallStaysAWarning() =>
        Assert.Contains("[WRN] Microsoft.AspNetCore.Server.Kestrel: As of",
            Assert.Single(Write(logger => HeartbeatSlow(logger, Now, KestrelHeartbeat.WarningFrom, TimeSpan.FromSeconds(1), null))));

    [Fact]
    public void OtherKestrelWarningsStayWarnings() =>
        Assert.Contains("[WRN] Microsoft.AspNetCore.Server.Kestrel: The response header",
            Assert.Single(Write(logger => InvalidResponseHeaderRemoved(logger, "X-Test", null))));

    [Fact]
    public void SameEventFromAnotherCategoryStaysAWarning() =>
        Assert.Contains("[WRN] Glebosaurus:",
            Assert.Single(Write(logger => HeartbeatSlow(logger, Now, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), null), "Glebosaurus")));

    [Fact]
    public void WithoutAReadableDurationItStaysAWarning()
    {
        var eventId = new EventId(22, KestrelHeartbeat.EventName);

        Assert.Equal(LogLevel.Warning, KestrelHeartbeat.Level(KestrelHeartbeat.Category, eventId, LogLevel.Warning, "tekst"));
        Assert.Equal(LogLevel.Warning, KestrelHeartbeat.Level(KestrelHeartbeat.Category, eventId, LogLevel.Warning,
            new List<KeyValuePair<string, object?>> { new("heartbeatDuration", "2 s") }));
    }

    private static readonly DateTimeOffset Now = new(2026, 11, 25, 21, 1, 8, TimeSpan.Zero);

    // Filtry jak w agencie (AgentRuntime.CreateLogging): kategorie Microsoft od ostrzeżeń w górę.
    private string[] Write(Action<ILogger> log, string category = KestrelHeartbeat.Category)
    {
        var files = new DailyLogFiles(directory);
        using (var factory = LoggerFactory.Create(builder =>
               {
                   builder.SetMinimumLevel(LogLevel.Information);
                   builder.AddFilter("Microsoft", LogLevel.Warning);
                   builder.AddProvider(new FileLoggerProvider(files, keepDays: 7));
               }))
            log(factory.CreateLogger(category));
        return [.. Directory.GetFiles(directory, "*.log").SelectMany(File.ReadAllLines).Where(line => line.Length > 0)];
    }
}
