using System.Security.Cryptography;

namespace Glebosaurus.Tuya.Tests;

/// <summary>
/// Atrapa czujnika po stronie GATT: odbiera pakiety zapisu, składa ramki Tuya i odpowiada tym samym protokołem
/// (Device Info, parowanie sesji, raport DP), więc <see cref="TuyaSession"/> przechodzi pełny przebieg bez radia.
/// </summary>
/// <param name="afterReport">Dodatkowe ramki (komenda, dane), które czujnik wysyła zaraz po zwykłym raporcie stanu – np. zaległe pomiary.</param>
internal sealed class FakeSensorLink(byte[] deviceKey, bool bound, IReadOnlyList<(ushort Command, byte[] Data)>? afterReport = null) : IGattLink
{
    private static readonly byte[] Srand = [1, 2, 3, 4, 5, 6];
    private static readonly byte[] AuthKey = [.. Enumerable.Range(0, 32).Select(i => (byte)i)];

    private readonly byte[] loginKey = MD5.HashData(deviceKey);
    private readonly byte[] sessionKey = MD5.HashData([.. deviceKey, .. Srand]);
    private readonly TuyaReassembler reassembler = new();
    private Action<byte[]>? handler;
    private uint sequence;

    /// <summary>Dane parowania odebrane w ramce o kodzie 1 (jawne przy lokalnym przypisaniu, szyfrowane w sesji).</summary>
    public List<(byte Flag, byte[] Data)> PairRequests { get; } = [];

    public bool IsSubscribed => handler is not null;

    public Task<IAsyncDisposable> SubscribeAsync(Action<byte[]> onPacket, CancellationToken cancellationToken)
    {
        handler = onPacket;
        return Task.FromResult<IAsyncDisposable>(new Unsubscriber(() => handler = null));
    }

    public Task WriteAsync(byte[] packet, CancellationToken cancellationToken)
    {
        byte[]? envelope;
        try { envelope = reassembler.Feed(packet); }
        catch (InvalidDataException) { return Task.CompletedTask; }
        if (envelope is null || envelope[0] is not (0 or 4 or 5)) return Task.CompletedTask;

        TuyaFrame frame;
        try { frame = TuyaProtocol.Decode(envelope, envelope[0] switch { 4 => loginKey, 5 => sessionKey, _ => null }); }
        catch (Exception e) when (e is InvalidDataException or CryptographicException) { return Task.CompletedTask; } // zły klucz: czujnik milczy

        switch (frame.Command)
        {
            case 0: Send(0, frame.Sequence, DeviceInfo(), loginKey, 4); break;
            case 1:
                PairRequests.Add((envelope[0], frame.Data));
                if (envelope[0] == 5) Send(1, frame.Sequence, [0], sessionKey, 5);
                break;
            case 3:
                Send(0x8001, 0, SoilReport(), sessionKey, 5);
                foreach (var (command, data) in afterReport ?? []) Send(command, 0, data, sessionKey, 5);
                break;
        }
        return Task.CompletedTask;
    }

    private byte[] DeviceInfo()
    {
        var data = new byte[46];
        data[0] = 9; data[1] = 3;   // firmware 9.3
        data[2] = 3; data[3] = 1;   // protokół 3.1
        data[5] = (byte)(bound ? 1 : 0);
        Srand.CopyTo(data, 6);
        AuthKey.CopyTo(data, 14);
        return data;
    }

    /// <summary>Wartości zaobserwowane na prawdziwym czujniku: DP6=0, DP3=18, DP5=237, DP15=33, DP14=1, DP9=0.</summary>
    private static byte[] SoilReport() =>
    [
        .. Dp(6, 2, Int(0)), .. Dp(3, 2, Int(18)), .. Dp(5, 2, Int(237)),
        .. Dp(15, 2, Int(33)), .. Dp(14, 4, 1), .. Dp(9, 4, 0)
    ];

    /// <summary>Dane ramki 0x8003: DP z czasem czujnika (znacznik 1 + sekundy Unix + punkty danych).</summary>
    public static byte[] TimedReport(DateTimeOffset time, params byte[][] dataPoints) =>
        [1, .. Int((int)time.ToUnixTimeSeconds()), .. dataPoints.SelectMany(dp => dp)];

    public static byte[] Dp(byte id, byte type, params byte[] value) => [id, type, (byte)value.Length, .. value];

    public static byte[] Int(int value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private void Send(ushort command, uint responseTo, byte[] data, byte[] key, byte flag)
    {
        var envelope = TuyaProtocol.Encode(new(++sequence, responseTo, command, data), key, flag);
        foreach (var packet in TuyaProtocol.Fragment(envelope)) handler?.Invoke(packet);
    }

    private sealed class Unsubscriber(Action onDispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            onDispose();
            return ValueTask.CompletedTask;
        }
    }
}
