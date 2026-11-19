using Android.Bluetooth;
using Android.Content;
using Glebosaurus.Tuya;
using Java.Util;
using Microsoft.Extensions.Logging;

namespace Glebosaurus.Android.Ble;

/// <summary>Połączenie GATT zakończone błędem stosu Bluetooth (najczęściej 133 – ogólny błąd, zwykle przejściowy).</summary>
internal sealed class GattStatusException(GattStatus status, string operation)
    : IOException($"{operation}: status GATT {(int)status}")
{
    public GattStatus Status { get; } = status;
}

/// <summary>
/// Kanał GATT Tuya (usługa 0x1910: powiadomienia 0x2B10, zapis bez odpowiedzi 0x2B11) nad <see cref="BluetoothGatt"/>. Android
/// przyjmuje jedną operację GATT naraz i odpowiada asynchronicznie (wywołania zwrotne na wątku stosu), więc każda operacja
/// czeka tu na swoje potwierdzenie. Zamknięcie: <c>disconnect()</c>, krótkie czekanie na rozłączenie i zawsze <c>close()</c>
/// – inaczej stos trzyma uchwyt i kolejne połączenia kończą się błędem 133.
/// </summary>
internal sealed class GattConnection : BluetoothGattCallback, IGattLink, IAsyncDisposable
{
    private static readonly UUID ServiceUuid = UUID.FromString("00001910-0000-1000-8000-00805f9b34fb")!;
    private static readonly UUID NotifyUuid = UUID.FromString("00002b10-0000-1000-8000-00805f9b34fb")!;
    private static readonly UUID WriteUuid = UUID.FromString("00002b11-0000-1000-8000-00805f9b34fb")!;
    private static readonly UUID ClientConfigUuid = UUID.FromString("00002902-0000-1000-8000-00805f9b34fb")!;

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DiscoverTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Potwierdzenie włączenia powiadomień zwykle trwa ok. 2 s (Windows); dłużej znaczy, że czujnik nie odpowiada.</summary>
    private static readonly TimeSpan SubscribeTimeout = TimeSpan.FromSeconds(12);

    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DisconnectTimeout = TimeSpan.FromSeconds(3);
    private const int ConnectAttempts = 3;

    /// <summary><c>BluetoothStatusCodes.SUCCESS</c> (Android 13+: zapis przyjęty do kolejki stosu).</summary>
    private const int StatusSuccess = 0;

    private readonly Lock gate = new();
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? servicesDiscovered;
    private TaskCompletionSource? descriptorWritten;
    private TaskCompletionSource? characteristicWritten;
    private Action<byte[]>? onPacket;
    private BluetoothGatt? gatt;
    private BluetoothGattCharacteristic? notify;
    private BluetoothGattCharacteristic? write;
    private bool closed;

    private GattConnection() { }

    /// <summary>Łączy się (tylko LE, bez automatycznego ponawiania przez system), odkrywa usługę Tuya i zwraca gotowy kanał.</summary>
    public static async Task<GattConnection> ConnectAsync(Context context, BluetoothDevice device, ILogger logger, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var connection = new GattConnection();
            try
            {
                connection.gatt = device.ConnectGatt(context, false, connection, BluetoothTransports.Le)
                    ?? throw new IOException("Android nie rozpoczął połączenia GATT.");
                await Await(connection.connected.Task, ConnectTimeout, "Czujnik nie przyjął połączenia", cancellationToken);
                // Część telefonów zwraca pustą listę usług, gdy odkrywanie rusza w tej samej chwili co połączenie.
                await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
                await connection.DiscoverAsync(cancellationToken);
                return connection;
            }
            catch (GattStatusException e) when (attempt < ConnectAttempts && !cancellationToken.IsCancellationRequested)
            {
                logger.LogInformation("Połączenie z {Address} nieudane ({Error}) – ponawiam ({Attempt} z {Max}).", device.Address, e.Message,
                    attempt + 1, ConnectAttempts);
                await connection.DisposeAsync();
                await Task.Delay(TimeSpan.FromSeconds(1.5 * attempt), cancellationToken);
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }
    }

