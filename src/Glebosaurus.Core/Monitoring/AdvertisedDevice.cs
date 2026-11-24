using Glebosaurus.Core.Config;
using Glebosaurus.FlowerCare;
using Glebosaurus.Tuya;

namespace Glebosaurus.Core.Monitoring;

/// <summary>
/// Złożenie reklam BLE jednego urządzenia po adresie MAC, niezależne od biblioteki BLE (agent Android; na Windows i Linuksie
/// to samo robi <c>SeenSensor</c> nad Shiny). Czujnik rozdziela dane usługi 0xA201 i dane producenta 0x07D0 na osobne
/// pakiety (reklama i odpowiedź na skan), więc tożsamość Tuya powstaje dopiero po zebraniu obu.
/// </summary>
public sealed class AdvertisedDevice
{
    public AdvertisedDevice(string address)
    {
        Address = BleAddress.TryParse(address, out var parsed) ? BleAddress.Format(parsed) : address.ToUpperInvariant();
        Value = BleAddress.TryParse(address, out parsed) ? parsed : null;
    }

    /// <summary>Adres MAC w postaci AA:BB:CC:DD:EE:FF.</summary>
    public string Address { get; }

    private ulong? Value { get; }

    public string? Name { get; private set; }

    /// <summary>Moc sygnału z ważnych próbek (Android podaje 127, gdy jej nie zna – taka próbka się nie liczy).</summary>
    public SignalStats Signal { get; } = new();

    /// <summary>Ostatnia ważna moc sygnału (dBm) – do podglądu „na żywo” przy wyborze miejsca na telefon.</summary>
    public int? LastRssi { get; private set; }

    public bool? IsConnectable { get; private set; }
    public int Packets { get; private set; }
    public DateTimeOffset? FirstSeenAt { get; private set; }
    public DateTimeOffset? LastSeenAt { get; private set; }

    /// <summary>Dane usługi 0xA201 bez UUID usługi (bajt 0 == 0, dalej product_id).</summary>
    public byte[]? ServiceData { get; private set; }

    /// <summary>Dane producenta 0x07D0 bez ID firmy (22 B).</summary>
    public byte[]? ManufacturerData { get; private set; }

    public bool IsTuyaLike => ServiceData is not null || ManufacturerData is not null;

    /// <summary>Ostatnia reklama MiBeacon (dane usługi 0xFE95) – czujnik Xiaomi Flower Care.</summary>
    public MiBeacon? Beacon { get; private set; }

    /// <summary>Czujnik Xiaomi Flower Care: model 0x0098 w reklamie MiBeacon albo nazwa „Flower care”.</summary>
    public bool IsFlowerCare => Beacon?.IsFlowerCare == true || FlowerCareAdvertising.IsFlowerCareName(Name);

    public TuyaIdentity? Identity =>
        ServiceData is null || ManufacturerData is null ? null : TuyaIdentity.TryParse(ServiceData, ManufacturerData);

    /// <param name="serviceData">Dane usługi 0xA201 (z UUID 01 A2 na początku albo bez) – null, gdy pakiet ich nie miał.</param>
    /// <param name="manufacturerData">Dane producenta 0x07D0 (z ID firmy D0 07 na początku albo bez) – null, gdy pakiet ich nie miał.</param>
    /// <param name="miBeacon">Dane usługi 0xFE95 (MiBeacon, czujnik Flower Care) – null, gdy pakiet ich nie miał.</param>
    public void Add(DateTimeOffset at, int rssi, string? name, bool? connectable, byte[]? serviceData, byte[]? manufacturerData,
        byte[]? miBeacon = null)
    {
        if (miBeacon is { Length: > 0 } && MiBeacon.TryParse(miBeacon) is { } beacon) Beacon = beacon;
        Packets++;
        FirstSeenAt ??= at;
        LastSeenAt = at;
        Signal.Add(rssi);
        if (SignalStats.IsValid(rssi)) LastRssi = rssi;
        if (!string.IsNullOrWhiteSpace(name)) Name = name;
        // Odpowiedź na skan bywa oznaczona jako niełączliwa, choć reklama była łączliwa – wystarczy jeden łączliwy pakiet.
        IsConnectable = IsConnectable == true || connectable == true ? true : connectable ?? IsConnectable;
        if (serviceData is { Length: > 0 }) ServiceData = serviceData is [0x01, 0xA2, ..] ? serviceData[2..] : serviceData;
        if (manufacturerData is { Length: > 0 })
            ManufacturerData = manufacturerData.Length == 24 && manufacturerData[0] == 0xD0 && manufacturerData[1] == 0x07 ? manufacturerData[2..] : manufacturerData;
    }

    /// <summary>Czy to ten czujnik: Flower Care po adresie MAC, Tuya – <see cref="Matches(TuyaCredentials)"/>.</summary>
    public bool Matches(SensorConfig sensor) => sensor.Model == SensorModel.FlowerCare
        ? IsConnectable != false && Value is { } address && BleAddress.TryParse(sensor.Mac, out var wanted) && address == wanted
        : Matches(sensor.Tuya);

    /// <summary>
    /// Czy to czujnik opisany tymi poświadczeniami: po adresie MAC (od pierwszego pakietu) albo po UUID Tuya z pełnej
    /// reklamy (zapasowo). Niełączliwych urządzeń nie bierzemy.
    /// </summary>
    public bool Matches(TuyaCredentials credentials)
    {
        if (IsConnectable == false) return false;
        if (Value is { } address && BleAddress.TryParse(credentials.Mac, out var wanted) && address == wanted) return true;
        return Identity is { } identity && identity.Uuid == credentials.Uuid;
    }
}
