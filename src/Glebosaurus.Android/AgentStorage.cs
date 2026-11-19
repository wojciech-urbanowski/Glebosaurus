using Android.Content;
using Android.Security.Keystore;
using Glebosaurus.Core.Cloud;
using Glebosaurus.Core.Logging;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;

namespace Glebosaurus.Android;

/// <summary>
/// Dane agenta w prywatnym katalogu aplikacji (innym aplikacjom niedostępnym): baza SQLite (bufor i kopia odczytów),
/// dzienniki, token agenta i ostatnia dobra kopia konfiguracji z kluczami Tuya – oba zaszyfrowane kluczem z Android Keystore.
/// Adres projektu i klucz publikowalny (nie są sekretem) oraz to, czy agent ma pracować, w ustawieniach aplikacji.
/// </summary>
internal sealed class AgentStorage(Context context)
{
    private const string Preferences = "glebosaurus";

    public string Directory => context.FilesDir!.AbsolutePath;
    public string DatabasePath => Path.Combine(Directory, "glebosaurus.db");
    public string LogDirectory => Path.Combine(Directory, "logs");
    public DailyLogFiles LogFiles => new(LogDirectory);
    public string TokenPath => Path.Combine(Directory, "glebosaurus-cloud.token");
    public string ConfigCachePath => Path.Combine(Directory, "glebosaurus-cloud-config.bin");

    public ILocalProtector Protector { get; } = new KeystoreProtector();

    public AgentTokenStore Tokens => new(TokenPath, Protector);
    public CloudConfigCache ConfigCache => new(ConfigCachePath, Protector);

    private ISharedPreferences Prefs => context.GetSharedPreferences(Preferences, FileCreationMode.Private)!;

    /// <summary>Adres projektu i klucz publikowalny z ostatniej rejestracji; null – telefon nie był rejestrowany.</summary>
    public (Uri Url, string PublishableKey)? Cloud =>
        Prefs.GetString("cloud_url", null) is { } url && Prefs.GetString("cloud_key", null) is { } key ? (new Uri(url), key) : null;

    /// <summary>Nazwa agenta w chmurze z rejestracji (do ekranu, zanim przyjdzie konfiguracja).</summary>
    public string? AgentName => Prefs.GetString("agent_name", null);

    /// <summary>Czy agent ma pracować (także po restarcie telefonu i po aktualizacji aplikacji).</summary>
    public bool Enabled
    {
        get => Prefs.GetBoolean("enabled", false);
        set => Prefs.Edit()!.PutBoolean("enabled", value)!.Apply();
    }

    public bool IsEnrolled => Cloud is not null && File.Exists(TokenPath);

    /// <summary>Po rejestracji: nowy projekt albo nowy token. Kopia konfiguracji z poprzedniego projektu przestaje obowiązywać.</summary>
    public async Task SaveEnrollmentAsync(EnrollmentLink link, EnrollResult enrolled, CancellationToken cancellationToken)
    {
        var previous = Cloud;
        await Tokens.SaveAsync(enrolled.Token, cancellationToken);
        if (previous is { } old && !string.Equals(old.Url.Host, link.ProjectUrl.Host, StringComparison.OrdinalIgnoreCase))
            File.Delete(ConfigCachePath);
        Prefs.Edit()!
            .PutString("cloud_url", link.ProjectUrl.GetLeftPart(UriPartial.Authority))!
            .PutString("cloud_key", link.PublishableKey)!
            .PutString("agent_name", enrolled.Agent)!
            .Commit();
    }
}

/// <summary>
/// AES-GCM kluczem z Android Keystore (zasada 1: „DPAPI / Android Keystore / plik 600”). Klucz nie opuszcza Keystore i ginie
/// z odinstalowaniem aplikacji – wtedy token i kopia konfiguracji są nieczytelne (nowa rejestracja), co jest zamierzone.
/// </summary>
internal sealed class KeystoreProtector : ILocalProtector
{
    private const string Alias = "glebosaurus-local-secrets";
    private const string Transformation = "AES/GCM/NoPadding";

    public string Description => "Android Keystore (AES-GCM)";

    public byte[] Protect(byte[] data)
    {
        var cipher = Cipher.GetInstance(Transformation)!;
        cipher.Init(CipherMode.EncryptMode, Key());
        var iv = cipher.GetIV()!;
        var sealedData = cipher.DoFinal(data)!;
        return [(byte)iv.Length, .. iv, .. sealedData];
    }

    public byte[] Unprotect(byte[] data)
    {
        try
        {
            var ivLength = data[0];
            var cipher = Cipher.GetInstance(Transformation)!;
            cipher.Init(CipherMode.DecryptMode, Key(), new GCMParameterSpec(128, data[1..(1 + ivLength)]));
            return cipher.DoFinal(data[(1 + ivLength)..])!;
        }
        catch (Exception e) when (e is GeneralSecurityException or Java.Lang.IllegalArgumentException or Java.Lang.IllegalStateException
                                      or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            throw new InvalidDataException("Android Keystore nie odszyfrował danych (aplikacja zainstalowana od nowa?).", e);
        }
    }

    private static IKey Key()
    {
        var store = KeyStore.GetInstance("AndroidKeyStore")!;
        store.Load(null);
        if (store.GetKey(Alias, null) is { } existing) return existing;
        var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, "AndroidKeyStore")!;
        generator.Init(new KeyGenParameterSpec.Builder(Alias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)!
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)!
            .SetKeySize(256)!
            .Build());
        return generator.GenerateKey()!;
    }
}
