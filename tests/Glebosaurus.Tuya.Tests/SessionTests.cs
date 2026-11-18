namespace Glebosaurus.Tuya.Tests;

public class SessionTests
{
    private static TuyaCredentials Credentials(string localKeyHex = "a1b2c3d4e5f6") => new()
    {
        Mac = "DC:23:51:CC:03:C9", Uuid = "07aaad78f40b0a74", DeviceId = "1234567890123456789012",
        ProductId = "gvygg3m8", LocalKeyHex = localKeyHex
    };

    [Fact]
    public async Task ReadReturnsMappedSoilReadingAndReleasesSubscription()
    {
        var credentials = Credentials();
        var link = new FakeSensorLink(credentials.KeyBytes, bound: true);

        var result = await new TuyaSession(link, credentials, TimeSpan.FromSeconds(5)).ReadAsync(CancellationToken.None);

        Assert.Equal("3.1", result.Protocol);
        Assert.Equal("9.3", result.Firmware);
        Assert.Equal(6, result.DataPoints.Count);
        Assert.False(result.Truncated); // sesja skończyła się ciszą, nie limitem czasu
        Assert.False(link.IsSubscribed);
        Assert.Single(link.PairRequests);
        Assert.Equal(5, link.PairRequests[0].Flag); // klucz sesji, nie jawnie
        Assert.Equal(credentials.PairingPayload(), link.PairRequests[0].Data);

        var reading = SoilSensorProfiles.Map(SoilSensorProfiles.ForProduct(credentials.ProductId)!, result.DataPoints, DateTimeOffset.UnixEpoch);
        Assert.Equal(18, reading.MoisturePercent);
        Assert.Equal(23.7m, reading.SoilTemperatureCelsius);
        Assert.Equal(33, reading.BatteryPercent);
        Assert.Equal(1, reading.BatteryState);
    }

