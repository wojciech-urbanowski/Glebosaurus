using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Glebosaurus.Core.Logging;

/// <summary>
/// Dziennik w plikach dziennych (glebosaurus-RRRRMMDD.log, usługa: service-RRRRMMDD.log) – monitor i usługa działają po
/// cichu, bez okna konsoli, więc to jedyny ślad ich pracy na Windows. Pliki z dni starszych niż <paramref name="keepDays"/> są usuwane
/// przy pierwszym zapisie każdego dnia (czyli też zaraz po starcie agenta). Pełny dziennik z poprzedniego dnia
/// trafia mailem do administratora (patrz <c>MonitorEngine</c>), stąd niezbędna jest wspólna znajomość nazw plików
/// w <see cref="DailyLogFiles"/>.
/// </summary>
public sealed class FileLoggerProvider(DailyLogFiles files, int keepDays) : ILoggerProvider
{
    private readonly object gate = new();
    private DateOnly lastCleanup;

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose() { }

    private void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var now = DateTime.Now;
        var line = new StringBuilder()
            .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append(" [").Append(Abbreviation(level)).Append("] ")
            .Append(category).Append(": ").Append(message);
        if (exception is not null) line.AppendLine().Append(exception);
        line.AppendLine();

        lock (gate)
        {
            try
            {
                var today = DateOnly.FromDateTime(now);
                Directory.CreateDirectory(files.DirectoryPath);
                File.AppendAllText(files.PathFor(today), line.ToString(), Encoding.UTF8);
                Cleanup(today);
            }
            catch (IOException) { /* brak miejsca lub plik zajęty – dziennik nie może zatrzymać monitora */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void Cleanup(DateOnly today)
    {
        if (lastCleanup == today) return;
        lastCleanup = today;
        files.ApplyRetention(today, keepDays);
    }

    private static string Abbreviation(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC", LogLevel.Debug => "DBG", LogLevel.Information => "INF",
        LogLevel.Warning => "WRN", LogLevel.Error => "ERR", LogLevel.Critical => "CRT", _ => "???"
    };

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) provider.Write(logLevel, category, formatter(state, exception), exception);
        }
    }
}
