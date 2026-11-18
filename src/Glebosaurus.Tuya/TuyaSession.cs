// Protocol reference: roquerodrigo/tuya-ble-sdk and Wheemer/local-tuya-ble (MIT).
// See THIRD-PARTY-NOTICES.md for attribution and license.
// Przebieg sesji i lokalnego przypisania sprawdzony na sprzęcie (ATLO SMS1, Tuya BLE 3.1), 2026-11-13.
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace Glebosaurus.Tuya;

/// <summary>Czujnik zgłasza się jako niesparowany: trzeba go (ponownie) sparować zapisanym kluczem.</summary>
public sealed class TuyaUnboundException(string message) : InvalidOperationException(message);

/// <param name="Protocol">Wersja protokołu, np. "3.1".</param>
/// <param name="Firmware">Wersja firmware czujnika, np. "9.3".</param>
/// <param name="Truncated">
/// Sesję przerwał jej limit czasu, gdy czujnik wciąż nadawał (np. długa historia): odebrane punkty są prawdziwe, ale ostatni
/// z nich nie musi być stanem z chwili połączenia – reszta historii i zwykły raport stanu przyjdą przy następnym połączeniu.
/// </param>
public sealed record TuyaReadResult(IReadOnlyList<TuyaDataPoint> DataPoints, string Protocol, string Firmware, IReadOnlyList<string> Notes,
    bool Truncated = false);