    [Fact]
    public async Task ReadEndsAsSoonAsRequiredDatapointsArrive()
    {
        var credentials = Credentials();
        var link = new FakeSensorLink(credentials.KeyBytes, bound: true);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var result = await new TuyaSession(link, credentials, TimeSpan.FromSeconds(5))
            .ReadAsync(SoilSensorProfiles.IsComplete(SoilSensorProfiles.Sgs01), CancellationToken.None);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1.5), $"sesja trwała {stopwatch.Elapsed}"); // bez 2 s oczekiwania na ciszę
        Assert.Equal(6, result.DataPoints.Count);
    }

    [Fact]
    public async Task OnFrameSeesEverySensorFrameIncludingUnknownOnesButNoHandshake()
    {
        var credentials = Credentials();
        var older = new DateTimeOffset(2026, 11, 13, 6, 0, 0, TimeSpan.Zero);
        var link = new FakeSensorLink(credentials.KeyBytes, bound: true,
        [
            (0x8003, FakeSensorLink.TimedReport(older, FakeSensorLink.Dp(3, 2, FakeSensorLink.Int(21)))),
            (0x8003, FakeSensorLink.TimedReport(older.AddHours(1), FakeSensorLink.Dp(3, 2, FakeSensorLink.Int(19)))),
            (0x8999, [1, 2, 3])
        ]);
        var frames = new List<TuyaFrame>();
        var log = new List<string>();

        await new TuyaSession(link, credentials, TimeSpan.FromSeconds(5), log.Add, quietTimeout: TimeSpan.FromMilliseconds(300), onFrame: frames.Add)
            .ReadAsync(CancellationToken.None);

        Assert.Equal([(ushort)0x8001, 0x8003, 0x8003, 0x8999], frames.Select(f => f.Command));
        Assert.Equal([1, 2, 3], frames[^1].Data);
        // Odpowiedzi uzgadniania (Device Info = 0, parowanie sesji = 1) niosą materiał kluczy i nie trafiają do podglądu.
        Assert.DoesNotContain(frames, f => f.Command is 0 or 1);
        Assert.Equal(2, log.Count(line => line.Contains("czas czujnika")));
    }

    [Fact]
    public async Task HistoryRecordsAreAllKeptAndMapToReadingsOldestFirst()
    {
        var credentials = Credentials();
        var older = new DateTimeOffset(2026, 11, 13, 6, 0, 0, TimeSpan.Zero);
        var link = new FakeSensorLink(credentials.KeyBytes, bound: true,
        [
            (0x8003, FakeSensorLink.TimedReport(older, FakeSensorLink.Dp(3, 2, FakeSensorLink.Int(21)), FakeSensorLink.Dp(5, 2, FakeSensorLink.Int(200)))),
            (0x8003, FakeSensorLink.TimedReport(older.AddHours(1), FakeSensorLink.Dp(3, 2, FakeSensorLink.Int(19)), FakeSensorLink.Dp(5, 2, FakeSensorLink.Int(190))))
        ]);

        var result = await new TuyaSession(link, credentials, TimeSpan.FromSeconds(5), quietTimeout: TimeSpan.FromMilliseconds(300))
            .ReadAsync(CancellationToken.None);

        Assert.Equal(3, result.DataPoints.Count(p => p.Id == 3)); // zwykły raport + dwa rekordy historii
        var readings = SoilSensorProfiles.MapAll(SoilSensorProfiles.Sgs01, result.DataPoints, older.AddDays(1));
        Assert.Equal([older, older.AddHours(1), older.AddDays(1)], readings.Select(r => r.At)); // zegar „dobry”: ostatni = chwila połączenia
        Assert.Equal([21, 19, 18], readings.Select(r => r.MoisturePercent)); // 18 = raport stanu z atrapy
    }

    [Theory]
    [InlineData("dp-length")]      // DP3 typu „wartość” z 2 bajtami zamiast 4
    [InlineData("dp-type")]        // nieznany typ punktu danych
    [InlineData("utf8")]           // DP tekstowy z bajtem, który nie jest UTF-8
    [InlineData("timestamp")]      // nieznany rodzaj znacznika czasu
    [InlineData("ms-digits")]      // znacznik w milisekundach, który nie jest liczbą
    public async Task UnreadableReportIsSkippedAndAcknowledgedWithoutLosingTheRestOfTheSeries(string defect)
    {
        var credentials = Credentials();
        var older = new DateTimeOffset(2026, 11, 13, 6, 0, 0, TimeSpan.Zero);
        byte[] moisture = FakeSensorLink.Dp(3, 2, FakeSensorLink.Int(20));
        byte[] unreadable = defect switch
        {
            "dp-length" => FakeSensorLink.TimedReport(older.AddMinutes(30), FakeSensorLink.Dp(3, 2, 0, 20)),
            "dp-type" => FakeSensorLink.TimedReport(older.AddMinutes(30), FakeSensorLink.Dp(3, 9, 20)),
            "utf8" => FakeSensorLink.TimedReport(older.AddMinutes(30), FakeSensorLink.Dp(20, 3, 0xFF)),
            "timestamp" => [7, 0, 0, 0, 0, .. moisture],
            "ms-digits" => [0, .. "17630000000x0"u8, .. moisture],
            _ => throw new ArgumentOutOfRangeException(nameof(defect))
        };
        var link = new FakeSensorLink(credentials.KeyBytes, bound: true,
        [
            (0x8003, FakeSensorLink.TimedReport(older, FakeSensorLink.Dp(3, 2, FakeSensorLink.Int(21)), FakeSensorLink.Dp(5, 2, FakeSensorLink.Int(200)))),
            (0x8003, unreadable),
            (0x8003, FakeSensorLink.TimedReport(older.AddHours(1), FakeSensorLink.Dp(3, 2, FakeSensorLink.Int(19)), FakeSensorLink.Dp(5, 2, FakeSensorLink.Int(190))))
        ]);

        var result = await new TuyaSession(link, credentials, TimeSpan.FromSeconds(5), quietTimeout: TimeSpan.FromMilliseconds(300))
            .ReadAsync(CancellationToken.None);

        // Odczyt się nie wywraca: zostają zwykły raport (18) i oba czytelne rekordy historii, bez nieczytelnego.
        Assert.Equal([18, 21, 19], result.DataPoints.Where(p => p.Id == 3).Select(p => int.Parse(p.Value)));
        // Potwierdzona jest każda ramka, także nieczytelna – inaczej czujnik mógłby oddawać ją na początku każdej sesji.
        Assert.Equal([(ushort)0x8001, 0x8003, 0x8003, 0x8003], link.Acknowledged);
        var note = Assert.Single(result.Notes, n => n.StartsWith("Pominięto nieczytelne raporty", StringComparison.Ordinal));
        Assert.Contains(": 1 ", note);
        Assert.Contains(Convert.ToHexString(unreadable), note); // surowe bajty do zbadania w dzienniku
        Assert.False(link.IsSubscribed);
    }

    [Fact]
    public async Task SessionDeadlineWithDataReturnsWhatWasReceivedInsteadOfDroppingIt()
    {
        var credentials = Credentials();
        var link = new FakeSensorLink(credentials.KeyBytes, bound: true);

        // Cisza (20 s) jest dłuższa niż limit sesji (2 s): sesja kończy się limitem, ale dane już są (czujnik wciąż mógłby nadawać historię).
        var result = await new TuyaSession(link, credentials, TimeSpan.FromSeconds(5),
            sessionDeadline: TimeSpan.FromSeconds(2), quietTimeout: TimeSpan.FromSeconds(20)).ReadAsync(CancellationToken.None);

        Assert.Equal(6, result.DataPoints.Count);
        Assert.Contains(result.Notes, n => n.Contains("limit czasu"));
        Assert.True(result.Truncated); // ostatni odebrany rekord nie musi być stanem z chwili połączenia
        Assert.False(link.IsSubscribed);
    }

    [Fact]
    public async Task QuietTimeoutDecidesHowLongSessionWaitsForMoreFrames()
    {
        var credentials = Credentials();
        var link = new FakeSensorLink(credentials.KeyBytes, bound: true);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var result = await new TuyaSession(link, credentials, TimeSpan.FromSeconds(5), quietTimeout: TimeSpan.FromMilliseconds(700))
            .ReadAsync(CancellationToken.None);

        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(600), $"sesja zakończyła się po {stopwatch.Elapsed}, bez czekania na ciszę");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1.9), $"sesja trwała {stopwatch.Elapsed}"); // krócej niż domyślne 2 s ciszy
        Assert.Equal(6, result.DataPoints.Count);
    }

    [Fact]
    public async Task SessionDeadlineIsReportedAsTimeoutNotAsCancellation()
    {
        var link = new FakeSensorLink(Credentials("a1b2c3d4e5f6").KeyBytes, bound: true);
        var session = new TuyaSession(link, Credentials("000000000000"), TimeSpan.FromSeconds(30), sessionDeadline: TimeSpan.FromMilliseconds(300));

        var error = await Assert.ThrowsAsync<TimeoutException>(() => session.ReadAsync(CancellationToken.None));

        Assert.Contains("limit czasu", error.Message);
        Assert.False(link.IsSubscribed);
    }

    [Fact]
    public async Task CallerCancellationStillPropagatesAsCancellation()
    {
        var link = new FakeSensorLink(Credentials("a1b2c3d4e5f6").KeyBytes, bound: true);
        var session = new TuyaSession(link, Credentials("000000000000"), TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ReadAsync(cts.Token));
    }

    [Fact]
    public async Task ReadWithWrongKeyTimesOutInsteadOfReportingData()
    {
        var link = new FakeSensorLink(Credentials("a1b2c3d4e5f6").KeyBytes, bound: true);
        var session = new TuyaSession(link, Credentials("000000000000"), TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAsync<TimeoutException>(() => session.ReadAsync(CancellationToken.None));
        Assert.False(link.IsSubscribed);
    }

    [Fact]
    public async Task ReadOfUnboundSensorRequestsRepairing()
    {
        var credentials = Credentials();
        var link = new FakeSensorLink(credentials.KeyBytes, bound: false);

        await Assert.ThrowsAsync<TuyaUnboundException>(() =>
            new TuyaSession(link, credentials, TimeSpan.FromSeconds(5)).ReadAsync(CancellationToken.None));
        Assert.Empty(link.PairRequests);
    }

    [Fact]
    public async Task BindPersistsKeyBeforeTransmittingItInPlainPairingFrame()
    {
        var credentials = Credentials();
        var link = new FakeSensorLink(credentials.KeyBytes, bound: false);
        var transmittedWhenPersisting = -1;

        await new TuyaSession(link, credentials, TimeSpan.FromSeconds(5)).BindAsync(_ =>
        {
            transmittedWhenPersisting = link.PairRequests.Count;
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.Equal(0, transmittedWhenPersisting); // zapis klucza poprzedza transmisję
        var request = Assert.Single(link.PairRequests);
        Assert.Equal(0, request.Flag);              // przypisanie jawnie, jak w sprawdzonym przebiegu
        Assert.Equal(credentials.PairingPayload(), request.Data);
        Assert.False(link.IsSubscribed);
    }
}
