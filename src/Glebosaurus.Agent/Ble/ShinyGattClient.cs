using Glebosaurus.FlowerCare;
using Shiny.BluetoothLE;

namespace Glebosaurus.Agent.Ble;

/// <summary>Odczyt i zapis charakterystyk (Flower Care) nad połączonym <see cref="IPeripheral"/>.</summary>
internal sealed class ShinyGattClient(IPeripheral peripheral) : IGattClient
{
    private const int TimeoutMs = 10_000;

    public async Task<byte[]> ReadAsync(string service, string characteristic, CancellationToken cancellationToken)
    {
        var result = await peripheral.ReadCharacteristicAsync(service, characteristic, cancellationToken, TimeoutMs);
        return result.Data ?? [];
    }

    public async Task WriteAsync(string service, string characteristic, byte[] value, CancellationToken cancellationToken) =>
        await peripheral.WriteCharacteristicAsync(service, characteristic, value, true, cancellationToken, TimeoutMs);
}