/// <summary>Jedna sesja z czujnikiem po połączeniu GATT: odczyt punktów danych albo lokalne przypisanie.</summary>
/// <param name="quietTimeout">
/// Jak długo po ostatniej ramce sesja czeka na kolejne, zanim uzna odczyt za zakończony (domyślnie 2 s). Diagnostyka
/// (polecenie listen) wydłuża ten czas, żeby zobaczyć wszystko, co czujnik wysyła po zwykłym raporcie.
/// </param>
/// <param name="onFrame">
/// Podgląd każdej ramki od czujnika (raporty DP, prośby o czas), także tych, których sesja nie obsługuje.
/// Nie dostaje odpowiedzi uzgadniania (Device Info, parowanie sesji), więc nie ujawnia materiału kluczy.
/// </param>
public sealed class TuyaSession(
    IGattLink link, TuyaCredentials credentials, TimeSpan operationTimeout, Action<string>? log = null, TimeSpan? sessionDeadline = null,
    TimeSpan? quietTimeout = null, Action<TuyaFrame>? onFrame = null)
{
    /// <summary>Całkowity limit sesji (od włączenia powiadomień do końca). Normalny odczyt trwa kilka sekund.</summary>
    private static readonly TimeSpan DefaultSessionDeadline = TimeSpan.FromSeconds(40);

    /// <summary>Cisza po ostatniej ramce, po której odczyt uznajemy za zakończony.</summary>
    private static readonly TimeSpan DefaultQuietTimeout = TimeSpan.FromSeconds(2);

    private TimeSpan SessionDeadline => sessionDeadline ?? DefaultSessionDeadline;

    private TimeSpan QuietTimeout => quietTimeout ?? DefaultQuietTimeout;

    private readonly TuyaReassembler reassembler = new();
    private readonly Channel<byte[]> incoming = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(128)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
    private readonly byte[] loginKey = MD5.HashData(credentials.KeyBytes);
    private readonly List<TuyaDataPoint> dataPoints = [];
    private readonly List<string> notes = [];
    private byte[]? sessionKey;
    private byte[]? authenticationKey;
    private string protocol = "";
    private string firmware = "";
    private uint sequence;
    private int skippedReports;
    private string? firstSkipped;

    /// <summary>Uwierzytelnia sesję zapisanym kluczem, prosi o stan i zbiera punkty danych.</summary>
    /// <exception cref="TuyaUnboundException">Czujnik nie jest sparowany (np. po resecie przyciskiem).</exception>
    /// <exception cref="TimeoutException">Czujnik nie odpowiada – zły klucz albo brak zasięgu.</exception>
    public Task<TuyaReadResult> ReadAsync(CancellationToken cancellationToken) => ReadAsync(null, cancellationToken);

    /// <param name="isComplete">
    /// Gdy zwróci true dla zebranych dotąd punktów danych, sesja kończy się od razu zamiast czekać na ciszę.
    /// Krótsze połączenie oszczędza baterię czujnika.
    /// </param>
    public async Task<TuyaReadResult> ReadAsync(Func<IReadOnlyList<TuyaDataPoint>, bool>? isComplete, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(SessionDeadline);
        try
        {
            return await ReadCoreAsync(isComplete, deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Czujnik wciąż nadaje (np. długa historia): oddajemy to, co już odebrane i potwierdzone, zamiast wyrzucać wszystko.
            if (dataPoints.Count > 0)
            {
                notes.Add($"Sesja przekroczyła limit czasu ({SessionDeadline.TotalSeconds:0} s); zwrócono punkty danych odebrane do tej chwili.");
                return Result(truncated: true);
            }
            throw new TimeoutException($"Sesja Tuya przekroczyła limit czasu ({SessionDeadline.TotalSeconds:0} s).");
        }
    }

    private TuyaReadResult Result(bool truncated = false)
    {
        var all = notes.ToList();
        if (skippedReports > 0)
            all.Add($"Pominięto nieczytelne raporty punktów danych: {skippedReports} (potwierdzone, żeby czujnik oddał resztę serii); pierwszy: {firstSkipped}.");
        return new(dataPoints.ToArray(), protocol, firmware, all, truncated);
    }

    /// <summary>Punkty danych z ramki 0x8001 (bez czasu) albo 0x8003 (znacznik czasu czujnika: 1 + sekundy Unix albo 0 + 13 cyfr ms).</summary>
    /// <exception cref="InvalidDataException">Ramka jest nieczytelna: nieznany znacznik czasu, typ albo długość punktu danych.</exception>
    private static IReadOnlyList<TuyaDataPoint> ParseReport(TuyaFrame frame)
    {
        if (frame.Command == 0x8001) return TuyaProtocol.ParseDataPoints(frame.Data);
        if (frame.Data.Length >= 5 && frame.Data[0] == 1)
            return TuyaProtocol.ParseDataPoints(frame.Data, 5, DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadUInt32BigEndian(frame.Data.AsSpan(1))));
        if (frame.Data.Length >= 14 && frame.Data[0] == 0
            && long.TryParse(Encoding.ASCII.GetString(frame.Data, 1, 13), NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds))
            return TuyaProtocol.ParseDataPoints(frame.Data, 14, DateTimeOffset.FromUnixTimeMilliseconds(milliseconds));
        throw new InvalidDataException("Nieobsługiwany znacznik czasu Tuya.");
    }

    private async Task<TuyaReadResult> ReadCoreAsync(Func<IReadOnlyList<TuyaDataPoint>, bool>? isComplete, CancellationToken token)
    {
        await using var subscription = await link.SubscribeAsync(OnPacket, token);

        log?.Invoke("Uwierzytelnianie lokalnej sesji Tuya BLE...");
        var info = await RequestAsync(0, [], loginKey, 4, token);
        if (info.Data.Length < 46 || info.Data[2] != 3)
            throw new InvalidDataException("Nieobsługiwana odpowiedź/wariant protokołu Tuya. Obsługiwany jest klasyczny protokół 3.");
        if (info.Data[5] == 0)
            throw new TuyaUnboundException("Czujnik nie jest sparowany. Sparuj go ponownie zapisanym kluczem.");
        protocol = $"{info.Data[2]}.{info.Data[3]}";
        firmware = $"{info.Data[0]}.{info.Data[1]}";
        sessionKey = MD5.HashData([.. credentials.KeyBytes, .. info.Data.AsSpan(6, 6)]);
        authenticationKey = info.Data[14..46];

        var pair = await RequestAsync(1, credentials.PairingPayload(), sessionKey, 5, token);
        if (pair.Data.Length != 1 || pair.Data[0] is not (0 or 2))
            throw new InvalidDataException("Czujnik odrzucił dane lokalnej sesji Tuya.");

        await SendAsync(3, [], sessionKey, 5, 0, token);
        while (true)
        {
            try
            {
                var wait = dataPoints.Count == 0 ? operationTimeout : QuietTimeout;
                var frame = await ReadFrameAsync(wait, token);
                await HandleAsync(frame, token);
                if (isComplete?.Invoke(dataPoints) == true) break;
            }
            catch (TimeoutException) { break; }
        }

        if (dataPoints.Count == 0) notes.Add("Sesja uwierzytelniona, ale czujnik nie zgłosił punktów danych w limicie czasu.");
        return Result();
    }

    /// <summary>
    /// Lokalnie przypisuje niesparowany czujnik: wysyła nieszyfrowane Device Info, a po <paramref name="persistBeforeTransmit"/>
    /// dane parowania (UUID + klucz + deviceId). Wywołujący musi zapisać klucz PRZED transmisją, żeby ponowna próba
    /// użyła tego samego klucza. Poprawność klucza potwierdza dopiero kolejny <see cref="ReadAsync"/> na nowym połączeniu.
    /// </summary>
    public async Task BindAsync(Func<CancellationToken, Task> persistBeforeTransmit, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(SessionDeadline);
        try
        {
            await BindCoreAsync(persistBeforeTransmit, deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Sesja Tuya przekroczyła limit czasu ({SessionDeadline.TotalSeconds:0} s).");
        }
    }

    private async Task BindCoreAsync(Func<CancellationToken, Task> persistBeforeTransmit, CancellationToken token)
    {
        await using var subscription = await link.SubscribeAsync(OnPacket, token);

        log?.Invoke("Lokalne przypisanie niesparowanego czujnika (protokół 3)...");
        await SendAsync(0, [], null, 0, 0, token);
        await Task.Delay(1000, token);
        await persistBeforeTransmit(token);
        await SendAsync(1, credentials.PairingPayload(), null, 0, 0, token);
        await Task.Delay(2000, token);
    }

    private void OnPacket(byte[] bytes)
    {
        try
        {
            lock (reassembler)
                if (reassembler.Feed(bytes) is { } envelope) incoming.Writer.TryWrite(envelope);
        }
        catch (InvalidDataException) { /* Uszkodzony/nieuporządkowany pakiet; następny pakiet 0 zsynchronizuje odbiór. */ }
    }

    private async Task<TuyaFrame> ReadFrameAsync(TimeSpan timeout, CancellationToken token)
    {
        var until = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            var remaining = until - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("Brak odpowiedzi Tuya BLE. Sprawdź klucz i zasięg.");
            using var receiveTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            receiveTimeout.CancelAfter(remaining);
            byte[] envelope;
            try { envelope = await incoming.Reader.ReadAsync(receiveTimeout.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new TimeoutException("Brak odpowiedzi Tuya BLE. Sprawdź klucz i zasięg.");
            }
            // Przy sparowanym czujniku nigdy nie przyjmujemy nieuwierzytelnionych odpowiedzi jawnym tekstem.
            if (envelope[0] is not (1 or 4 or 5)) continue;
            byte[]? key = envelope[0] switch { 1 => authenticationKey, 4 => loginKey, 5 => sessionKey, _ => null };
            try { return TuyaProtocol.Decode(envelope, key); }
            catch (Exception e) when (e is InvalidDataException or CryptographicException)
            {
                const string note = "Odrzucono ramkę z nieprawidłowym CRC lub nieobsługiwanym kluczem.";
                if (!notes.Contains(note)) notes.Add(note);
            }
        }
    }

    private async Task<TuyaFrame> RequestAsync(ushort command, byte[] data, byte[] key, byte flag, CancellationToken token)
    {
        var request = await SendAsync(command, data, key, flag, 0, token);
        var until = DateTimeOffset.UtcNow + operationTimeout;
        while (true)
        {
            var frame = await ReadFrameAsync(until - DateTimeOffset.UtcNow, token);
            if (frame.ResponseTo == request && frame.Command == command) return frame;
            await HandleAsync(frame, token);
        }
    }

    private async Task<uint> SendAsync(ushort command, byte[] data, byte[]? key, byte flag, uint responseTo, CancellationToken token)
    {
        var id = ++sequence;
        var payload = TuyaProtocol.Encode(new(id, responseTo, command, data), key, flag);
        foreach (var packet in TuyaProtocol.Fragment(payload))
        {
            await link.WriteAsync(packet, token);
            await Task.Delay(50, token);
        }
        return id;
    }

    private async Task HandleAsync(TuyaFrame frame, CancellationToken token)
    {
        onFrame?.Invoke(frame);
        if (frame.ResponseTo != 0) return;
        if (frame.Command is 0x8001 or 0x8003)
        {
            IReadOnlyList<TuyaDataPoint> points;
            try { points = ParseReport(frame); }
            catch (InvalidDataException e)
            {
                // Jedna nieczytelna ramka nie przerywa odczytu ani nie kasuje tego, co już odebrane (tak samo tuya-ble-sdk). Inaczej
                // niż SDK potwierdzamy ją jednak niżej: czujnik oddaje historię od najstarszego rekordu, więc bez potwierdzenia ten
                // sam rekord mógłby wracać na początku każdej sesji albo wstrzymać resztę serii. Surowe bajty idą do uwag (dziennik).
                points = [];
                if (skippedReports++ == 0)
                    firstSkipped = $"{frame.Command:X4} ({e.Message}): {Convert.ToHexString(frame.Data.AsSpan(0, Math.Min(frame.Data.Length, 128)))}"
                        + (frame.Data.Length > 128 ? "…" : "");
                log?.Invoke($"  Pominięto nieczytelny raport {frame.Command:X4}: {e.Message}");
            }
            foreach (var point in points)
            {
                // Wszystkie punkty zostają, także powtórzone numery: ramki 0x8003 niosą kolejne rekordy historii czujnika.
                dataPoints.Add(point);
                log?.Invoke($"  Tuya DP {point.Id}, typ {point.Type}: {point.Value}"
                    + (point.MeasuredAt is { } measured ? $" (czas czujnika {measured.ToLocalTime():yyyy-MM-dd HH:mm:ss})" : ""));
            }
            if (sessionKey is not null) await SendAsync(frame.Command, [], sessionKey, 5, frame.Sequence, token);
        }
        else if (frame.Command is 0x8004 or 0x8005)
        {
            var note = $"Odebrano raport {frame.Command:X4}; podpisane raporty tego wariantu nie są obsługiwane.";
            if (!notes.Contains(note)) notes.Add(note);
        }
        else if (frame.Command is 0x8011 or 0x8012 && sessionKey is not null)
        {
            var now = DateTimeOffset.Now;
            var zone = new byte[2];
            BinaryPrimitives.WriteInt16BigEndian(zone, (short)(now.Offset.TotalHours * 100));
            byte[] data = frame.Command == 0x8011
                ? [.. Encoding.ASCII.GetBytes(now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)), .. zone]
                : [(byte)(now.Year % 100), (byte)now.Month, (byte)now.Day, (byte)now.Hour,
                    (byte)now.Minute, (byte)now.Second, (byte)(((int)now.DayOfWeek + 6) % 7), .. zone];
            await SendAsync(frame.Command, data, sessionKey, 5, frame.Sequence, token);
        }
    }
}
