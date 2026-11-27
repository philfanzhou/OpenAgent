using System.Net;
using System.Text;
using OpenAgent.Runtime.OpenCode;
using Xunit;

namespace OpenAgent.Runtime.Tests;

public sealed class OpenCodeCompactionEventsTests
{
    private const string Connected = """{"type":"server.connected","properties":{}}""";
    private const string Busy = """{"type":"session.status","properties":{"sessionID":"target","status":{"type":"busy"}}}""";
    private const string Idle = """{"type":"session.status","properties":{"sessionID":"target","status":{"type":"idle"}}}""";

    [Theory]
    [InlineData("""{"type":"session.compacted","properties":{"sessionID":"other"}}""", false, false)]
    [InlineData("""{"type":"session.compacted","properties":{"sessionID":"other"}}""", true, true)]
    [InlineData("""{"type":"session.compacted","properties":{"sessionID":"target"}}""", false, true)]
    [InlineData("""{"type":"message.updated","properties":{"sessionID":"target","info":{"role":"assistant","summary":true}}}""", false, true)]
    public async Task ReadAuditsAsync_QueriesForOwnCompactionOrPendingAudit(string notification, bool auditPending, bool shouldRead)
    {
        EventHandler handler = new(Connected, Idle, Busy, notification, Idle);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
        await using OpenCodeCompactionEvents events = await OpenCodeCompactionEvents.StartAsync(
            CreateClient(handler), "target", "/workspace", deadline.Token);

        var audits = await events.ReadAuditsAsync([], auditPending, deadline.Token);

        if (shouldRead)
        {
            Assert.Equal("/session/target/message?directory=%2Fworkspace", Assert.Single(handler.MessageRequests));
            var summary = Assert.Single(audits);
            Assert.Equal("Failed", summary.Status);
            Assert.Equal("A native summary", summary.Summary);
        }
        else
        {
            Assert.Empty(handler.MessageRequests);
            Assert.Empty(audits);
        }
    }

    [Fact]
    public async Task ReadAuditsAsync_StreamClosesAfterBusy_RequiresFallback()
    {
        EventHandler handler = new(Connected, Busy);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
        await using OpenCodeCompactionEvents events = await OpenCodeCompactionEvents.StartAsync(
            CreateClient(handler), "target", "/workspace", deadline.Token);

        await Assert.ThrowsAsync<IOException>(() => events.ReadAuditsAsync([], false, deadline.Token));
        Assert.Empty(handler.MessageRequests);
    }

    private static HttpClient CreateClient(HttpMessageHandler handler) => new(handler)
    {
        BaseAddress = new Uri("http://127.0.0.1:19000/")
    };

    private sealed class EventHandler(params string[] notifications) : HttpMessageHandler
    {
        internal List<string> MessageRequests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string route = request.RequestUri!.PathAndQuery;
            if (route.StartsWith("/event?", StringComparison.Ordinal))
            {
                string body = string.Concat(notifications.Select(value => "data: " + value + "\n\n"));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
                });
            }
            MessageRequests.Add(route);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    [
                      {
                        "info": {"role":"assistant","summary":true,"id":"summary","time":{},"error":{"name":"failed"}},
                        "parts": [{"type":"text","text":"A native summary"}]
                      }
                    ]
                    """, Encoding.UTF8, "application/json")
            });
        }
    }
}
