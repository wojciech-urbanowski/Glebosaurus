using System.Globalization;
using Glebosaurus.Core.Monitoring;
using Glebosaurus.FlowerCare;
using Glebosaurus.Tuya;
using Shiny.BluetoothLE;

namespace Glebosaurus.Agent.Ble;

/// <summary>
/// Złożenie reklam jednego urządzenia. Czujnik rozdziela dane usługi i dane producenta na osobne pakiety (reklama i
/// odpowiedź na skan), więc tożsamość Tuya powstaje dopiero po zebraniu obu.
/// </summary>
internal sealed class SeenSensor(IPeripheral peripheral)
{
    public IPeripheral Peripheral { get; } = peripheral;
    public string? Name { get; private set; }

    /// <summary>
    /// Moc sygnału ze wszystkich pakietów. Shiny podaje dla części pakietów −127 („brak wartości”), więc liczymy tylko ważne
    /// próbki, zamiast brać wartość z ostatniego pakietu (wyszłoby −127 mimo dobrego sygnału).
    /// </summary>
    public SignalStats Signal { get; } = new();

    public bool? IsConnectable { get; private set; }
    public int Packets { get; private set; }

    /// <summary>Dane usługi 0xA201 bez UUID usługi (bajt 0 == 0, dalej product_id).</summary>
    public byte[]? ServiceData { get; private set; }

    /// <summary>Dane producenta 0x07D0 bez ID firmy (22 B).</summary>
    public byte[]? ManufacturerData { get; private set; }

    /// <summary>Surowe długości z biblioteki BLE – do sprawdzenia, czy dana platforma dołącza UUID/ID firmy do danych.</summary>
    public int RawServiceLength { get; private set; }
    public int RawManufacturerLength { get; private set; }

    public bool IsTuyaLike => ServiceData is not null || ManufacturerData is not null;

    /// <summary>Ostatnia reklama MiBeacon (dane usługi 0xFE95) – czujnik Xiaomi Flower Care.</summary>
    public MiBeacon? Beacon { get; private set; }

    /// <summary>Pomiary z reklam MiBeacon według obiektu (ostatni z każdego) – do diagnostyki.</summary>
    public SortedDictionary<ushort, string> BeaconValues { get; } = [];

    /// <summary>Surowe dane usługi 0xFE95 z kolejnych pakietów (różne) – do diagnostyki.</summary>
    public SortedSet<string> RawBeacons { get; } = [];

    /// <summary>Czujnik Xiaomi Flower Care: model 0x0098 w reklamie MiBeacon albo nazwa „Flower care”.</summary>
    public bool IsFlowerCare => Beacon?.IsFlowerCare == true || FlowerCareAdvertising.IsFlowerCareName(Name);

    public TuyaIdentity? Identity =>
        ServiceData is null || ManufacturerData is null ? null : TuyaIdentity.TryParse(ServiceData, ManufacturerData);

    /// <summary>
    /// Czy to czujnik opisany tymi poświadczeniami: po adresie MAC (działa od pierwszego pakietu) albo po UUID Tuya
    /// z pełnej reklamy (zapasowo, gdy platforma nie zdradza adresu). Niełączliwych urządzeń nie bierzemy.
    /// </summary>
    public bool Matches(TuyaCredentials credentials)
    {
        if (IsConnectable == false) return false;
        if (BleAddress.TryParse(credentials.Mac, out var wanted) && BleAddress.TryExtract(Peripheral.Uuid, out var address) && address == wanted)
            return true;
        return Identity is { } identity && identity.Uuid == credentials.Uuid;
    }

