using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Models;
using OpenAgent.Engine.Config;
using StackExchange.Redis;
using Xunit;

namespace OpenAgent.Engine.Tests.Config;

public class ConfigurationCacheTests
{
    [Fact]
    public async Task GetConfigAsync_CacheMiss_LoadsPostgreSqlAndBackfillsRedisWithTtl()
    {
        var redis = new FakeRedisConnectionProvider();
        var repository = new RecordingRepository(CreateEntity("database"));
        ConfigurationService store = CreateStore(redis, repository);

        AgentConfig? result = await store.GetConfigAsync(
            "support", "tenant-a", CancellationToken.None);
        RedisValue cached = redis.StringGet(
            ConfigurationService.BuildCacheKey("agent", "tenant-a", "support"));

        Assert.Equal("database", result?.Instructions);
        Assert.False(cached.IsNullOrEmpty);
        Assert.Equal(TimeSpan.FromSeconds(300), redis.LastStringExpiry);
    }

    [Fact]
    public async Task GetConfigAsync_SameAgentId_IsTenantScoped()
    {
        var redis = new FakeRedisConnectionProvider();
        var repository = new MultiTenantRepository(
            CreateEntity("tenant-a"),
            CreateEntity("tenant-b", "tenant-b"));
        ConfigurationService provider = CreateStore(redis, repository);

        AgentConfig? tenantA = await provider.GetConfigAsync("support", "tenant-a");
        AgentConfig? tenantB = await provider.GetConfigAsync("support", "tenant-b");

        Assert.Equal("tenant-a", tenantA?.Instructions);
        Assert.Equal("tenant-b", tenantB?.Instructions);
        Assert.NotEqual(
            ConfigurationService.BuildCacheKey("agent", "tenant-a", "support"),
            ConfigurationService.BuildCacheKey("agent", "tenant-b", "support"));
    }

    [Fact]
    public async Task SaveAsync_CommitsPostgreSqlThenRefreshesRedisImmediately()
    {
        var redis = new FakeRedisConnectionProvider();
        var repository = new RecordingRepository();
        ConfigurationService store = CreateStore(redis, repository);

        AgentConfigEntity? saved = await store.SaveAgentAsync(
            "support", "tenant-a", CreateEntity("updated"), null, CancellationToken.None);
        RedisValue cached = redis.StringGet(
            ConfigurationService.BuildCacheKey("agent", "tenant-a", "support"));

        Assert.Equal("1", saved?.CurrentVersion);
        Assert.Equal("updated", repository.Current?.Config.Instructions);
        Assert.Contains("updated", cached.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveAsync_RedisUnavailable_StillCommitsPostgreSql()
    {
        var redis = new FakeRedisConnectionProvider { IsAvailable = false };
        var repository = new RecordingRepository();
        ConfigurationService store = CreateStore(redis, repository);

        AgentConfigEntity? saved = await store.SaveAgentAsync(
            "support", "tenant-a", CreateEntity("committed"), null, CancellationToken.None);

        Assert.NotNull(saved);
        Assert.Equal("committed", repository.Current?.Config.Instructions);
    }

    private static ConfigurationService CreateStore(
        FakeRedisConnectionProvider redis,
        IAgentConfigRepository repository) => new(
            repository,
            new Moq.Mock<ILlmConfigRepository>().Object,
            redis,
            Options.Create(new AgentConfigSourceOptions()),
            NullLogger<ConfigurationService>.Instance);


    private static AgentConfigEntity CreateEntity(
        string instructions,
        string tenantId = "tenant-a") => new()
    {
        AgentId = "support",
        TenantId = tenantId,
        Config = new AgentConfig { TenantId = tenantId, Instructions = instructions }
    };

    private sealed class MultiTenantRepository(params AgentConfigEntity[] entities)
        : IAgentConfigRepository
    {
        private readonly Dictionary<(string, string), AgentConfigEntity> _entities =
            entities.ToDictionary(entity => (entity.TenantId, entity.AgentId));

        public Task<AgentConfigEntity?> GetAsync(
            string tenantId, string agentId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_entities.GetValueOrDefault((tenantId, agentId)));

        public Task<IReadOnlyList<AgentConfigEntity>> ListAsync(
            string? tenantId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AgentConfigEntity>>(_entities.Values
                .Where(entity => tenantId == null || entity.TenantId == tenantId).ToArray());

        public Task<AgentConfigEntity?> UpsertAsync(
            string tenantId, string agentId, AgentConfigEntity entity, string? expectedVersion,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class RecordingRepository(AgentConfigEntity? current = null) : IAgentConfigRepository
    {
        internal AgentConfigEntity? Current { get; private set; } = current;

        public Task<AgentConfigEntity?> GetAsync(
            string tenantId, string agentId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Current != null
                && Current.TenantId == tenantId
                && Current.AgentId == agentId ? Current : null);

        public Task<IReadOnlyList<AgentConfigEntity>> ListAsync(
            string? tenantId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AgentConfigEntity>>(
                Current == null || tenantId != null && Current.TenantId != tenantId ? [] : [Current]);

        public Task<AgentConfigEntity?> UpsertAsync(
            string tenantId, string agentId, AgentConfigEntity entity, string? expectedVersion,
            CancellationToken cancellationToken = default)
        {
            entity.TenantId = tenantId;
            entity.AgentId = agentId;
            entity.Config.TenantId = tenantId;
            entity.CurrentVersion = Current == null ? "1" : "2";
            Current = entity;
            return Task.FromResult<AgentConfigEntity?>(entity);
        }
    }
}