    private async Task DiscoverAsync(CancellationToken cancellationToken)
    {
        var discovered = Begin(ref servicesDiscovered);
        if (gatt?.DiscoverServices() != true) throw new IOException("Android nie rozpoczął odkrywania usług GATT.");
        await Await(discovered, DiscoverTimeout, "Czujnik nie podał listy usług", cancellationToken);
        var service = gatt.GetService(ServiceUuid) ?? throw new IOException("Czujnik nie ma usługi Tuya 0x1910.");
        notify = service.GetCharacteristic(NotifyUuid) ?? throw new IOException("Brak charakterystyki 0x2B10.");
        write = service.GetCharacteristic(WriteUuid) ?? throw new IOException("Brak charakterystyki 0x2B11.");
    }

    public async Task<IAsyncDisposable> SubscribeAsync(Action<byte[]> handler, CancellationToken cancellationToken)
    {
        var (connection, characteristic) = (gatt!, notify!);
        lock (gate) onPacket = handler;
        if (!connection.SetCharacteristicNotification(characteristic, true))
            throw new IOException("Android nie włączył powiadomień charakterystyki 0x2B10.");
        var descriptor = characteristic.GetDescriptor(ClientConfigUuid) ?? throw new IOException("Brak deskryptora 0x2902 przy 0x2B10.");
        byte[] enable = [0x01, 0x00];
        await writes.WaitAsync(cancellationToken);
        try
        {
            var written = Begin(ref descriptorWritten);
            if (OperatingSystem.IsAndroidVersionAtLeast(33))
            {
                var status = connection.WriteDescriptor(descriptor, enable);
                if (status != StatusSuccess) throw new IOException($"Zapis deskryptora 0x2902 odrzucony (kod {status}).");
            }
            else
            {
#pragma warning disable CS0618, CA1422 // na Androidzie < 13 jedyna droga
                descriptor.SetValue(enable);
                if (!connection.WriteDescriptor(descriptor)) throw new IOException("Zapis deskryptora 0x2902 odrzucony.");
#pragma warning restore CS0618, CA1422
            }
            try
            {
                await Await(written, SubscribeTimeout, "", cancellationToken);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException("Czujnik nie potwierdził włączenia powiadomień (charakterystyka 0x2B10).");
            }
        }
        finally { writes.Release(); }
        return new Subscription(this);
    }

