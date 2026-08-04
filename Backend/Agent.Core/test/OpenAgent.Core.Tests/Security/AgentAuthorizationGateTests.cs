using OpenAgent.Contracts.Security;
using OpenAgent.Core.Security;
using Xunit;

namespace OpenAgent.Core.Tests.Security;

public class AgentAuthorizationGateTests
{
    [Theory]
    [InlineData(AgentResourceType.Agent)]
    [InlineData(AgentResourceType.Model)]
    [InlineData(AgentResourceType.Tool)]
    [InlineData(AgentResourceType.Function)]
    [InlineData(AgentResourceType.Mcp)]
    [InlineData(AgentResourceType.Skill)]
    public async Task EnsureAuthorizedAsync_DeniedResource_ThrowsPermissionError(
        AgentResourceType resourceType)
    {
        var service = new DenyAuthorizationService();
        var gate = new AgentAuthorizationGate(service);
        var userContext = new AgentUserContext
        {
            UserId = "user-1",
            TenantId = "tenant-1"
        };

        Contracts.Security.AgentException exception = await Assert.ThrowsAsync<Contracts.Security.AgentException>(
            () => gate.EnsureAuthorizedAsync(
                "agent-1",
                resourceType,
                "resource-1",
                "execute",
                userContext,
                CancellationToken.None));

        Assert.Equal(Contracts.Requests.AgentErrorCode.PermissionDenied, exception.ErrorCode);
        Assert.Equal(resourceType, Assert.Single(service.Requests).ResourceType);
    }

    private sealed class DenyAuthorizationService : IAgentAuthorizationService
    {
        internal List<AgentAuthorizationRequest> Requests { get; } = [];

        public Task<bool> IsAuthorizedAsync(
            AgentAuthorizationRequest request,
            IAgentUserContext userContext,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(false);
        }
    }
}
