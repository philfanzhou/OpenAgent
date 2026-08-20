using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Models;
using OpenAgent.Core.Security;
using Xunit;

namespace OpenAgent.Core.Tests.Security;

public class AgentAuthorizationGateTests
{
    private class DenyAllAuthorizationService : IAgentAuthorizationService
    {
        public Task<bool> IsAuthorizedAsync(
            AgentAuthorizationRequest request,
            IAgentUserContext userContext,
            CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }

    private static AgentUserContext Context() => new() { UserId = "u1", TenantId = "t1" };

    [Fact]
    public async Task IsAuthorizedAsync_AllowAll_ReturnsTrue()
    {
        var gate = new AgentAuthorizationGate(
            new AllowAllAgentAuthorizationService(),
            new LlmRegistry());

        var result = await gate.IsAuthorizedAsync(
            "a1", AgentResourceType.Agent, "a1", "execute", Context(), default);

        Assert.True(result);
    }

    [Fact]
    public async Task IsAuthorizedAsync_DenyAll_ReturnsFalse()
    {
        var gate = new AgentAuthorizationGate(
            new DenyAllAuthorizationService(),
            new LlmRegistry());

        var result = await gate.IsAuthorizedAsync(
            "a1", AgentResourceType.Agent, "a1", "execute", Context(), default);

        Assert.False(result);
    }

    [Fact]
    public async Task EnsureAuthorizedAsync_Deny_ThrowsAgentException()
    {
        var gate = new AgentAuthorizationGate(
            new DenyAllAuthorizationService(),
            new LlmRegistry());

        await Assert.ThrowsAsync<AgentException>(
            () => gate.EnsureAuthorizedAsync(
                "a1", AgentResourceType.Agent, "a1", "execute", Context(), default));
    }

    [Fact]
    public async Task ResolveAuthorizedModelAsync_Allow_ModelResolved()
    {
        var registry = new LlmRegistry();
        registry.Register(new LlmProviderProfile
        {
            TenantId = "t1",
            Id = "azure",
            Format = ApiFormat.OpenAIChatCompletions,
            Endpoint = "https://azure.example.com",
            ApiKey = "sk-test"
        });

        var gate = new AgentAuthorizationGate(
            new AllowAllAgentAuthorizationService(),
            registry);

        var resolved = await gate.ResolveAuthorizedModelAsync(
            "a1",
            new LlmConfig { Provider = "azure", ModelId = "gpt-4o" },
            Context(),
            default);

        Assert.Equal("https://azure.example.com", resolved.Endpoint);
        Assert.Equal("sk-test", resolved.ApiKey);
    }

    [Fact]
    public async Task ResolveAuthorizedModelAsync_DeniedModel_ThrowsPermissionDenied()
    {
        var registry = new LlmRegistry();
        registry.Register(new LlmProviderProfile
        {
            TenantId = "t1",
            Id = "azure",
            Endpoint = "https://llm.example.test",
            ApiKey = "secret"
        });
        var gate = new AgentAuthorizationGate(
            new DenyAllAuthorizationService(),
            registry);

        AgentException exception = await Assert.ThrowsAsync<AgentException>(
            () => gate.ResolveAuthorizedModelAsync(
                "a1",
                new LlmConfig { Provider = "azure", ModelId = "gpt-4o" },
                Context(),
                default));

        Assert.Equal(AgentErrorCode.PermissionDenied, exception.ErrorCode);
    }

    [Fact]
    public async Task ResolveAuthorizedModelAsync_EmptyEndpoint_ThrowsDependencyUnavailable()
    {
        var registry = new LlmRegistry();
        registry.Register(new LlmProviderProfile
        {
            TenantId = "t1",
            Id = "bad",
            Endpoint = "",
            ApiKey = ""
        });

        var gate = new AgentAuthorizationGate(
            new AllowAllAgentAuthorizationService(),
            registry);

        AgentException exception = await Assert.ThrowsAsync<AgentException>(
            () => gate.ResolveAuthorizedModelAsync(
                "a1",
                new LlmConfig { Provider = "bad", ModelId = "gpt-4o" },
                Context(),
                default));

        Assert.Equal(AgentErrorCode.DependencyUnavailable, exception.ErrorCode);
    }

    [Fact]
    public async Task ResolveAuthorizedModelAsync_DifferentTenant_ThrowsTenantIsolation()
    {
        var registry = new LlmRegistry();
        registry.Register(new LlmProviderProfile
        {
            TenantId = "t2",
            Id = "other-tenant",
            Endpoint = "https://llm.example.com",
            ApiKey = "secret"
        });
        var gate = new AgentAuthorizationGate(
            new AllowAllAgentAuthorizationService(),
            registry);

        await Assert.ThrowsAsync<TenantDataIsolationException>(() =>
            gate.ResolveAuthorizedModelAsync(
                "a1",
                new LlmConfig { Provider = "other-tenant", ModelId = "model" },
                Context(),
                default));
    }
}
