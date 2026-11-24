namespace Glebosaurus.FlowerCare;

/// <summary>
/// Kanał GATT, którego potrzebuje sesja Flower Care: odczyt charakterystyki i zapis z potwierdzeniem. Implementacje (Shiny,
/// Android, atrapa w testach) nie znają protokołu czujnika, a sesja nie zna biblioteki BLE. UUID w pełnej postaci 128-bitowej,
/// małymi literami (<see cref="FlowerCareProtocol"/>).
/// </summary>
public interface IGattClient
{
    /// <summary>Czyta bieżącą wartość charakterystyki prosto z urządzenia (bez pamięci podręcznej systemu).</summary>
    Task<byte[]> ReadAsync(string service, string characteristic, CancellationToken cancellationToken);

    /// <summary>Zapisuje wartość z potwierdzeniem (write request).</summary>
    Task WriteAsync(string service, string characteristic, byte[] value, CancellationToken cancellationToken);
}
