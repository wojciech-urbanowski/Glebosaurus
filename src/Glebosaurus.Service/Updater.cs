using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Glebosaurus.Core;
using Glebosaurus.Core.Updates;
using Microsoft.Extensions.Logging;

namespace Glebosaurus.Service;

/// <summary>
/// Aktualizacja z wydań na GitHubie. Usługa tylko sprawdza, czy jest nowsza wersja, i przekazuje instalację skryptowi
/// <c>install-windows.ps1</c> z tego samego wydania (sprawdzonemu skrótem SHA-256) – ten sam skrypt instaluje usługę od zera,
/// więc logika podmiany plików jest w jednym miejscu. Skrypt czeka, aż usługa się zakończy (jej plików nie da się podmienić
/// w trakcie pracy), podmienia <c>app\</c> i uruchamia zadanie Harmonogramu z nową wersją.
/// </summary>
internal sealed class Updater(ServicePaths paths, InstallSettings settings, HttpClient http, ILogger logger, TimeProvider time)
{
    public static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Glebosaurus", AppInfo.Version));
        return client;
    }

    /// <summary>Oferta aktualizacji albo null (aktualna wersja, brak wydań, wstrzymana próba). Błędy sieci przechodzą dalej.</summary>
    public async Task<UpdateOffer?> CheckAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, settings.LatestReleaseUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            logger.LogInformation("Repozytorium {Repository} nie ma jeszcze wydań (albo nie jest publiczne).", settings.Repository);
            return null;
        }
        response.EnsureSuccessStatusCode();
        var latest = ReleaseInfo.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var decision = UpdatePolicy.Decide(AppInfo.ParsedVersion, latest, UpdateAttempt.Load(paths.UpdateAttempt), time.GetUtcNow());
        var level = decision.Verdict is UpdateVerdict.UpToDate or UpdateVerdict.Available ? LogLevel.Information : LogLevel.Warning;
        logger.Log(level, "Wydania: {Reason}", decision.Reason);
        return decision.Offer;
    }

    /// <summary>
    /// Pobiera i sprawdza skrypt instalacji nowej wersji i uruchamia go bez okna. True – skrypt ruszył i usługa ma się
    /// zakończyć (monitor musi być już zatrzymany); false – nic się nie zmieniło, usługa pracuje dalej.
    /// </summary>
    public async Task<bool> HandOffAsync(UpdateOffer offer, CancellationToken cancellationToken)
    {
        var version = ReleaseVersion.Format(offer.Version);
        try
        {
            var sums = Sha256Sums.Parse(Encoding.UTF8.GetString(await DownloadAsync(offer.Checksums.DownloadUrl, cancellationToken)));
            var script = await DownloadAsync(offer.Installer.DownloadUrl, cancellationToken);
            Sha256Sums.Verify(sums, offer.Installer.Name, Sha256Sums.Compute(script));
            Directory.CreateDirectory(paths.Updates);
            var scriptPath = Path.Combine(paths.Updates, $"install-windows-{version}.ps1");
            await File.WriteAllBytesAsync(scriptPath, script, cancellationToken);

            // Ślad próby przed startem skryptu: jeśli skrypt w ogóle nie ruszy, następny start usługi nie wpadnie w pętlę.
            new UpdateAttempt(offer.Version, time.GetUtcNow(), null).Save(paths.UpdateAttempt);
            var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = paths.Updates
            };
            foreach (var argument in new[]
                     {
                         "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath,
                         "-Version", version, "-InstallDir", paths.Root, "-Unattended"
                     })
                info.ArgumentList.Add(argument);
            using var process = Process.Start(info) ?? throw new InvalidOperationException("Nie udało się uruchomić PowerShella.");
            logger.LogInformation("Aktualizacja do {Version}: uruchomiono skrypt {Script} (proces {Pid}); usługa kończy pracę, " +
                "a skrypt uruchomi nową wersję (jego dziennik: {Log}).", version, scriptPath, process.Id, Path.Combine(paths.Updates, "install-last.log"));
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or InvalidDataException or IOException or UnauthorizedAccessException
                                      or Win32Exception or InvalidOperationException or TaskCanceledException)
        {
            logger.LogError("Nie udało się przekazać aktualizacji do {Version}: {Error}", version, e.Message);
            return false;
        }
    }

    private async Task<byte[]> DownloadAsync(Uri url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }
}
