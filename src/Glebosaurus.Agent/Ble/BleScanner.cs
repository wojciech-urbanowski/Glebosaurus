namespace Glebosaurus.Agent.Ble;

/// <summary>Jedna pętla nasłuchu reklam BLE dla wszystkich poleceń (scan, read, pair, monitor).</summary>
internal static class BleScanner
{
    /// <summary>
    /// Skanuje, aż <paramref name="stopWhen"/> zwróci true albo upłynie <paramref name="timeout"/>. Wywołanie następuje po
    /// każdym pakiecie, pod blokadą (ma być krótkie). Zwraca wszystkie widziane urządzenia, ze złożonymi reklamami.
    /// </summary>
    public static async Task<IReadOnlyList<SeenSensor>> ScanAsync(
        BleHost ble, TimeSpan timeout, Func<SeenSensor, bool>? stopWhen, CancellationToken cancellationToken)
    {
        var seen = new Dictionary<string, SeenSensor>();
        var stopped = false;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = ble.Manager.Scan(new Shiny.BluetoothLE.ScanConfig()).Observe(result =>
        {
            lock (seen)
            {
                if (stopped) return;
                if (!seen.TryGetValue(result.Peripheral.Uuid, out var sensor))
                    seen[result.Peripheral.Uuid] = sensor = new SeenSensor(result.Peripheral);
                sensor.Add(result);
                if (stopWhen?.Invoke(sensor) == true) done.TrySetResult();
            }
        }, error => done.TrySetException(error));

        try
        {
            await Task.WhenAny(done.Task, Task.Delay(timeout, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            if (done.Task.IsFaulted) await done.Task;
        }
        finally
        {
            ble.Manager.StopScan();
        }

        lock (seen)
        {
            stopped = true;
            return seen.Values.ToList();
        }
    }
}
