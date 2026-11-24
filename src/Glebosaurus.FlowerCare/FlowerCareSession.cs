namespace Glebosaurus.FlowerCare;

/// <summary>Pomiar z historii czujnika z czasem przeliczonym na zegar rzeczywisty.</summary>
public sealed record FlowerCareHistoryReading(DateTimeOffset At, FlowerCareValues Values);

/// <summary>Wynik jednej sesji z czujnikiem Flower Care.</summary>
/// <param name="History">Rekordy z pamięci czujnika nowsze niż ostatni znany odczyt, od najstarszego.</param>
/// <param name="StoredRecords">Ile rekordów czujnik trzyma w pamięci (historii nie kasujemy).</param>
public sealed record FlowerCareResult(
    FlowerCareValues Current, int BatteryPercent, string Firmware, IReadOnlyList<FlowerCareHistoryReading> History,
    int StoredRecords, IReadOnlyList<string> Notes);

/// <summary>Limity pobierania historii w jednej sesji.</summary>
/// <param name="MaxRecords">Najwięcej rekordów (godzinnych) na sesję; 0 – bez historii.</param>
/// <param name="Deadline">Najdłużej tyle trwa pobieranie historii (każdy rekord to zapis i odczyt GATT).</param>
public sealed record FlowerCareHistoryOptions(int MaxRecords, TimeSpan Deadline)
{
    /// <summary>10 dób godzinnych rekordów, najwyżej 2 minuty.</summary>
    public static FlowerCareHistoryOptions Default { get; } = new(240, TimeSpan.FromMinutes(2));

    public static FlowerCareHistoryOptions None { get; } = new(0, TimeSpan.Zero);
}

/// <summary>
/// Sesja odczytu czujnika Flower Care na już połączonym kanale GATT: bateria i firmware, pomiar bieżący (po przełączeniu w
/// tryb odczytu), potem historia – od najnowszego rekordu wstecz, aż do ostatniego znanego odczytu.
/// <para>
/// Historii nie kasujemy (zostaje też dla aplikacji Flower Care), więc czujnik trzyma coraz więcej rekordów, a sesja czyta
/// tylko ich końcówkę. Czas rekordu to sekundy od startu czujnika (włożenia baterii); na zegar rzeczywisty przelicza go
/// zegar czujnika odczytany w tej samej sesji. Który koniec pamięci jest najnowszy, rozstrzyga zegar: najnowszy rekord ma
/// czas nie późniejszy niż zegar czujnika i najwyżej <see cref="NewestRecordMaxAge"/> starszy (czujnik zapisuje rekord co
/// godzinę). Idąc wstecz, czas musi maleć – rekord z większym czasem pochodzi sprzed wymiany baterii (zegar liczył wtedy
/// od innej chwili) i kończy historię.
/// </para>
/// </summary>
public sealed class FlowerCareSession(IGattClient gatt, TimeProvider clock, Action<string>? log = null)
{
    /// <summary>Najnowszy rekord nie może być starszy (czujnik zapisuje co godzinę; zapas na opóźnienie zapisu).</summary>
    public static readonly TimeSpan NewestRecordMaxAge = TimeSpan.FromMinutes(90);

    /// <param name="historySince">Czas ostatniego znanego odczytu czujnika: starsze rekordy już są w bazie. null – czujnik
    /// bez odczytów: historia do limitu z <paramref name="options"/>.</param>
    public async Task<FlowerCareResult> ReadAsync(DateTimeOffset? historySince, FlowerCareHistoryOptions options, CancellationToken cancellationToken)
    {
        var notes = new List<string>();
        var (battery, firmware) = FlowerCareProtocol.ParseFirmware(await ReadAsync(FlowerCareProtocol.DataService, FlowerCareProtocol.FirmwareCharacteristic, cancellationToken));
        log?.Invoke($"Bateria {battery}%, firmware {firmware}.");

        await gatt.WriteAsync(FlowerCareProtocol.DataService, FlowerCareProtocol.CommandCharacteristic, FlowerCareProtocol.RealtimeModeCommand, cancellationToken);
        var current = FlowerCareProtocol.ParseRealtime(await ReadAsync(FlowerCareProtocol.DataService, FlowerCareProtocol.RealtimeCharacteristic, cancellationToken));
        if (current.Problem() is { } problem) throw new InvalidDataException($"Pomiar bieżący poza zakresem: {problem}.");

        if (options.MaxRecords <= 0) return new(current, battery, firmware, [], 0, notes);
        var (history, stored) = await ReadHistoryAsync(historySince, options, notes, cancellationToken);
        return new(current, battery, firmware, history, stored, notes);
    }

