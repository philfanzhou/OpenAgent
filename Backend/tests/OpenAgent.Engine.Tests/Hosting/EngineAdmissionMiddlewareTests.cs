using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Engine.Host.Middleware;
using Xunit;

namespace OpenAgent.Engine.Tests.Hosting;

public class EngineAdmissionMiddlewareTests
{
    [Fact]
    public void EnsureChatAccess_MatchingScopedAgentGrant_Succeeds()
    {
        AgentUserContext user = CreateUser("agent.execute:intent-router");

        EngineAdmissionMiddleware.EnsureChatAccess(user, "intent-router");
    }

    [Theory]
    [InlineData("agent.execute:intent-router", "support")]
    [InlineData("agent.execute:intent-router", null)]
    public void EnsureChatAccess_NonMatchingAgent_IsRejected(
        string permission,
        string? agentId)
    {
        AgentUserContext user = CreateUser(permission);

        AgentException exception = Assert.Throws<AgentException>(() =>
            EngineAdmissionMiddleware.EnsureChatAccess(user, agentId));

        Assert.Equal(AgentErrorCode.PermissionDenied, exception.ErrorCode);
    }

    private static AgentUserContext CreateUser(string permission) => new()
    {
        UserId = "user-1",
        TenantId = "tenant-1",
        IsAuthenticated = true,
        Claims = new Dictionary<string, string>
        {
            [GatewayClaimTypes.Permission] = permission
        }
    };
}
