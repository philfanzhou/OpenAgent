using System.Net;
using System.Net.Http.Json;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Hosting.Authorization;
using OpenAgent.Router.Endpoints;
using OpenAgent.Router.Models;
using Xunit;

namespace OpenAgent.Router.Tests.Endpoints;

public class EngineAgentClientTests
{
    [Fact]
    public async Task ListAgentsAsync_AuthenticatedRequest_PropagatesRoutingIdentity()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new[]
            {
                new AgentSummary { AgentId = "finance", Name = "Finance" }
            })
        });
        var client = new EngineAgentClient(new HttpClient(handler));

        IReadOnlyList<AgentSummary> result = await client.ListAgentsAsync(
            "http://engine/",
            new DownstreamRequestIdentity(
                "signed-grant",
                "tenant-1",
                "trace-1"),
            CancellationToken.None);

        AgentSummary agent = Assert.Single(result);
        Assert.Equal("finance", agent.AgentId);
        Assert.Equal("http://engine/api/v1/agent/agents", handler.RequestUri);
        Assert.Equal("signed-grant", handler.Headers[GatewayAuthorizationDefaults.GrantHeaderName]);
        Assert.DoesNotContain("Authorization", handler.Headers);
        Assert.Equal("tenant-1", handler.Headers["X-Tenant-Id"]);
        Assert.Equal("trace-1", handler.Headers["X-Trace-Id"]);
    }

    [Fact]
    public async Task ChatAsync_IntentRequest_UsesConfiguredAgentAndMessage()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new ChatResponse { Message = "selected" })
        });
        var client = new EngineAgentClient(new HttpClient(handler));

        string? result = await client.ChatAsync(
            "http://engine",
            new DownstreamRequestIdentity("signed-grant", null, null),
            "intent-router",
            "choose an agent",
            CancellationToken.None);

        Assert.Equal("selected", result);
        Assert.Contains("\"message\":\"choose an agent\"", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\"agentId\":\"intent-router\"", handler.Body, StringComparison.Ordinal);
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public string? RequestUri { get; private set; }
        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri?.ToString();
            foreach ((string name, IEnumerable<string> values) in request.Headers)
            {
                Headers[name] = string.Join(",", values);
            }

            Body = request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return responseFactory(request);
        }
    }
}