    private async Task<(IReadOnlyList<FlowerCareHistoryReading> History, int Stored)> ReadHistoryAsync(
        DateTimeOffset? since, FlowerCareHistoryOptions options, List<string> notes, CancellationToken cancellationToken)
    {
        var started = clock.GetTimestamp();
        await gatt.WriteAsync(FlowerCareProtocol.HistoryService, FlowerCareProtocol.HistoryControlCharacteristic, FlowerCareProtocol.HistoryModeCommand, cancellationToken);
        var count = FlowerCareProtocol.ParseHistoryCount(await ReadAsync(FlowerCareProtocol.HistoryService, FlowerCareProtocol.HistoryDataCharacteristic, cancellationToken));
        var deviceTime = FlowerCareProtocol.ParseDeviceTime(await ReadAsync(FlowerCareProtocol.HistoryService, FlowerCareProtocol.DeviceTimeCharacteristic, cancellationToken));
        var clockAt = clock.GetUtcNow();
        log?.Invoke($"Historia: {count} rekordów, zegar czujnika {deviceTime} s od startu.");
        if (count == 0) return ([], 0);

        DateTimeOffset WallTime(uint seconds) =>
            DateTimeOffset.FromUnixTimeSeconds(clockAt.ToUnixTimeSeconds() - (long)(deviceTime - seconds));
        bool CouldBeNewest(FlowerCareHistoryRecord record) =>
            record.SecondsSinceBoot <= deviceTime && deviceTime - record.SecondsSinceBoot <= NewestRecordMaxAge.TotalSeconds;

        // Który koniec pamięci jest najnowszy – po zegarze (opis klasy).
        var first = await RecordAsync(0, cancellationToken);
        var last = count > 1 ? await RecordAsync(count - 1, cancellationToken) : first;
        FlowerCareHistoryRecord newest;
        if (CouldBeNewest(first) && (!CouldBeNewest(last) || first.SecondsSinceBoot >= last.SecondsSinceBoot)) newest = first;
        else if (CouldBeNewest(last)) newest = last;
        else
        {
            notes.Add($"historia pominięta: żaden koniec pamięci ({count} rekordów) nie jest świeżym rekordem według zegara czujnika ({deviceTime} s; rekordy {first.SecondsSinceBoot} i {last.SecondsSinceBoot} s)");
            return ([], count);
        }
        var step = newest.Index == 0 ? 1 : -1;

        var result = new List<FlowerCareHistoryReading>();
        var record = newest;
        uint? previous = null;
        while (true)
        {
            if (previous is { } later && record.SecondsSinceBoot >= later)
            {
                log?.Invoke($"Historia: rekord {record.Index} sprzed wymiany baterii – koniec.");
                break;
            }
            var at = WallTime(record.SecondsSinceBoot);
            if (since is { } known && at <= known) break;
            if (record.Values.Problem() is { } problem) notes.Add($"rekord historii {record.Index} pominięty: {problem}");
            else result.Add(new(at, record.Values));
            previous = record.SecondsSinceBoot;

            var next = record.Index + step;
            if (next < 0 || next >= count) break;
            if (result.Count >= options.MaxRecords || clock.GetElapsedTime(started) >= options.Deadline)
            {
                notes.Add($"historia urwana limitem sesji po {result.Count} rekordach (starsze zostają w czujniku, ale nie trafią do bazy)");
                break;
            }
            record = next == first.Index ? first : next == last.Index ? last : await RecordAsync(next, cancellationToken);
        }
        result.Reverse();
        return (result, count);
    }

    private async Task<FlowerCareHistoryRecord> RecordAsync(int index, CancellationToken cancellationToken)
    {
        await gatt.WriteAsync(FlowerCareProtocol.HistoryService, FlowerCareProtocol.HistoryControlCharacteristic,
            FlowerCareProtocol.HistoryEntryCommand(index), cancellationToken);
        return FlowerCareProtocol.ParseHistoryRecord(index,
            await ReadAsync(FlowerCareProtocol.HistoryService, FlowerCareProtocol.HistoryDataCharacteristic, cancellationToken));
    }

    private Task<byte[]> ReadAsync(string service, string characteristic, CancellationToken cancellationToken) =>
        gatt.ReadAsync(service, characteristic, cancellationToken);
}
