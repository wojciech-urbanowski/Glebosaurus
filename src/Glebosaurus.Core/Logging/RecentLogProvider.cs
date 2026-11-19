using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Glebosaurus.Core.Logging;

/// <summary>
/// Ostatnie wpisy dziennika w pamięci – do ekranu agenta Android, gdzie plików dziennika nikt nie otworzy. Krótko, pod wąski
/// ekran: godzina, poziom i treść (bez kategorii i śladu stosu – pełny wpis jest w pliku dnia).
/// </summary>
public sealed class RecentLogProvider(int capacity = 200, LogLevel minimum = LogLevel.Information, TimeProvider? clock = null) : ILoggerProvider
{
    private readonly Queue<string> lines = new();
    private readonly Lock gate = new();
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly LogLevel threshold = minimum;

    /// <summary>Kopia wpisów od najstarszego.</summary>
    public IReadOnlyList<string> Lines()
    {
        lock (gate) return [.. lines];
    }

    public ILogger CreateLogger(string categoryName) => new RecentLogger(this);

    public void Dispose() { }

    private void Write(LogLevel level, string message, Exception? exception)
    {
        var at = time.GetLocalNow().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        var text = exception is null ? message : $"{message} ({exception.GetType().Name}: {exception.Message})";
        var line = $"{at} {Abbreviation(level)} {text}";
        lock (gate)
        {
            lines.Enqueue(line);
            while (lines.Count > capacity) lines.Dequeue();
        }
    }

    private static string Abbreviation(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC", LogLevel.Debug => "DBG", LogLevel.Information => "INF",
        LogLevel.Warning => "WRN", LogLevel.Error => "ERR", LogLevel.Critical => "CRT", _ => "???"
    };

    private sealed class RecentLogger(RecentLogProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= provider.threshold;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) provider.Write(logLevel, formatter(state, exception), exception);
        }
    }
}
