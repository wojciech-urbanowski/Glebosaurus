using System.Text;

namespace Glebosaurus.Tuya.Tests;

public class ProtocolTests
{
    [Fact]
    public void CrcMatchesModbusReference() => Assert.Equal(0x4B37, TuyaProtocol.Crc16("123456789"u8));

    [Fact]
    public void RealAdvertisementOfBoundSensorGivesUuidButHidesProduct()
    {
        // Sparowany czujnik (poprzedni egzemplarz, DC:23:51:CB:F7:7D), reklama zarejestrowana 2026-10-18.
        var identity = TuyaIdentity.TryParse(
            Convert.FromHexString("008A4D0C000BACE5BA"),
            Convert.FromHexString("80030000010062540C6355E6F1F16AABE218B0BCE8A7"));
        Assert.NotNull(identity);
        Assert.Equal("7a84d137ac2085ad", identity.Uuid);
        Assert.Equal(3, identity.ProtocolMajor);
        Assert.True(identity.IsBound);
        Assert.Null(identity.ProductId);
    }

    [Fact]
    public void RealAdvertisementOfUnboundSensorRevealsProduct()
    {
        // Niesparowany ATLO SMS1 tuż po włożeniu baterii (DC:23:51:CC:03:C9), reklama zarejestrowana 2026-11-13.
        var identity = TuyaIdentity.TryParse(
            Convert.FromHexString("006776796767336D38"),
            Convert.FromHexString("0003000001009828AE661450A9A18CC4C1C5357479A9"));
        Assert.NotNull(identity);
        Assert.Equal("07aaad78f40b0a74", identity.Uuid);
        Assert.Equal("gvygg3m8", identity.ProductId);
        Assert.Equal(3, identity.ProtocolMajor);
        Assert.False(identity.IsBound);
    }

    [Theory]
    [InlineData("", "0003000001009828AE661450A9A18CC4C1C5357479A9")]
    [InlineData("006776796767336D38", "00030000")]
    [InlineData("016776796767336D38", "0003000001009828AE661450A9A18CC4C1C5357479A9")]
    public void MalformedAdvertisementIsNotAnIdentity(string serviceHex, string manufacturerHex) =>
        Assert.Null(TuyaIdentity.TryParse(Convert.FromHexString(serviceHex), Convert.FromHexString(manufacturerHex)));

    [Fact]
    public void FragmentationHandlesVarintLengthAndRecoversAfterMissingPacket()
    {
        var data = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
        var envelope = TuyaProtocol.Encode(new(7, 0, 3, data), null, 0);
        var packets = TuyaProtocol.Fragment(envelope);
        Assert.All(packets, packet => Assert.InRange(packet.Length, 1, 20));
        var receiver = new TuyaReassembler();
        Assert.Null(receiver.Feed(packets[0]));
        Assert.Throws<InvalidDataException>(() => receiver.Feed(packets[2]));
        byte[]? received = null;
        foreach (var packet in packets) received = receiver.Feed(packet);
        Assert.Equal(envelope, received);
        Assert.Equal(data, TuyaProtocol.Decode(received!, null).Data);
    }

    [Fact]
    public void AesEnvelopeAuthenticatesCrcAndRejectsCorruption()
    {
        var key = Convert.FromHexString("E80B5017098950FC58AAD83C8C14978E"); // MD5("abcdef").
        var iv = new byte[16];
        var encoded = TuyaProtocol.Encode(new(1, 0, 3, []), key, 4, iv);
        Assert.Equal(33, encoded.Length);
        var decoded = TuyaProtocol.Decode(encoded, key);
        Assert.Equal(1u, decoded.Sequence);
        Assert.Equal(3, decoded.Command);
        encoded[^1] ^= 0xFF;
        Assert.Throws<InvalidDataException>(() => TuyaProtocol.Decode(encoded, key));
    }

