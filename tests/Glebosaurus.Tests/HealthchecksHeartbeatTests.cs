using System.Net;
using Glebosaurus.Cloud;
using Glebosaurus.Core;

namespace Glebosaurus.Tests;

/// <summary>Sygnał dla healthchecks.io: User-Agent z wersją i nazwą agenta, adres nigdy w komunikatach błędów.</summary>
public sealed class HealthchecksHeartbeatTests
{
    private static readonly Uri Ping = new("https://hc-ping.com/sekretny-adres");

    [Fact]
    public async Task Ping_CarriesVersionAndAgentNameInUserAgent()
    {
        var handler = new StubHandler(HttpStatusCode.OK);
        var heartbeat = new HealthchecksHeartbeat(new HttpClient(handler));

        await heartbeat.PingAsync(Ping, "rasp3", CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(Ping, request.RequestUri);
        Assert.Equal($"Glebosaurus/{AppInfo.Version} (rasp3)", request.UserAgent);
    }

    [Theory]
    [InlineData("bestia5", "(bestia5)")]
    [InlineData("a)b (c\r\nX-Evil: 1", "(a_b__c__X-Evil__1)")]   // nawias i znaki sterujące nie wchodzą do nagłówka
    [InlineData("", "(agent)")]
    public async Task Ping_SanitizesAgentName(string agent, string expectedComment)
    {
        var handler = new StubHandler(HttpStatusCode.OK);

        await new HealthchecksHeartbeat(new HttpClient(handler)).PingAsync(Ping, agent, CancellationToken.None);

        Assert.EndsWith(" " + expectedComment, Assert.Single(handler.Requests).UserAgent);
    }

    [Fact]
    public async Task FailedPing_NeverRevealsTheAddress()
    {
        var heartbeat = new HealthchecksHeartbeat(new HttpClient(new StubHandler(HttpStatusCode.InternalServerError)));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => heartbeat.PingAsync(Ping, "rasp3", CancellationToken.None));

        Assert.DoesNotContain("sekretny-adres", error.Message);
        Assert.Contains("500", error.Message);
    }

    private sealed record Seen(HttpMethod Method, Uri? RequestUri, string UserAgent);

    private sealed class StubHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new(request.Method, request.RequestUri, request.Headers.UserAgent.ToString()));
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }
}
