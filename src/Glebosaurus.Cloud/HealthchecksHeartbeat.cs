using Glebosaurus.Core.Cloud;

namespace Glebosaurus.Cloud;

/// <summary>Sygnał „nadawca działa” do healthchecks.io (zwykły GET adresu sprawdzenia). Adres to sekret – nie trafia do
/// komunikatów błędów.</summary>
public sealed class HealthchecksHeartbeat(HttpClient http) : IHeartbeat
{
    public async Task PingAsync(Uri url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"healthchecks.io odpowiedział {(int)response.StatusCode}.");
        }
        catch (HttpRequestException e)
        {
            throw new HttpRequestException($"Sygnał do healthchecks.io nie przeszedł: {e.Message.Replace(url.ToString(), "<adres sygnału>")}", e);
        }
    }
}
