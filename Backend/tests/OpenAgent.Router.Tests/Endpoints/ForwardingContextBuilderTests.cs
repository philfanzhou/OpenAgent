using OpenAgent.Authorization;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Endpoints;
using Xunit;

namespace OpenAgent.Router.Tests.Endpoints;

public class ForwardingContextBuilderTests
{
    [Fact]
    public async Task ApplyAsync_ReplacesClientIdentityWithTrustedContext()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://router/chat");
        request.Headers.Add("X-Agent-Id", "finance");
        request.Headers.Add("X-Tenant-Id", "tenant-1");
        request.Headers.Add("X-User-Id", "user-1");
        request.Headers.Add("X-Conversation-Id", "conversation-1");
        request.Headers.Add("Authorization", "Bearer client-token");
        request.Headers.Add(DelegatedAuthorizationHeaders.Grant, "client-grant");
        await ForwardingContextBuilder.ApplyAsync(
            request,
            new Uri("http://engine/api/v1/agent/chat"),
            new AgentUserContext { UserId = "trusted-user", TenantId = "trusted-tenant", IsAuthenticated = true },
            "trusted-tenant",
            "trusted-agent",
            "trusted-conversation",
            "trace-1",
            "trusted-grant");

        Assert.False(request.Headers.Contains("Authorization"));
        Assert.False(request.Headers.Contains("X-Agent-Id"));
        Assert.Equal("trusted-agent", Assert.Single(request.Headers.GetValues(AgentRoutingHeaders.ResolvedAgentId)));
        Assert.Equal("trusted-tenant", Assert.Single(request.Headers.GetValues("X-Tenant-Id")));
        Assert.Equal("trusted-user", Assert.Single(request.Headers.GetValues("X-User-Id")));
        Assert.Equal("trusted-conversation", Assert.Single(request.Headers.GetValues("X-Conversation-Id")));
        Assert.Equal("trace-1", Assert.Single(request.Headers.GetValues("X-Trace-Id")));
        Assert.Equal("trusted-grant", Assert.Single(request.Headers.GetValues(DelegatedAuthorizationHeaders.Grant)));
    }

    [Fact]
    public async Task ApplyAsync_RemovesSpoofedTenantWhenTrustedTenantIsAbsent()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://router/chat");
        request.Headers.Add("X-Tenant-Id", "spoofed-tenant");

        await ForwardingContextBuilder.ApplyAsync(
            request,
            new Uri("http://engine/api/v1/agent/chat"),
            new AgentUserContext { UserId = "user-1", IsAuthenticated = true },
            tenantId: null,
            agentId: null,
            conversationId: null,
            "trace-1",
            "trusted-grant");

        string? actual = request.Headers.TryGetValues(
            "X-Tenant-Id",
            out IEnumerable<string>? values)
            ? Assert.Single(values)
            : null;
        Assert.Null(actual);
    }
}
