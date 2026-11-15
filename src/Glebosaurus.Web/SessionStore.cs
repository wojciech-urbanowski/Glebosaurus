using System.Security.Cryptography;
using System.Text;

namespace Glebosaurus.Web;

/// <summary>
/// Sesje zalogowanych przeglądarek, tylko w pamięci (restart monitora wylogowuje). Ciasteczko niesie losowy token
/// 256-bitowy; tu trzymany jest jego skrót SHA-256 z czasem wygaśnięcia. Liczba sesji jest ograniczona – przy nadmiarze
/// wypada najstarsza.
/// </summary>
public sealed class SessionStore(TimeSpan lifetime, int maxSessions = 32)
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, DateTimeOffset> sessions = new(StringComparer.Ordinal);

    public TimeSpan Lifetime => lifetime;

    public string Create(DateTimeOffset now)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        lock (gate)
        {
            RemoveExpired(now);
            while (sessions.Count >= maxSessions)
                sessions.Remove(sessions.MinBy(s => s.Value).Key);
            sessions[Key(token)] = now + lifetime;
        }
        return token;
    }

    public bool IsValid(string? token, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 100) return false;
        lock (gate)
        {
            var key = Key(token);
            if (!sessions.TryGetValue(key, out var expires)) return false;
            if (expires > now) return true;
            sessions.Remove(key);
            return false;
        }
    }

    public void Remove(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 100) return;
        lock (gate) sessions.Remove(Key(token));
    }

    public int Count
    {
        get { lock (gate) return sessions.Count; }
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (var expired in sessions.Where(s => s.Value <= now).Select(s => s.Key).ToList()) sessions.Remove(expired);
    }

    private static string Key(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
