using System.Net.Http.Headers;
using Glebosaurus.Core;
using Glebosaurus.Core.Cloud;

namespace Glebosaurus.Cloud;

/// <summary>Sygnał „nadawca działa” do healthchecks.io (zwykły GET adresu sprawdzenia). Adres to sekret – nie trafia do
/// komunikatów błędów. Nagłówek User-Agent niesie wersję i nazwę agenta („Glebosaurus/0.2.10 (rasp3)”) – dziennik pingów w
/// healthchecks.io pokazuje go przy każdym sygnale, więc widać, który agent jest teraz nadawcą (adres IP bywa wspólny).</summary>
public sealed class HealthchecksHeartbeat(HttpClient http) : IHeartbeat
{
    public async Task PingAsync(Uri url, string agent, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Glebosaurus", AppInfo.Version));
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue($"({AgentLabel(agent)})"));
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"healthchecks.io odpowiedział {(int)response.StatusCode}.");
        }
        catch (HttpRequestException e)
        {
            throw new HttpRequestException($"Sygnał do healthchecks.io nie przeszedł: {e.Message.Replace(url.ToString(), "<adres sygnału>")}", e);
        }
    }

    /// <summary>Nazwa agenta w komentarzu nagłówka: nazwy w chmurze to małe litery, cyfry, „_” i „-”; cokolwiek innego
    /// (nagłówek z nawiasem lub znakiem sterującym by nie przeszedł) zamieniamy na „_”.</summary>
    private static string AgentLabel(string agent)
    {
        var label = new string([.. agent.Take(40).Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_')]);
        return label.Length == 0 ? "agent" : label;
    }
}
