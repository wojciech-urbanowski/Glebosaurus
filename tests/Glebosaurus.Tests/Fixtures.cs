using System.Runtime.CompilerServices;
using Glebosaurus.Core.Config;
using Glebosaurus.Core.Domain;
using Glebosaurus.Core.Mail;
using Glebosaurus.Core.Monitoring;
using Glebosaurus.Data;
using Glebosaurus.Tuya;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Glebosaurus.Tests;

internal static class Fixtures
{
    public static readonly DateTimeOffset T0 = new(2026, 11, 13, 6, 0, 0, TimeSpan.Zero);

    public static SensorConfig Sensor(string id = "s1", string name = "Monstera", string uuid = "07aaad78f40b0a74") => new()
    {
        Id = id, Name = name,
        Tuya = new() { Mac = "DC:23:51:CC:03:C9", Uuid = uuid, DeviceId = "1234567890123456789012", LocalKeyHex = "a1b2c3d4e5f6", ProductId = "gvygg3m8" }
    };

    /// <summary>Konfiguracja agenta, jaką składa <c>CloudConfigMapper</c> (czujniki z chmury, dostęp do chmury z pliku).</summary>
    public static AgentConfig Config(params SensorConfig[] sensors) => new()
    {
        MonitorName = "Test",
        // Testy liczą czas w odstępach 15 min – stały tutaj, niezależnie od domyślnego w AgentConfig.
        ReadIntervalMinutes = 15,
        Sensors = sensors.Length > 0 ? [.. sensors] : [Sensor()],
        Reports = new() { Times = [], CatchUpHours = 3 },
        Cloud = CloudAccess()
    };

    public static CloudAccessConfig CloudAccess() => new() { Url = "https://abcdefghijklmnopqrst.supabase.co", PublishableKey = "sb_publishable_x" };

    public static StoredReading Reading(string sensorId, DateTimeOffset at, int? moisture = 50, int? battery = 80, decimal? temperature = 22.5m, int? state = 2) =>
        new(sensorId, at, moisture, temperature, battery, state);

    public static SensorReadOutcome Ok(SensorConfig sensor, int? moisture, int? battery = 80, decimal? temperature = 22.5m, params SensorReading[] earlier) =>
        new(sensor, Soil(T0, moisture, battery, temperature), SensorReadFailure.None) { Earlier = earlier };

    public static SensorReading Soil(DateTimeOffset at, int? moisture, int? battery = 80, decimal? temperature = 22.5m) =>
        new(at, moisture, temperature, battery, 2);

    /// <summary>Czujnik Xiaomi Flower Care (bez klucza, po MAC).</summary>
    public static SensorConfig FlowerCare(string id = "fc", string name = "Fikus", string mac = "5C:85:7E:14:78:3C") => new()
    {
        Id = id, Name = name, Model = SensorModel.FlowerCare, Mac = mac
    };

    public static string TempDatabase() => Path.Combine(Path.GetTempPath(), $"glebosaurus-test-{Guid.NewGuid():N}.db");

    public static void DeleteDatabase(string path)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(path + suffix); } catch (IOException) { }
    }
}

/// <summary>Czas sterowany z testu, żeby „minęło 25 godzin” trwało ułamek sekundy.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset now = start;
    public override DateTimeOffset GetUtcNow() => now;
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    // Zegar monotoniczny idzie razem z ręcznym czasem (dzierżawy agenta liczą ważność na nim).
    public override long GetTimestamp() => now.UtcTicks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public void Advance(TimeSpan span) => now += span;
}

internal sealed class FakeSensorReader : ISensorReader
{
    public Func<SensorConfig, SensorReadOutcome> Script { get; set; } = sensor => Fixtures.Ok(sensor, 50);
    public List<string[]> Calls { get; } = [];

    /// <summary>Granice historii z każdego wywołania (ostatni znany odczyt czujników Flower Care).</summary>
    public List<IReadOnlyDictionary<string, DateTimeOffset>> HistorySince { get; } = [];

    public async IAsyncEnumerable<SensorReadOutcome> ReadAsync(IReadOnlyList<SensorConfig> sensors, TimeSpan scanWindow,
        IReadOnlyDictionary<string, DateTimeOffset> historySince, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Calls.Add(sensors.Select(s => s.Id).ToArray());
        HistorySince.Add(historySince);
        foreach (var sensor in sensors)
        {
            await Task.Yield();
            yield return Script(sensor);
        }
    }
}

internal sealed class FakeMail : IMailGateway
{
    public List<MailMessage> Sent { get; } = [];
    public int Attempts { get; private set; }
    public Exception? Failure { get; set; }

    /// <summary>Błąd tylko dla wybranych wiadomości (np. tylko dla logu administratora), reszta przechodzi.</summary>
    public Func<MailMessage, Exception?>? FailWhen { get; set; }

    public Task SendAsync(MailMessage message, CancellationToken cancellationToken)
    {
        Attempts++;
        if ((Failure ?? FailWhen?.Invoke(message)) is { } failure) throw failure;
        Sent.Add(message);
        return Task.CompletedTask;
    }
}

/// <summary>Archiwum dziennika w pamięci: dzień → treść; zapamiętuje, które dni zostały odczytane.</summary>
internal sealed class FakeLogArchive : ILogArchive
{
    public Dictionary<DateOnly, string> Days { get; } = [];
    public List<DateOnly> Reads { get; } = [];

    public Task<string?> ReadDayAsync(DateOnly day, CancellationToken cancellationToken)
    {
        Reads.Add(day);
        return Task.FromResult(Days.TryGetValue(day, out var text) ? text : null);
    }
}

/// <summary>Baza SQLite w pliku tymczasowym, usuwana po teście.</summary>
internal sealed class TempStore : IDisposable
{
    public TempStore()
    {
        Path = Fixtures.TempDatabase();
        Store = new SqliteMonitorStore(Path);
        Store.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public string Path { get; }
    public SqliteMonitorStore Store { get; }

    public void Dispose() => Fixtures.DeleteDatabase(Path);
}

/// <summary>Dziennik w pamięci: pozwala sprawdzić, co silnik zapisał (już po sformatowaniu komunikatu).</summary>
internal sealed class CapturingLogger : ILogger
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception)));
}

