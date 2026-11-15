using Glebosaurus.Tuya;
using Shiny.BluetoothLE;

namespace Glebosaurus.Agent.Ble;

/// <summary>Kanał GATT Tuya (usługa 0x1910: powiadomienia 0x2B10, zapis 0x2B11) nad połączonym <see cref="IPeripheral"/>.</summary>
internal sealed class ShinyGattLink(IPeripheral peripheral) : IGattLink
{
    private const string ServiceUuid = "00001910-0000-1000-8000-00805f9b34fb";
    private const string NotifyUuid = "00002b10-0000-1000-8000-00805f9b34fb";
    private const string WriteUuid = "00002b11-0000-1000-8000-00805f9b34fb";

    /// <summary>Potwierdzenie włączenia powiadomień zwykle trwa ok. 2 s; po zabiciu poprzedniego procesu potrafiło wisieć bez końca.</summary>
    private static readonly TimeSpan SubscribeTimeout = TimeSpan.FromSeconds(12);

    public async Task<IAsyncDisposable> SubscribeAsync(Action<byte[]> onPacket, CancellationToken cancellationToken)
    {
        var subscription = peripheral.NotifyCharacteristic(ServiceUuid, NotifyUuid, false).Observe(result =>
        {
            if (result.Event == BleCharacteristicEvent.Notification && result.Data is { Length: > 0 } data) onPacket(data);
        });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SubscribeTimeout);
        try
        {
            await peripheral.WaitForCharacteristicSubscriptionAsync(ServiceUuid, NotifyUuid, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            subscription.Dispose();
            throw new TimeoutException("Czujnik nie potwierdził włączenia powiadomień (charakterystyka 0x2B10).");
        }
        catch
        {
            subscription.Dispose();
            throw;
        }
        return new Subscription(subscription);
    }

    public async Task WriteAsync(byte[] packet, CancellationToken cancellationToken) =>
        await peripheral.WriteCharacteristicAsync(ServiceUuid, WriteUuid, packet, false, cancellationToken, 10_000);

    private sealed class Subscription(IDisposable inner) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            // Rozłączenie nie może przesłonić wyniku sesji.
            try { inner.Dispose(); } catch (Exception) { /* ignorowane */ }
            return ValueTask.CompletedTask;
        }
    }
}
