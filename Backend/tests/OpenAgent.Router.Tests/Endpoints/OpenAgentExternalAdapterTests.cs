using OpenAgent.Contracts.Routing;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Endpoints;
using OpenAgent.Router.Options;
using Xunit;

namespace OpenAgent.Router.Tests.Endpoints;

public class OpenAgentExternalAdapterTests
{
    [Fact]
    public async Task ApplyAsync_DefaultPolicy_ReplacesCredentialsAndDoesNotForwardIdentity()
    {
        var adapter = new OpenAgentExternalAdapter();
        var agent = new ExternalAgentOptions
        {
            AgentId = "external-support",
            BaseUrl = "https://partner.example/root",
            ChatPath = "/agent/chat",
            RemoteAgentId = "support-v2",
            Authentication = new ExternalAgentAuthenticationOptions
            {
                HeaderName = "Authorization",
                Scheme = "Bearer",
                Token = "service-token"
            }
        };
        var user = new AgentUserContext
        {
            UserId = "user-1",
            TenantId = "tenant-1",
            IsAuthenticated = true
        };
        var request = new HttpRequestMessage(HttpMethod.Post, "http://untrusted");
        request.Headers.TryAddWithoutValidation("Authorization", "Basic user-token");
        request.Headers.TryAddWithoutValidation("Cookie", "session=secret");
        request.Headers.TryAddWithoutValidation("X-Api-Key", "user-api-key");
        request.Headers.TryAddWithoutValidation("X-User-Id", "spoofed-user");
        request.Headers.TryAddWithoutValidation("X-Tenant-Id", "spoofed-tenant");
        Uri target = adapter.BuildTargetUri(agent, "attachments/stream");

        await adapter.ApplyAsync(
            request,
            target,
            agent,
            user,
            "tenant-1",
            "conversation-1",
            "trace-1");

        Assert.Equal("https://partner.example/root/agent/chat/attachments/stream", request.RequestUri?.ToString());
        Assert.Equal("Bearer service-token", Assert.Single(request.Headers.GetValues("Authorization")));
        Assert.Equal("support-v2", Assert.Single(request.Headers.GetValues(AgentRoutingHeaders.ResolvedAgentId)));
        Assert.Equal("conversation-1", Assert.Single(request.Headers.GetValues("X-Conversation-Id")));
        Assert.Equal("trace-1", Assert.Single(request.Headers.GetValues("X-Trace-Id")));
        Assert.False(request.Headers.Contains("X-User-Id"));
        Assert.False(request.Headers.Contains("X-Tenant-Id"));
        Assert.False(request.Headers.Contains("Cookie"));
        Assert.False(request.Headers.Contains("X-Api-Key"));
    }

    [Fact]
    public async Task ApplyAsync_IdentityForwardingEnabled_AddsTrustedIdentityHeaders()
    {
        var adapter = new OpenAgentExternalAdapter();
        var agent = new ExternalAgentOptions
        {
            AgentId = "external-support",
            BaseUrl = "https://partner.example",
            ForwardIdentityHeaders = true
        };
        var user = new AgentUserContext
        {
            UserId = "user-1",
            TenantId = "tenant-1",
            IsAuthenticated = true
        };
        var request = new HttpRequestMessage(HttpMethod.Post, "http://untrusted");

        await adapter.ApplyAsync(
            request,
            adapter.BuildTargetUri(agent, "stream"),
            agent,
            user,
            "tenant-1",
            null,
            "trace-1");

        Assert.Equal("user-1", Assert.Single(request.Headers.GetValues("X-User-Id")));
        Assert.Equal("tenant-1", Assert.Single(request.Headers.GetValues("X-Tenant-Id")));
    }
}
