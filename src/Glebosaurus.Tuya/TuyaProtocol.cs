// Protocol reference: roquerodrigo/tuya-ble-sdk and Wheemer/local-tuya-ble (MIT).
// See THIRD-PARTY-NOTICES.md for attribution and license.
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Glebosaurus.Tuya;

public sealed record TuyaFrame(uint Sequence, uint ResponseTo, ushort Command, byte[] Data);
public sealed record TuyaDataPoint(int Id, int Type, string Hex, string Value, DateTimeOffset? MeasuredAt);

public static class TuyaProtocol
{
    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++) crc = (ushort)((crc >> 1) ^ ((crc & 1) != 0 ? 0xA001 : 0));
        }
        return crc;
    }

    public static byte[] Encode(TuyaFrame frame, byte[]? key, byte securityFlag, byte[]? iv = null)
    {
        if (frame.Data.Length > 2048) throw new InvalidDataException("Za długa ramka Tuya.");
        var raw = new byte[(12 + frame.Data.Length + 2 + 15) / 16 * 16];
        BinaryPrimitives.WriteUInt32BigEndian(raw, frame.Sequence);
        BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(4), frame.ResponseTo);
        BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(8), frame.Command);
        BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(10), (ushort)frame.Data.Length);
        frame.Data.CopyTo(raw, 12);
        var end = 12 + frame.Data.Length;
        BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(end), Crc16(raw.AsSpan(0, end)));
        if (key is null)
        {
            if (securityFlag != 0) throw new ArgumentException("Brak klucza szyfrowania.");
            return [0, .. raw];
        }
        iv ??= RandomNumberGenerator.GetBytes(16);
        using var aes = Aes.Create();
        aes.Key = key;
        return [securityFlag, .. iv, .. aes.EncryptCbc(raw, iv, PaddingMode.None)];
    }

    public static TuyaFrame Decode(byte[] envelope, byte[]? key)
    {
        if (envelope.Length < 17) throw new InvalidDataException("Niepełna ramka Tuya.");
        byte[] raw;
        if (envelope[0] == 0) raw = envelope[1..];
        else
        {
            if (key is null || envelope.Length < 33 || (envelope.Length - 17) % 16 != 0)
                throw new InvalidDataException("Brak klucza lub błędna długość szyfrowanej ramki Tuya.");
            using var aes = Aes.Create();
            aes.Key = key;
            raw = aes.DecryptCbc(envelope.AsSpan(17), envelope.AsSpan(1, 16), PaddingMode.None);
        }
        if (raw.Length < 14) throw new InvalidDataException("Niepełny nagłówek Tuya.");
        var end = 12 + BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(10));
        if (end + 2 > raw.Length || BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(end)) != Crc16(raw.AsSpan(0, end)))
            throw new InvalidDataException("Niepoprawny CRC ramki Tuya (błędny klucz lub uszkodzone dane).");
        return new(BinaryPrimitives.ReadUInt32BigEndian(raw), BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(4)),
            BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(8)), raw[12..end]);
    }

    public static IReadOnlyList<byte[]> Fragment(byte[] envelope)
    {
        var result = new List<byte[]>();
        var position = 0;
        while (position < envelope.Length)
        {
            var header = new List<byte>();
            WriteVarint(header, result.Count);
            if (result.Count == 0) { WriteVarint(header, envelope.Length); header.Add(0x30); }
            var count = Math.Min(20 - header.Count, envelope.Length - position);
            result.Add([.. header, .. envelope.AsSpan(position, count)]);
            position += count;
        }
        return result;
    }

    private static void WriteVarint(List<byte> bytes, int value)
    {
        do { bytes.Add((byte)((value & 127) | (value >= 128 ? 128 : 0))); value >>= 7; } while (value != 0);
    }

    internal static int ReadVarint(ReadOnlySpan<byte> bytes, ref int position)
    {
        var value = 0;
        for (var shift = 0; shift < 28 && position < bytes.Length; shift += 7)
        {
            var b = bytes[position++];
            value |= (b & 127) << shift;
            if (b < 128) return value;
        }
        throw new InvalidDataException("Błędny nagłówek fragmentu Tuya.");
    }

    public static IReadOnlyList<TuyaDataPoint> ParseDataPoints(ReadOnlySpan<byte> data, int offset = 0, DateTimeOffset? measuredAt = null)
    {
        var result = new List<TuyaDataPoint>();
        while (offset < data.Length)
        {
            if (data.Length - offset < 3) throw new InvalidDataException("Niepełny nagłówek punktu danych Tuya.");
            var id = data[offset++];
            var type = data[offset++];
            var length = data[offset++];
            if (length > data.Length - offset) throw new InvalidDataException("Niepełna wartość punktu danych Tuya.");
            var bytes = data.Slice(offset, length);
            var value = type switch
            {
                0 or 5 => Convert.ToHexString(bytes),
                1 when length == 1 => bytes[0] == 0 ? "false" : "true",
                2 when length == 4 => BinaryPrimitives.ReadInt32BigEndian(bytes).ToString(CultureInfo.InvariantCulture),
                3 => DecodeString(bytes, id),
                4 when length == 1 => bytes[0].ToString(CultureInfo.InvariantCulture),
                _ => throw new InvalidDataException($"Nieobsługiwany typ/długość DP {id}: {type}/{length}.")
            };
            result.Add(new(id, type, Convert.ToHexString(bytes), value, measuredAt));
            offset += length;
        }
        return result;
    }

    /// <summary>Każdy błąd odczytu punktu danych to <see cref="InvalidDataException"/>, żeby sesja mogła pominąć tylko tę ramkę.</summary>
    private static string DecodeString(ReadOnlySpan<byte> bytes, int id)
    {
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException e) { throw new InvalidDataException($"Niepoprawny tekst UTF-8 w DP {id}.", e); }
    }
}

public sealed class TuyaReassembler
{
    private readonly List<byte> buffer = [];
    private int length;
    private int next;

    public byte[]? Feed(ReadOnlySpan<byte> packet)
    {
        try
        {
            var position = 0;
            var number = TuyaProtocol.ReadVarint(packet, ref position);
            if (number == 0)
            {
                Reset();
                length = TuyaProtocol.ReadVarint(packet, ref position);
                if (length < 1 || length > 4096 || position >= packet.Length || packet[position++] >> 4 != 3)
                    throw new InvalidDataException("Nieobsługiwana długość lub wersja protokołu Tuya.");
            }
            if (length == 0 || number != next++) throw new InvalidDataException("Nieprawidłowa kolejność fragmentów Tuya.");
            buffer.AddRange(packet[position..]);
            if (buffer.Count > length) throw new InvalidDataException("Za długa ramka Tuya.");
            if (buffer.Count < length) return null;
            var result = buffer.ToArray();
            Reset();
            return result;
        }
        catch { Reset(); throw; }
    }

    private void Reset() { buffer.Clear(); length = 0; next = 0; }
}
