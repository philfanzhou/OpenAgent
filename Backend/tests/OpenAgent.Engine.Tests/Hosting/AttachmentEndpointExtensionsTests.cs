using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using OpenAgent.Contracts.Routing;
using OpenAgent.Engine.Host.Extensions;
using Xunit;

namespace OpenAgent.Engine.Tests.Hosting;

public class AttachmentEndpointExtensionsTests
{
    [Fact]
    public void ResolveAgentId_TrustedResolvedHeaderWinsOverMultipartInput()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[AgentRoutingHeaders.ResolvedAgentId] = "finance";
        context.Request.Headers["X-Agent-Id"] = "untrusted-header";
        var form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["agentId"] = "untrusted-form"
        });

        string? agentId = AttachmentEndpointExtensions.ResolveAgentId(
            context.Request,
            form);

        Assert.Equal("finance", agentId);
    }
}