    public void Add(ScanResult result)
    {
        Packets++;
        Signal.Add(result.Rssi);
        var advertisement = result.AdvertisementData;
        Name = result.Peripheral.Name ?? advertisement.LocalName ?? Name;
        // Odpowiedź na skan bywa oznaczona jako niełączliwa, choć reklama była łączliwa – wystarczy jeden łączliwy pakiet.
        IsConnectable = IsConnectable == true || advertisement.IsConnectable == true ? true : advertisement.IsConnectable ?? IsConnectable;

        foreach (var service in advertisement.ServiceData ?? [])
        {
            if (!service.Uuid.StartsWith("0000a201", StringComparison.OrdinalIgnoreCase) || service.Data is not { } data) continue;
            RawServiceLength = data.Length;
            // Niektóre platformy dołączają 16-bitowy UUID (01 A2) na początku danych usługi.
            ServiceData = data is [0x01, 0xA2, ..] ? data[2..] : data;
        }

        foreach (var service in advertisement.ServiceData ?? [])
        {
            if (!service.Uuid.StartsWith("0000fe95", StringComparison.OrdinalIgnoreCase) && !service.Uuid.Equals("fe95", StringComparison.OrdinalIgnoreCase)
                || service.Data is not { } data || MiBeacon.TryParse(data) is not { } beacon) continue;
            Beacon = beacon;
            RawBeacons.Add(Convert.ToHexString(data));
            if (beacon is { ObjectId: { } id } && beacon.DescribeObject() is { } text) BeaconValues[id] = text;
        }

        if (advertisement.ManufacturerData is { CompanyId: TuyaIdentity.CompanyId, Data: { } mfg })
        {
            RawManufacturerLength = mfg.Length;
            // Niektóre platformy dołączają ID firmy (D0 07) na początku danych producenta.
            ManufacturerData = mfg.Length == 24 && mfg[0] == 0xD0 && mfg[1] == 0x07 ? mfg[2..] : mfg;
        }
    }

    public string DescribeFlowerCare(bool verbose)
    {
        var text = string.Create(CultureInfo.InvariantCulture,
            $"{BleAddress.TryExtract(Peripheral.Uuid, out var address) switch { true => BleAddress.Format(address), false => Peripheral.Uuid }}  " +
            $"{Name ?? "(bez nazwy)"}  pakietów: {Packets,-4} sygnał {Signal.Describe()}  ");
        text += BeaconValues.Count == 0 ? "bez pomiarów w reklamach" : "w reklamach: " + string.Join(", ", BeaconValues.Values);
        if (verbose && Beacon is { } beacon)
            text += $"{Environment.NewLine}      MiBeacon: kontrola 0x{beacon.FrameControl:X4}, model 0x{beacon.ProductId:X4}, MAC {beacon.Mac ?? "-"}," +
                    $" {(beacon.Encrypted ? "szyfrowana" : "jawna")}, {(IsConnectable == false ? "niełączliwy" : "łączliwy")}" +
                    $"{Environment.NewLine}      dane 0xFE95: {string.Join(" ", RawBeacons)}";
        return text;
    }

    public string Describe(bool verbose)
    {
        var identity = Identity;
        var text = string.Create(CultureInfo.InvariantCulture,
            $"{Peripheral.Uuid}  {Name ?? "(bez nazwy)",-6} pakietów: {Packets,-4} sygnał {Signal.Describe()}  ");
        text += identity is null
            ? "tożsamość Tuya: niepełna"
            : $"UUID Tuya {identity.Uuid}, produkt {identity.ProductId ?? "ukryty"}, {(identity.IsBound ? "sparowany" : "NIESPAROWANY")}, " +
              $"protokół {identity.ProtocolMajor}, {(IsConnectable == false ? "niełączliwy" : "łączliwy")}";
        if (verbose)
            text += $"{Environment.NewLine}      dane usługi: {(ServiceData is null ? "-" : Convert.ToHexString(ServiceData))} (surowo {RawServiceLength} B)," +
                    $" dane producenta: {(ManufacturerData is null ? "-" : Convert.ToHexString(ManufacturerData))} (surowo {RawManufacturerLength} B)";
        return text;
    }
}
