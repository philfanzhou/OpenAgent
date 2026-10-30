using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Moq;
using OpenAgent.Capabilities.Security;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;
using OpenAgent.Engine.Tests.TestDoubles;
using Xunit;

namespace OpenAgent.Engine.Tests.Hosting;

public sealed class AuthorizationStreamingTests
{
    [Fact]
    public async Task Stream_McpAccessRevoked_CancelsDriverAndPersistsPermissionFailure()
    {
        int revoked = 0;
        Mock<IResourcePolicyStore> store = new();
        store.Setup(value => value.ReadAsync(It.IsAny<string>(), It.IsAny<AgentAuthorizationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string tenant, AgentAuthorizationRequest request, CancellationToken ct) => new ResourceAccessSnapshot(true, "v1",
                request.ResourceType == AgentResourceType.Mcp && Volatile.Read(ref revoked) == 1 ? new ResourcePolicy { Revision = 1 } : null));
        RevocationRuntimeDriver driver = new();
        await using var host = await SseToolStreamingTests.StreamingHost.StartAsync(driver,
            new AgentConfig { TenantId = "tenant-1", Mcp = new() { Servers = [new() { Name = "protected", TenantId = "tenant-1", Url = "https://mcp.test" }] } },
            configure: services =>
            {
                services.RemoveAll<IAgentAuthorizationService>();
                services.AddScoped<IAgentAuthorizationService>(_ => new ResourcePolicyAuthorizationService(store.Object,
                    Options.Create(new AgentAuthorizationOptions())));
            }, user: new AgentUserContext { UserId = "user-1", TenantId = "tenant-1", Roles = ["Admin"] });
        var response = host.PostStreamAsync("revoked-conversation");
        await driver.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Volatile.Write(ref revoked, 1);
        var frames = await response.WaitAsync(TimeSpan.FromSeconds(5));
        await driver.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Contains(frames, frame => frame.Event == "error");
        Assert.Equal("error", Assert.Single(frames, frame => frame.Event == "done").Data.GetProperty("status").GetString());
        var record = await host.Store.GetRecordAsync("tenant-1", "revoked-conversation");
        var message = Assert.Single(record!.Messages!, row => row.Role == "assistant");
        Assert.Equal("Failed", message.Metadata?.ExecutionStatus);
        Assert.NotNull(message.Metadata?.Error);
        Assert.DoesNotContain(frames, frame => frame.Data.ToString().Contains("https://mcp.test", StringComparison.Ordinal));
    }
}
