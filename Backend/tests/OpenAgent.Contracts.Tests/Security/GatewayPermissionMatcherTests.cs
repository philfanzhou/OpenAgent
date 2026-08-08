using OpenAgent.Contracts.Security;
using Xunit;

namespace OpenAgent.Contracts.Tests.Security;

public class GatewayPermissionMatcherTests
{
    [Theory]
    [InlineData("agent.execute:intent-router", "agent.execute", true)]
    [InlineData("agent.execute:*", "agent.execute", true)]
    [InlineData("agent.read:finance", "agent.execute", false)]
    [InlineData("agent.execute:", "agent.execute", false)]
    public void HasGrantForAnyResource_RecognizesScopedEndpointAccess(
        string granted,
        string required,
        bool expected)
    {
        Assert.Equal(
            expected,
            GatewayPermissionMatcher.HasGrantForAnyResource([granted], required));
    }

    [Theory]
    [InlineData("*", "agent.execute", "finance", true)]
    [InlineData("agent.execute", "agent.execute", "finance", true)]
    [InlineData("agent.execute:*", "agent.execute", "finance", true)]
    [InlineData("agent.execute:finance", "agent.execute", "finance", true)]
    [InlineData("agent.execute:finance", "agent.execute", "support", false)]
    [InlineData("agent.read", "agent.execute", "finance", false)]
    public void IsAllowed_EnforcesPermissionAndOptionalResource(
        string granted,
        string required,
        string resourceId,
        bool expected)
    {
        Assert.Equal(expected, GatewayPermissionMatcher.IsAllowed([granted], required, resourceId));
    }
}
