using System.Buffers.Binary;
using Glebosaurus.FlowerCare;

namespace Glebosaurus.Tests;

public class FlowerCareProtocolTests
{
    [Fact]
    public void ParsesRealtimeFrameCapturedFromTheSensor()
    {
        // 2026-11-24, HHCCJCY01 z firmware 3.3.6 w powietrzu przy lampie (FlowerCareDiagnostics, HISTORIA.md).
        var values = FlowerCareProtocol.ParseRealtime(Convert.FromHexString("260103AE000000000000023C00FB349B"));
        Assert.Equal(new FlowerCareValues(294, 174, 0, 0), values);
        Assert.Null(values.Problem());
    }

    [Fact]
    public void RealtimeFrameWithoutModeChangeIsRejected()
    {
        var error = Assert.Throws<InvalidDataException>(() => FlowerCareProtocol.ParseRealtime(Convert.FromHexString("AABBCCDDEEFF99887766000000000000")));
        Assert.Contains("tryb odczytu", error.Message);
    }

    [Fact]
    public void ParsesBatteryAndFirmware()
    {
        Assert.Equal((100, "3.3.6"), FlowerCareProtocol.ParseFirmware(Convert.FromHexString("6439332E332E36")));
        Assert.Equal((64, "3.1.8"), FlowerCareProtocol.ParseFirmware(Convert.FromHexString("4027332E312E38")));
    }

    [Fact]
    public void ParsesHistoryRecordAndCommands()
    {
        var raw = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(raw, 7200);
        BinaryPrimitives.WriteInt16LittleEndian(raw.AsSpan(4), -15);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(7), 82_386);
        raw[11] = 41;
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(12), 1607);
        var record = FlowerCareProtocol.ParseHistoryRecord(5, raw);
        Assert.Equal((5, 7200u), (record.Index, record.SecondsSinceBoot));
        Assert.Equal(new FlowerCareValues(-15, 82_386, 41, 1607), record.Values);

        Assert.Equal([0xA1, 0x10, 0x00], FlowerCareProtocol.HistoryEntryCommand(16));
        Assert.Equal([0xA1, 0x2C, 0x01], FlowerCareProtocol.HistoryEntryCommand(300));
        Assert.Equal(43, FlowerCareProtocol.ParseHistoryCount(Convert.FromHexString("2B00000000000000")));
        Assert.Equal(80u, FlowerCareProtocol.ParseDeviceTime(Convert.FromHexString("50000000")));
    }

    [Theory]
    [InlineData(101, 0, 0, "wilgotność")]
    [InlineData(50, 200_001, 0, "światło")]
    [InlineData(50, 0, 20_001, "przewodność")]
    public void ValuesOutsideRangesAreProblems(int moisture, int light, int conductivity, string expected) =>
        Assert.Contains(expected, new FlowerCareValues(200, light, moisture, conductivity).Problem());

    [Theory]
    [InlineData("71209800043C78147E855C0D0910020000", "5C:85:7E:14:78:3C", "przewodność 0 µS/cm")]
    [InlineData("71209800053C78147E855C0D0410022501", "5C:85:7E:14:78:3C", "temperatura 29.3 °C")]
    [InlineData("71209800033C78147E855C0D08100100", "5C:85:7E:14:78:3C", "wilgotność 0%")]
    [InlineData("712098003c1dd6658d7cc40d0910024706", "C4:7C:8D:65:D6:1D", "przewodność 1607 µS/cm")]
    [InlineData("95FE71209800043C78147E855C0D0710035A0000", "5C:85:7E:14:78:3C", "światło 90 lx")]
    public void ParsesMiBeaconAdvertisements(string hex, string mac, string description)
    {
        var beacon = MiBeacon.TryParse(Convert.FromHexString(hex));
        Assert.NotNull(beacon);
        Assert.True(beacon.IsFlowerCare);
        Assert.False(beacon.Encrypted);
        Assert.Equal(mac, beacon.Mac);
        Assert.Equal(description, beacon.DescribeObject());
    }

    [Fact]
    public void RecognisesTheAdvertisedName()
    {
        Assert.True(FlowerCareAdvertising.IsFlowerCareName("Flower care"));
        Assert.False(FlowerCareAdvertising.IsFlowerCareName("TY"));
        Assert.Null(MiBeacon.TryParse([0x71, 0x20]));
    }
}

