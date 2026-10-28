using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Security;
using Xunit;

namespace OpenAgent.Core.Tests.Security;

public class AgentAuthorizationGateTests
{
    [Fact]
    public async Task IsAuthorizedAsync_AllowAll_ReturnsTrue()
    {
        var gate = new AgentAuthorizationGate(new AllowAllAgentAuthorizationService());

        bool result = await gate.IsAuthorizedAsync(
            "a1", AgentResourceType.Agent, "a1", "execute", Context(), default);

        Assert.True(result);
    }

    [Fact]
    public async Task EnsureAgentAuthorizedAsync_Deny_ThrowsAgentException()
    {
        var gate = new AgentAuthorizationGate(new DenyAllAuthorizationService());

        await Assert.ThrowsAsync<AgentException>(
            () => gate.EnsureAgentAuthorizedAsync("a1", Context(), default));
    }

    [Fact]
    public async Task EnsureModelAuthorizedAsync_UsesSelectedProfileAndModel()
    {
        var authorization = new RecordingAuthorizationService();
        var gate = new AgentAuthorizationGate(authorization);

        await gate.EnsureModelAuthorizedAsync(
            "a1",
            new LlmConfig { Provider = "profile-1", ModelId = "model-1" },
            Context(),
            default);

        Assert.Equal(AgentResourceType.Model, authorization.Request?.ResourceType);
        Assert.Equal("profile-1/model-1", authorization.Request?.ResourceId);
    }

    private static AgentUserContext Context() => new() { UserId = "u1", TenantId = "t1" };

    private sealed class DenyAllAuthorizationService : IAgentAuthorizationService
    {
        public Task<bool> IsAuthorizedAsync(
            AgentAuthorizationRequest request,
            IAgentUserContext userContext,
            CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class RecordingAuthorizationService : IAgentAuthorizationService
    {
        internal AgentAuthorizationRequest? Request { get; private set; }

        public Task<bool> IsAuthorizedAsync(
            AgentAuthorizationRequest request,
            IAgentUserContext userContext,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(true);
        }
    }
}
