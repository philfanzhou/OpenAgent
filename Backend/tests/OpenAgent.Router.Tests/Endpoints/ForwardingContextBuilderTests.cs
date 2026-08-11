using OpenAgent.Contracts.Security;
using OpenAgent.Router.Endpoints;
using Xunit;

namespace OpenAgent.Router.Tests.Endpoints;

public class ForwardingContextBuilderTests
{
    [Fact]
    public async Task ApplyAsync_ReplacesUntrustedHeadersWithGatewayGrant()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://router/chat");
        request.Headers.Add("X-Agent-Id", "finance");
        var user = new AgentUserContext
        {
            UserId = "user-1",
            TenantId = "tenant-1",
            IsAuthenticated = true
        };

        await ForwardingContextBuilder.ApplyAsync(
            request,
            new Uri("http://engine/api/v1/agent/chat"),
            user,
            "tenant-1",
            "finance",
            "conversation-1",
            "trace-1",
            "gateway-grant");

        Assert.False(request.Headers.Contains("X-Agent-Id"));
        Assert.Equal(
            "finance",
            Assert.Single(request.Headers.GetValues(AgentRoutingHeaders.ResolvedAgentId)));
        Assert.Equal(
            "gateway-grant",
            Assert.Single(request.Headers.GetValues("X-OpenAgent-Gateway-Grant")));
    }
}
