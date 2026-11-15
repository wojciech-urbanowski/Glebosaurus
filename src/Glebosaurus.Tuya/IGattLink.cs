namespace Glebosaurus.Tuya;

/// <summary>
/// Minimalny kanał GATT, którego potrzebuje sesja Tuya: powiadomienia z charakterystyki 0x2B10 i zapis bez odpowiedzi
/// do 0x2B11 (usługa 0x1910). Implementacje (Shiny, atrapa w testach) nie znają protokołu Tuya, a sesja nie zna biblioteki BLE.
/// </summary>
public interface IGattLink
{
    /// <summary>Włącza powiadomienia i dostarcza każdy pakiet (≤ 20 B) do <paramref name="onPacket"/>. Dispose je wyłącza.</summary>
    Task<IAsyncDisposable> SubscribeAsync(Action<byte[]> onPacket, CancellationToken cancellationToken);

    /// <summary>Zapisuje jeden pakiet (≤ 20 B) do 0x2B11 bez odpowiedzi.</summary>
    Task WriteAsync(byte[] packet, CancellationToken cancellationToken);
}