public class FlowerCareSessionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Czujnik włożony do baterii 50 h 20 min temu; rekordy co godzinę od pierwszej pełnej godziny pracy.</summary>
    private const uint DeviceTime = 50 * 3600 + 1200;

    private static List<(uint Seconds, FlowerCareValues Values)> Hourly(int count, uint firstAt = 3600) =>
        Enumerable.Range(0, count).Select(i => (firstAt + (uint)i * 3600, new FlowerCareValues(200 + i, 1000 * i, 30 + i % 50, 500 + i))).ToList();

    private static DateTimeOffset WallTime(uint seconds) => Now.AddSeconds(-(double)(DeviceTime - seconds));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReadsHistoryNewerThanTheLastKnownReadingInEitherOrder(bool newestFirst)
    {
        var records = Hourly(50);
        var device = new FakeFlowerCare(records, DeviceTime, newestFirst);
        var session = new FlowerCareSession(device, new ManualTimeProvider(Now));

        var since = WallTime(records[46].Seconds).AddMinutes(5);           // ostatni znany odczyt 5 min po rekordzie 46
        var result = await session.ReadAsync(since, FlowerCareHistoryOptions.Default, CancellationToken.None);

        Assert.Equal(new FlowerCareValues(294, 174, 0, 0), result.Current);
        Assert.Equal((100, "3.3.6", 50), (result.BatteryPercent, result.Firmware, result.StoredRecords));
        Assert.Equal(records.Skip(47).Select(r => (WallTime(r.Seconds), r.Values)), result.History.Select(h => (h.At, h.Values)));
        Assert.Empty(result.Notes);
        Assert.Equal(new[] { 0, 49 }.Concat(newestFirst ? [1, 2, 3] : [48, 47, 46]), device.Requested);
        Assert.False(device.Cleared);
    }

    [Fact]
    public async Task WithoutKnownReadingsTakesHistoryUpToTheLimit()
    {
        var device = new FakeFlowerCare(Hourly(50), DeviceTime, newestFirst: true);
        var result = await new FlowerCareSession(device, new ManualTimeProvider(Now))
            .ReadAsync(null, new FlowerCareHistoryOptions(10, TimeSpan.FromMinutes(2)), CancellationToken.None);

        Assert.Equal(10, result.History.Count);
        Assert.Equal(WallTime(49 * 3600 + 3600), result.History[^1].At);
        Assert.True(result.History.Zip(result.History.Skip(1)).All(p => p.First.At < p.Second.At));
        Assert.Contains(result.Notes, n => n.Contains("urwana limitem"));
    }

    [Fact]
    public async Task RecordsFromBeforeABatterySwapEndTheHistory()
    {
        // Nowy start czujnika 2 h 30 min temu (zegar 9000 s), w pamięci jeszcze rekordy z poprzedniego startu (do 400 h).
        var previousBoot = Hourly(3, firstAt: 398 * 3600);
        var currentBoot = Hourly(2, firstAt: 3600);
        var device = new FakeFlowerCare([.. previousBoot, .. currentBoot], 9000, newestFirst: false);
        var result = await new FlowerCareSession(device, new ManualTimeProvider(Now)).ReadAsync(null, FlowerCareHistoryOptions.Default, CancellationToken.None);

        Assert.Equal(currentBoot.Select(r => Now.AddSeconds(-(9000.0 - r.Seconds))), result.History.Select(h => h.At));
    }

    [Fact]
    public async Task HistoryWithoutAFreshEndIsSkipped()
    {
        // Ostatni rekord sprzed 5 h – zegar się nie zgadza (np. rekordy z innego startu); lepiej nic niż złe czasy.
        var device = new FakeFlowerCare(Hourly(10), 15 * 3600, newestFirst: false);
        var result = await new FlowerCareSession(device, new ManualTimeProvider(Now)).ReadAsync(null, FlowerCareHistoryOptions.Default, CancellationToken.None);

        Assert.Empty(result.History);
        Assert.Contains(result.Notes, n => n.Contains("historia pominięta"));
    }

    [Fact]
    public async Task SkipsRecordsOutsideRanges()
    {
        var records = Hourly(5);
        records[3] = (records[3].Seconds, records[3].Values with { MoisturePercent = 180 });
        var device = new FakeFlowerCare(records, 5 * 3600 + 60, newestFirst: false);
        var result = await new FlowerCareSession(device, new ManualTimeProvider(Now)).ReadAsync(null, FlowerCareHistoryOptions.Default, CancellationToken.None);

        Assert.Equal(4, result.History.Count);
        Assert.Contains(result.Notes, n => n.Contains("rekord historii 3 pominięty"));
    }

    [Fact]
    public async Task WithoutHistoryOptionsReadsOnlyTheCurrentState()
    {
        var device = new FakeFlowerCare(Hourly(5), 5 * 3600 + 60, newestFirst: false);
        var result = await new FlowerCareSession(device, new ManualTimeProvider(Now)).ReadAsync(null, FlowerCareHistoryOptions.None, CancellationToken.None);

        Assert.Empty(result.History);
        Assert.Empty(device.Requested);
    }

    [Fact]
    public async Task CurrentStateOutsideRangesIsAnError()
    {
        var device = new FakeFlowerCare([], 60, newestFirst: false) { Realtime = Convert.FromHexString("26010300000000FF000000000000000000") };
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new FlowerCareSession(device, new ManualTimeProvider(Now)).ReadAsync(null, FlowerCareHistoryOptions.Default, CancellationToken.None));
        Assert.Contains("wilgotność", error.Message);
    }
}