    public async Task WriteAsync(byte[] packet, CancellationToken cancellationToken)
    {
        var (connection, characteristic) = (gatt!, write!);
        await writes.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                var written = Begin(ref characteristicWritten);
                bool accepted;
                if (OperatingSystem.IsAndroidVersionAtLeast(33))
                    accepted = connection.WriteCharacteristic(characteristic, packet, (int)GattWriteType.NoResponse) == StatusSuccess;
                else
                {
#pragma warning disable CS0618, CA1422 // na Androidzie < 13 jedyna droga
                    characteristic.WriteType = GattWriteType.NoResponse;
                    characteristic.SetValue(packet);
                    accepted = connection.WriteCharacteristic(characteristic);
#pragma warning restore CS0618, CA1422
                }
                if (!accepted)
                {
                    // Stos zajęty poprzednim pakietem – krótka przerwa i jeszcze raz.
                    if (attempt >= 5) throw new IOException("Android odrzucił zapis do 0x2B11 (stos Bluetooth zajęty).");
                    await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), cancellationToken);
                    continue;
                }
                // Zapis bez odpowiedzi też ma potwierdzenie – gdy pakiet trafi do kontrolera; brak potwierdzenia nie jest błędem sesji.
                try { await Await(written, WriteTimeout, "Zapis do 0x2B11", cancellationToken); }
                catch (TimeoutException) { }
                return;
            }
        }
        finally { writes.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        BluetoothGatt? connection;
        lock (gate)
        {
            if (closed) return;
            closed = true;
            connection = gatt;
            onPacket = null;
        }
        if (connection is null) return;
        try
        {
            connection.Disconnect();
            await Task.WhenAny(disconnected.Task, Task.Delay(DisconnectTimeout));
        }
        catch (Java.Lang.Exception) { /* rozłączenie nie może przesłonić wyniku */ }
        finally
        {
            try { connection.Close(); } catch (Java.Lang.Exception) { }
            connection.Dispose();
            writes.Dispose();
        }
    }

    // ---- wywołania zwrotne stosu Bluetooth (wątek stosu) -----------------------------------------------------------------

    public override void OnConnectionStateChange(BluetoothGatt? g, GattStatus status, ProfileState newState)
    {
        if (newState == ProfileState.Connected && status == GattStatus.Success)
        {
            connected.TrySetResult();
            return;
        }
        if (newState != ProfileState.Disconnected && status == GattStatus.Success) return;
        var error = new GattStatusException(status, newState == ProfileState.Disconnected ? "Rozłączono" : "Połączenie");
        connected.TrySetException(error);
        disconnected.TrySetResult();
        lock (gate)
        {
            servicesDiscovered?.TrySetException(error);
            descriptorWritten?.TrySetException(error);
            characteristicWritten?.TrySetException(error);
        }
    }

    public override void OnServicesDiscovered(BluetoothGatt? g, GattStatus status) => Complete(servicesDiscovered, status, "Odkrywanie usług");

    public override void OnDescriptorWrite(BluetoothGatt? g, BluetoothGattDescriptor? descriptor, GattStatus status) =>
        Complete(descriptorWritten, status, "Włączenie powiadomień");

    public override void OnCharacteristicWrite(BluetoothGatt? g, BluetoothGattCharacteristic? characteristic, GattStatus status) =>
        Complete(characteristicWritten, status, "Zapis do 0x2B11");

    /// <summary>Android 13+: wartość przychodzi w parametrze (domyślna implementacja woła starą wersję – tu jej nie wołamy).</summary>
    public override void OnCharacteristicChanged(BluetoothGatt gatt, BluetoothGattCharacteristic characteristic, byte[] value) => Deliver(value);

#pragma warning disable CS0672, CS0618, CA1422 // Android < 13: jedyna wersja; na 13+ system woła tę wyżej
    public override void OnCharacteristicChanged(BluetoothGatt? g, BluetoothGattCharacteristic? characteristic)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(33)) return;
        if (characteristic?.GetValue() is { } value) Deliver(value);
    }
#pragma warning restore CS0672, CS0618, CA1422

    private void Deliver(byte[] value)
    {
        Action<byte[]>? handler;
        lock (gate) handler = onPacket;
        if (handler is not null && value.Length > 0) handler(value);
    }

    private Task Begin(ref TaskCompletionSource? slot)
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate) slot = source;
        return source.Task;
    }

    private void Complete(TaskCompletionSource? source, GattStatus status, string operation)
    {
        if (source is null) return;
        if (status == GattStatus.Success) source.TrySetResult();
        else source.TrySetException(new GattStatusException(status, operation));
    }

    private static async Task Await(Task task, TimeSpan timeout, string what, CancellationToken cancellationToken)
    {
        try { await task.WaitAsync(timeout, cancellationToken); }
        catch (TimeoutException) { throw new TimeoutException($"{what}: brak odpowiedzi w {timeout.TotalSeconds:0} s."); }
    }

    private sealed class Subscription(GattConnection owner) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            lock (owner.gate) owner.onPacket = null;
            // Powiadomień nie wyłączamy zapisem deskryptora – zaraz i tak następuje rozłączenie.
            return ValueTask.CompletedTask;
        }
    }
}
