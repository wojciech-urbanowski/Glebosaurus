using Glebosaurus.Cloud.Admin;
using Glebosaurus.Core;
using Glebosaurus.Core.Cloud;
using Glebosaurus.Core.Config;
using Microsoft.Extensions.Logging.Abstractions;

namespace Glebosaurus.Cloud.Tests;

/// <summary>
/// Właściciel zarządza agentami (<c>cloud agent</c>) na prawdziwej chmurze: zmiana roli dociera do działającego agenta bez
/// nowej wersji konfiguracji i bez ponownej rejestracji, a odcięcie agenta działa od razu, kasuje jego niewykorzystane kody i
/// da się je odwrócić nowym kodem.
/// </summary>
public sealed class AgentAdminEndToEndTests(CloudDatabase db) : IDisposable
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"glebosaurus-e2e-agents-{Guid.NewGuid():N}");

    public void Dispose()
    {
        http.Dispose();
        try { Directory.Delete(directory, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private static SyncRequest Heartbeat() => new(new AgentStatus(AppInfo.Version, "test"));

    private async Task<SupabaseCloudApi> EnrollAsync(string code, CancellationToken ct)
    {
        var enrolled = await SupabaseCloudApi.EnrollAsync(http, db.Settings.Url, db.Settings.PublishableKey!, code, ct);
        return new SupabaseCloudApi(http, db.Settings.Url, db.Settings.PublishableKey!, enrolled.Token);
    }

    [Fact]
    public async Task RoleChange_ReachesRunningAgent_WithoutReenrollment()
    {
        db.SkipIfUnavailable();
        Assert.SkipWhen(db.Settings.PublishableKey is null, "Brak klucza publikowalnego w pliku próbnego projektu.");
        var ct = TestContext.Current.CancellationToken;
        var name = Scenario.Unique("rola");
        var api = await EnrollAsync(await CloudAgents.CreateWithCodeAsync(db.DataSource, name, senderPriority: null, ct), ct);
        var source = new CloudConfigSource(api, new CloudConfigCache(Path.Combine(directory, "cloud-config.bin"), LocalProtectors.ForThisPlatform()),
            new AgentConfig(), TimeProvider.System, NullLogger.Instance);
        var started = await source.LoadAsync(ct);
        Assert.False(started.Agent.CanSendMail);
        Assert.Null(started.Mail);   // zbieracz nie dostaje odbiorców

        // cloud agent --name … --sender-priority 3 --confirm: kod zostaje niewykorzystany, token działa dalej.
        await CloudAgents.CreateWithCodeAsync(db.DataSource, name, senderPriority: 3, ct);
        var sync = await api.SyncAsync(Heartbeat(), ct);
        Assert.Equal(new AgentIdentity(name, true, 3), sync.Agent);

        Assert.True(await source.RefreshAsync(sync.ConfigVersion, sync.Agent, ct));
        Assert.True(source.Current!.Agent.CanSendMail);
        Assert.Equal(3, source.Current.Agent.SenderPriority);
        Assert.NotNull(source.Current.Mail);
        Assert.False(await source.RefreshAsync(sync.ConfigVersion, sync.Agent, ct));

        // I z powrotem tylko zbieracz.
        await CloudAgents.CreateWithCodeAsync(db.DataSource, name, senderPriority: null, ct);
        sync = await api.SyncAsync(Heartbeat(), ct);
        Assert.True(await source.RefreshAsync(sync.ConfigVersion, sync.Agent, ct));
        Assert.False(source.Current!.Agent.CanSendMail);
        Assert.Null(source.Current.Mail);
    }

    [Fact]
    public async Task Revoke_CutsAgentOff_CancelsUnusedCodes_AndNewCodeLetsItBack()
    {
        db.SkipIfUnavailable();
        Assert.SkipWhen(db.Settings.PublishableKey is null, "Brak klucza publikowalnego w pliku próbnego projektu.");
        var ct = TestContext.Current.CancellationToken;
        var name = Scenario.Unique("odciety");
        var api = await EnrollAsync(await CloudAgents.CreateWithCodeAsync(db.DataSource, name, senderPriority: 2, ct), ct);
        await api.SyncAsync(Heartbeat(), ct);
        var leaked = await CloudAgents.CreateWithCodeAsync(db.DataSource, name, senderPriority: 2, ct);   // np. widoczny na ekranie

        var revoked = await CloudAgents.RevokeAsync(db.DataSource, name, ct);

        Assert.Equal(new CloudAgentRevocation(HadToken: true, CancelledCodes: 1), revoked);
        Assert.Equal(CloudErrorKind.Unauthorized, (await Assert.ThrowsAsync<CloudApiException>(() => api.SyncAsync(Heartbeat(), ct))).Kind);
        Assert.Equal(CloudErrorKind.Unauthorized, (await Assert.ThrowsAsync<CloudApiException>(() => api.GetConfigAsync(ct))).Kind);
        Assert.Equal(CloudErrorKind.Unauthorized, (await Assert.ThrowsAsync<CloudApiException>(() => EnrollAsync(leaked, ct))).Kind);
        var listed = Assert.Single(await CloudAgents.ListAsync(db.DataSource, ct), a => a.Name == name);
        Assert.False(listed.Enrolled);
        Assert.Equal(2, listed.SenderPriority);   // rola zostaje
        Assert.Equal(new CloudAgentRevocation(HadToken: false, CancelledCodes: 0), await CloudAgents.RevokeAsync(db.DataSource, name, ct));

        var back = await EnrollAsync(await CloudAgents.CreateWithCodeAsync(db.DataSource, name, senderPriority: 2, ct), ct);
        Assert.Equal(name, (await back.SyncAsync(Heartbeat(), ct)).Agent.Name);

        await Assert.ThrowsAsync<ArgumentException>(() => CloudAgents.RevokeAsync(db.DataSource, Scenario.Unique("nieznany"), ct));
    }
}