/// <summary>Atrapa czujnika Flower Care na poziomie charakterystyk GATT (zachowanie jak na sprzęcie 2026-11-24).</summary>
internal sealed class FakeFlowerCare(IReadOnlyList<(uint Seconds, FlowerCareValues Values)> recordsOldestFirst, uint deviceTime, bool newestFirst)
    : IGattClient
{
    public byte[] Realtime { get; init; } = Convert.FromHexString("260103AE000000000000023C00FB349B");
    public List<int> Requested { get; } = [];
    public bool Cleared { get; private set; }

    private bool realtimeMode;
    private bool historyMode;
    private int? entry;

    private (uint Seconds, FlowerCareValues Values) Record(int index) =>
        recordsOldestFirst[newestFirst ? recordsOldestFirst.Count - 1 - index : index];

    public Task<byte[]> ReadAsync(string service, string characteristic, CancellationToken cancellationToken)
    {
        byte[] value = (service, characteristic) switch
        {
            (FlowerCareProtocol.DataService, FlowerCareProtocol.FirmwareCharacteristic) => Convert.FromHexString("6439332E332E36"),
            (FlowerCareProtocol.DataService, FlowerCareProtocol.RealtimeCharacteristic) =>
                realtimeMode ? Realtime : Convert.FromHexString("AABBCCDDEEFF99887766000000000000"),
            (FlowerCareProtocol.HistoryService, FlowerCareProtocol.DeviceTimeCharacteristic) => LittleEndian(deviceTime),
            (FlowerCareProtocol.HistoryService, FlowerCareProtocol.HistoryDataCharacteristic) when historyMode && entry is { } index => Encode(Record(index)),
            (FlowerCareProtocol.HistoryService, FlowerCareProtocol.HistoryDataCharacteristic) when historyMode =>
                [(byte)recordsOldestFirst.Count, (byte)(recordsOldestFirst.Count >> 8), 0, 0, 0xBA, 0x13, 0x08, 0, 0xD8, 0x15, 0x08, 0, 0, 0, 0, 0],
            _ => throw new InvalidOperationException($"Nieoczekiwany odczyt {service}/{characteristic}.")
        };
        return Task.FromResult(value);
    }

    public Task WriteAsync(string service, string characteristic, byte[] value, CancellationToken cancellationToken)
    {
        switch (service, characteristic, value)
        {
            case (FlowerCareProtocol.DataService, FlowerCareProtocol.CommandCharacteristic, [0xA0, 0x1F]): realtimeMode = true; break;
            case (FlowerCareProtocol.HistoryService, FlowerCareProtocol.HistoryControlCharacteristic, [0xA0, 0x00, 0x00]): historyMode = true; entry = null; break;
            case (FlowerCareProtocol.HistoryService, FlowerCareProtocol.HistoryControlCharacteristic, [0xA1, var low, var high]):
                entry = low | high << 8;
                Requested.Add(entry.Value);
                break;
            case (FlowerCareProtocol.HistoryService, FlowerCareProtocol.HistoryControlCharacteristic, [0xA2, 0x00, 0x00]): Cleared = true; break;
            default: throw new InvalidOperationException($"Nieoczekiwany zapis {service}/{characteristic}: {Convert.ToHexString(value)}.");
        }
        return Task.CompletedTask;
    }

    private static byte[] LittleEndian(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] Encode((uint Seconds, FlowerCareValues Values) record)
    {
        var raw = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(raw, record.Seconds);
        BinaryPrimitives.WriteInt16LittleEndian(raw.AsSpan(4), (short)record.Values.TemperatureTenths);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(7), (uint)record.Values.LightLux);
        raw[11] = (byte)record.Values.MoisturePercent;
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(12), (ushort)record.Values.ConductivityMicroSiemens);
        return raw;
    }
}
