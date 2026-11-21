using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Glebosaurus.Tuya;

/// <summary>
/// Dane lokalnego przypisania czujnika (identyfikatory i klucz lokalny). Plik JSON tworzy <c>pair</c>
/// (<c>ID.credentials.json</c>); w chmurze leżą w Vault (strona: „Dodaj czujnik z pliku klucza”, parowanie zdalne).
/// </summary>
public sealed class TuyaCredentials
{
    public string Mac { get; set; } = "";
    public string Uuid { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string? ProductId { get; set; }
    public string? LocalKey { get; set; }
    public string? LocalKeyHex { get; set; }
    public bool BindingAttempted { get; set; }
    public bool Verified { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    /// <summary>Sześć bajtów materiału klucza (klasyczny protokół 3 używa pierwszych sześciu bajtów localKey).</summary>
    [JsonIgnore]
    public byte[] KeyBytes
    {
        get
        {
            if (!string.IsNullOrEmpty(LocalKeyHex) && !string.IsNullOrEmpty(LocalKey))
                throw new InvalidDataException("Podaj tylko localKey albo localKeyHex.");
            if (LocalKeyHex is { } hex)
            {
                var bytes = Convert.FromHexString(hex);
                if (bytes.Length != 6) throw new InvalidDataException("localKeyHex musi zawierać dokładnie 6 bajtów (12 znaków HEX).");
                return bytes;
            }
            if (LocalKey is not { Length: >= 6 } text || text.Any(c => !char.IsAscii(c)))
                throw new InvalidDataException("localKey musi zawierać co najmniej 6 znaków ASCII.");
            return Encoding.ASCII.GetBytes(text[..6]);
        }
    }

    public static TuyaCredentials Load(string path)
    {
        var credentials = JsonSerializer.Deserialize<TuyaCredentials>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("Pusty plik danych uwierzytelnienia.");
        credentials.Validate();
        return credentials;
    }

    public void Validate()
    {
        if (Mac.Length > 0 && !BleAddress.TryParse(Mac, out _))
            throw new InvalidDataException("Nieprawidłowy MAC w pliku klucza.");
        if (Uuid.Length != 16 || Uuid.Any(c => c < 32 || c >= 127) || DeviceId.Length is < 1 or > 22 || DeviceId.Any(c => c < 33 || c >= 127))
            throw new InvalidDataException("UUID musi mieć 16 znaków ASCII, deviceId od 1 do 22 znaków ASCII.");
        _ = KeyBytes;
    }

    /// <summary>Zabezpieczenie przed użyciem klucza z innego czujnika.</summary>
    public void EnsureMatches(TuyaIdentity identity)
    {
        if (Uuid != identity.Uuid)
            throw new InvalidDataException("UUID w pliku klucza nie odpowiada wykrytemu czujnikowi.");
    }

    public static TuyaCredentials Create(string mac, TuyaIdentity identity) => new()
    {
        Mac = mac, Uuid = identity.Uuid, ProductId = identity.ProductId,
        DeviceId = Convert.ToHexString(RandomNumberGenerator.GetBytes(11)).ToLowerInvariant(),
        LocalKeyHex = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant()
    };

    public void Save(string path, bool createNew = false)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        // CreateNew nie pozwala zastąpić istniejącego klucza, także przy równoległych uruchomieniach.
        if (createNew)
        {
            using var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(stream, this, JsonOptions);
            stream.Flush(true);
            return;
        }
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, this, JsonOptions);
                stream.Flush(true);
            }
            File.Move(temporary, fullPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>44 bajty: UUID (16) + klucz (6) + deviceId dopełniony zerami do 22.</summary>
    public byte[] PairingPayload() => [.. Encoding.ASCII.GetBytes(Uuid), .. KeyBytes,
        .. Encoding.ASCII.GetBytes(DeviceId.PadRight(22, '\0'))];
}
