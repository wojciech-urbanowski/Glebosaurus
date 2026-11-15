namespace Glebosaurus.Tuya.Tests;

public class PairingRulesTests
{
    private const ulong Mine = 0xDC2351CC03C9;
    private const ulong OtherDevice = 0xDC2351A3B6A7;

    private static TuyaIdentity Unbound(string? product = "gvygg3m8", int protocol = 3) => new("07aaad78f40b0a74", product, false, protocol);
    private static TuyaIdentity Bound() => new("07aaad78f40b0a74", null, true, 3);

    private static TuyaCredentials Existing(string uuid = "07aaad78f40b0a74") => new() { Uuid = uuid, DeviceId = "x", LocalKeyHex = "a1b2c3d4e5f6" };

    [Fact]
    public void UnboundSensorOfKnownProductIsACandidate() =>
        Assert.True(PairingRules.IsCandidate(Unbound(), true, Mine, existing: null, wantedMac: null));

    [Fact]
    public void AddressIsOptionalBecauseNotEveryPlatformRevealsIt() =>
        Assert.True(PairingRules.IsCandidate(Unbound(), null, address: null, existing: null, wantedMac: null));

    [Fact]
    public void IncompleteAdvertisementIsNotACandidate() =>
        Assert.False(PairingRules.IsCandidate(null, true, Mine, null, null));

    [Fact]
    public void BoundDeviceIsNeverAFreshPairingTarget() =>
        Assert.False(PairingRules.IsCandidate(Bound(), true, Mine, existing: null, wantedMac: null));

    [Theory]
    [InlineData("zzzzzzzz")]
    [InlineData(null)]
    public void UnknownProductIsNotACandidate(string? product) =>
        Assert.False(PairingRules.IsCandidate(Unbound(product), true, Mine, null, null));

    [Fact]
    public void OtherProtocolVersionIsNotACandidate() =>
        Assert.False(PairingRules.IsCandidate(Unbound(protocol: 4), true, Mine, null, null));

    [Fact]
    public void NonConnectableDeviceIsNotACandidate() =>
        Assert.False(PairingRules.IsCandidate(Unbound(), false, Mine, null, null));

    [Fact]
    public void ExplicitAddressExcludesEveryOtherDevice()
    {
        Assert.True(PairingRules.IsCandidate(Unbound(), true, Mine, null, wantedMac: Mine));
        Assert.False(PairingRules.IsCandidate(Unbound(), true, OtherDevice, null, wantedMac: Mine));
        Assert.False(PairingRules.IsCandidate(Unbound(), true, address: null, null, wantedMac: Mine)); // adres nieznany = nie ryzykujemy
    }

    [Fact]
    public void ResumingMatchesOnlyTheDeviceFromTheKeyFileWhetherBoundOrNot()
    {
        Assert.True(PairingRules.IsCandidate(Unbound(), true, Mine, Existing(), null));
        Assert.True(PairingRules.IsCandidate(Bound(), true, Mine, Existing(), null));
        Assert.False(PairingRules.IsCandidate(Unbound(), true, Mine, Existing("fedcba9876543210"), null));
    }
}
