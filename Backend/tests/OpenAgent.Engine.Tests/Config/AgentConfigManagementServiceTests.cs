using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Models;
using OpenAgent.Engine.Config;
using Xunit;

namespace OpenAgent.Engine.Tests.Config;

public class AgentConfigManagementServiceTests
{
    [Fact]
    public async Task SaveAsync_StampsTenantOwnershipOnNestedBindings()
    {
        var repository = new RecordingRepository();
        AgentConfigManagementService manager = CreateManager(repository);
        AgentConfigEntity entity = new()
        {
            Config = new AgentConfig
            {
                Mcp = new McpConfig { Servers = [new McpServerConfig { Name = "tools" }] },
                Rag = new RagConfig { Instances = [new RagInstanceConfig { Id = "knowledge" }] },
                Skills = new SkillsConfig { Instances = [new SkillInstanceConfig { Id = "search" }] }
            }
        };

        AgentConfigEntity? saved = await manager.SaveAsync(
            "support", "tenant-a", entity, null);

        Assert.Equal("tenant-a", saved?.TenantId);
        Assert.Equal("tenant-a", saved?.Config.TenantId);
        Assert.Equal("tenant-a", Assert.Single(saved!.Config.Mcp.Servers).TenantId);
        Assert.Equal(["tenant-a"], Assert.Single(saved.Config.Rag.Instances).AllowedTenantIds);
        Assert.Equal(["tenant-a"], Assert.Single(saved.Config.Skills.Instances).AllowedTenantIds);
    }

    [Fact]
    public async Task GetAsync_DifferentTenant_ReturnsNull()
    {
        var repository = new RecordingRepository();
        AgentConfigManagementService manager = CreateManager(repository);
        await manager.SaveAsync("support", "tenant-a", new AgentConfigEntity(), null);

        AgentConfigEntity? foreign = await manager.GetAsync("support", "tenant-b");

        Assert.Null(foreign);
    }

    private static AgentConfigManagementService CreateManager(IAgentConfigRepository repository)
    {
        var store = new AgentConfigDatabaseStore(
            new FakeRedisConnectionProvider { IsAvailable = false },
            Options.Create(new AgentConfigSourceOptions
            {
                RedisCacheTtlSeconds = 300,
                RedisCacheReconciliationSeconds = 60
            }),
            NullLogger<AgentConfigDatabaseStore>.Instance,
            repository);
        return new AgentConfigManagementService(store);
    }

    private sealed class RecordingRepository : IAgentConfigRepository
    {
        private AgentConfigEntity? _entity;

        public Task<AgentConfigEntity?> GetAsync(
            string tenantId, string agentId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_entity != null
                && _entity.TenantId == tenantId
                && _entity.AgentId == agentId ? _entity : null);

        public Task<IReadOnlyList<AgentConfigEntity>> ListAsync(
            string? tenantId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AgentConfigEntity>>([]);

        public Task<AgentConfigEntity?> UpsertAsync(
            string tenantId, string agentId, AgentConfigEntity entity, string? expectedVersion,
            CancellationToken cancellationToken = default)
        {
            _entity = entity;
            return Task.FromResult<AgentConfigEntity?>(entity);
        }
    }
}
