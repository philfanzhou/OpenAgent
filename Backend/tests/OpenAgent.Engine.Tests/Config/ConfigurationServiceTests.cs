using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Models;
using OpenAgent.Contracts.Security;
using OpenAgent.Contracts.Requests;
using OpenAgent.Engine.Config;
using Xunit;

namespace OpenAgent.Engine.Tests.Config;

public class ConfigurationServiceTests
{
    [Fact]
    public async Task SaveAsync_StampsTenantOwnershipOnNestedBindings()
    {
        var repository = new RecordingRepository();
        ConfigurationService manager = CreateManager(repository);
        AgentConfigEntity entity = new()
        {
            Config = new AgentConfig
            {
                Mcp = new McpConfig { Servers = [new McpServerConfig { Name = "tools" }] },
                Rag = new RagConfig { Instances = [new RagInstanceConfig { Id = "knowledge" }] },
                Skills = new SkillsConfig { Instances = [new SkillInstanceConfig { Id = "search" }] }
            }
        };

        AgentConfigEntity? saved = await manager.SaveAgentAsync(
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
        ConfigurationService manager = CreateManager(repository);
        await manager.SaveAgentAsync("support", "tenant-a", new AgentConfigEntity(), null);

        AgentConfigEntity? foreign = await manager.GetAgentAsync("support", "tenant-b");

        Assert.Null(foreign);
    }

    private static ConfigurationService CreateManager(IAgentConfigRepository repository) => new(
        repository,
        new Moq.Mock<ILlmConfigRepository>().Object,
        new FakeRedisConnectionProvider { IsAvailable = false },
        Options.Create(new AgentConfigSourceOptions()),
        new ConfigurationSecretResolver(new ConfigurationBuilder().Build()),
        NullLogger<ConfigurationService>.Instance);

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
