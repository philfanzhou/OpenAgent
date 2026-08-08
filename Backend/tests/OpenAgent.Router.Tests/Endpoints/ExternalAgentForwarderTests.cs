using Moq;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Endpoints;
using OpenAgent.Router.Options;
using OpenAgent.Router.Routing;
using Xunit;
using Yarp.ReverseProxy.Forwarder;

namespace OpenAgent.Router.Tests.Endpoints;

public class ExternalAgentForwarderTests
{
    [Fact]
    public void BuildExternalPermissions_DelegatesRuntimeUseWithoutManagementPermissions()
    {
        var agent = new ExternalAgentOptions
        {
            AgentId = "external-support",
            RemoteAgentId = "support-v2",
            BaseUrl = "https://partner.example"
        };
        ExternalAgentRegistry registry = new(Microsoft.Extensions.Options.Options.Create(new ExternalAgentRoutingOptions
        {
            Agents = [agent]
        }));
        using var forwarder = new ExternalAgentForwarder(
            Mock.Of<IHttpForwarder>(),
            registry,
            new TestGatewayAuthorizationService(),
            [new OpenAgentExternalAdapter()]);
        var user = new AgentUserContext
        {
            UserId = "admin-1",
            TenantId = "tenant-1",
            IsAuthenticated = true
        };

        IReadOnlyList<string> permissions = forwarder.BuildExternalPermissions(agent, user);

        Assert.Contains("agent.execute:support-v2", permissions);
        Assert.Contains(GatewayPermissions.ModelInvoke, permissions);
        Assert.Contains(GatewayPermissions.McpUse, permissions);
        Assert.DoesNotContain("*", permissions);
        Assert.DoesNotContain(GatewayPermissions.AgentConfigWrite, permissions);
        Assert.DoesNotContain(GatewayPermissions.ConversationDelete, permissions);
    }
}
