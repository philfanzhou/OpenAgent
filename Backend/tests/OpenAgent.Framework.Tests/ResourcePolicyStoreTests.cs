using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;
using OpenAgent.Framework.Configuration;
using OpenAgent.Framework.Security;
using Xunit;

namespace OpenAgent.Framework.Tests;

public sealed class ResourcePolicyStoreTests
{
    [Fact]
    public async Task Policy_MultipleInstances_IsTenantScopedAndConditionallyUpdated()
    {
        ServiceCollection services = new();
        services.AddDbContextFactory<OpenAgentDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        await using ServiceProvider provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<OpenAgentDbContext>>();
        ResourcePolicyStore first = new(factory), second = new(factory);
        await new McpConfigRepository(factory).SaveAsync("tenant", new() { Name = "mcp", Url = "https://example.test" });
        AgentAuthorizationRequest request = new("agent", AgentResourceType.Mcp, "mcp", "use");
        ResourceAccessSnapshot before = await second.ReadAsync("tenant", request);
        Assert.True(before.Exists);
        Assert.Null(before.Policy);
        Assert.False((await first.ReadAsync("other", request)).Exists);
        ResourcePolicy policy = new() { Rules = [new() { Actions = ["use"], UserIds = ["alice"] }] };
        Assert.Equal(1, (await first.SaveAsync("tenant", AgentResourceType.Mcp, "mcp", policy, 0))!.Revision);
        Assert.Equal(1, (await second.ReadAsync("tenant", request)).Policy!.Revision);
        Assert.Null(await second.SaveAsync("tenant", AgentResourceType.Mcp, "mcp", new(), 0));
        Assert.Equal(2, (await second.SaveAsync("tenant", AgentResourceType.Mcp, "mcp", new(), 1))!.Revision);
        Assert.Empty((await first.ReadAsync("tenant", request)).Policy!.Rules);
        Assert.Null((await first.ReadAsync("other", request)).Policy);
        await new McpConfigRepository(factory).SaveAsync("tenant", new() { Name = "mcp", Url = "https://changed.test" });
        Assert.NotEqual(before.Revision, (await second.ReadAsync("tenant", request)).Revision);
    }

    [Theory]
    [InlineData(AgentResourceType.Agent, "agent")]
    [InlineData(AgentResourceType.Model, "profile")]
    [InlineData(AgentResourceType.Model, "@agent/agent")]
    public async Task Read_ResourceOwnership_DoesNotTrustPolicyRow(AgentResourceType type, string id)
    {
        ServiceCollection services = new();
        services.AddDbContextFactory<OpenAgentDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<OpenAgentDbContext>>();
        await new AgentConfigRepository(factory).UpsertAsync("owner", "agent", new() { Config = new()
        { OpenCode = new() { Model = new() { ModelId = "model", Format = ApiFormat.OpenAIChatCompletions } } } }, null);
        await new LlmConfigRepository(factory).UpsertAsync("owner", "profile", new() { ModelId = "model" });
        ResourcePolicyStore store = new(factory);
        await store.SaveAsync("foreign", type, id, new() { Rules = [new() { Actions = ["*"] }] }, 0);
        Assert.False((await store.ReadAsync("foreign", new("agent", type, id, "invoke"))).Exists);
        Assert.True((await store.ReadAsync("owner", new("agent", type, id, "invoke"))).Exists);
    }
}