    [Fact]
    public void ReassemblerRejectsOversizedAndTruncatedFrames()
    {
        var receiver = new TuyaReassembler();
        Assert.Throws<InvalidDataException>(() => receiver.Feed([0, 0x81, 0x20, 0x30]));
        Assert.Throws<InvalidDataException>(() => receiver.Feed([0, 33]));
        Assert.Throws<InvalidDataException>(() => receiver.Feed([0, 1, 0x30, 1, 2]));
    }

    [Fact]
    public void DatapointsUseSignedBigEndianIntegersAndUnsignedEnums()
    {
        var points = TuyaProtocol.ParseDataPoints(Convert.FromHexString("050204FFFFFFC90302040000002A0F02040000005B0E0401FF"));
        Assert.Equal(new[] { "-55", "42", "91", "255" }, points.Select(p => p.Value));
        Assert.Equal(new[] { 5, 3, 15, 14 }, points.Select(p => p.Id));
    }

    [Theory]
    [InlineData("05020401")]
    [InlineData("0502")]
    [InlineData("0502020102")]
    public void TruncatedOrInvalidDatapointsAreNotReportedAsMeasurements(string hex) =>
        Assert.Throws<InvalidDataException>(() => TuyaProtocol.ParseDataPoints(Convert.FromHexString(hex)));

    [Fact]
    public void CredentialLayoutHasFixedFieldWidthsAndDoesNotReplaceAnExistingKey()
    {
        var identity = new TuyaIdentity("0123456789abcdef", "gvygg3m8", false, 3);
        var credential = new TuyaCredentials { Mac = "DC:23:51:CB:F7:7D", Uuid = identity.Uuid,
            DeviceId = "1234567890123456789012", LocalKey = "abcdef0123456789" };
        credential.Validate();
        credential.EnsureMatches(identity);
        Assert.Equal("0123456789abcdefabcdef1234567890123456789012", Encoding.ASCII.GetString(credential.PairingPayload()));
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            credential.Save(path, createNew: true);
            var original = File.ReadAllText(path);
            Assert.Throws<IOException>(() => credential.Save(path, createNew: true));
            Assert.Equal(original, File.ReadAllText(path));
            var other = new TuyaIdentity("fedcba9876543210", null, false, 3);
            Assert.Throws<InvalidDataException>(() => TuyaCredentials.Load(path).EnsureMatches(other));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-dc2351cc03c9")]           // Windows (Shiny)
    [InlineData("/org/bluez/hci0/dev_DC_23_51_CC_03_C9")]          // ścieżka D-Bus BlueZ
    [InlineData("DC:23:51:CC:03:C9")]
    [InlineData("dc-23-51-cc-03-c9")]
    public void AddressIsExtractedFromPlatformSpecificIdentifiers(string identifier)
    {
        Assert.True(BleAddress.TryExtract(identifier, out var address));
        Assert.Equal("DC:23:51:CC:03:C9", BleAddress.Format(address));
    }

    [Theory]
    [InlineData("")]
    [InlineData("nie-ma-adresu")]
    [InlineData("DC:23:51:CC:03")]
    public void NoAddressMeansNoMatch(string identifier) => Assert.False(BleAddress.TryExtract(identifier, out _));

    [Fact]
    public void CompletenessPredicateNeedsEveryRequiredDatapoint()
    {
        var isComplete = SoilSensorProfiles.IsComplete(SoilSensorProfiles.Sgs01);
        TuyaDataPoint Point(int id) => new(id, 2, "00000001", "1", null);

        Assert.False(isComplete([Point(3), Point(5)]));
        Assert.True(isComplete([Point(3), Point(5), Point(15), Point(9)]));
        Assert.Throws<ArgumentException>(() => SoilSensorProfiles.IsComplete("nieznany"));
    }

    [Theory]
    [InlineData("DC:23:51:CB:F7:7D", true)]
    [InlineData("dc-23-51-cb-f7-7d", true)]
    [InlineData("DC:23:51:CB:F7", false)]
    [InlineData("invalid", false)]
    public void BleAddressParsesBothSeparators(string text, bool valid)
    {
        Assert.Equal(valid, BleAddress.TryParse(text, out var address));
        if (valid) Assert.Equal("DC:23:51:CB:F7:7D", BleAddress.Format(address));
    }
}
