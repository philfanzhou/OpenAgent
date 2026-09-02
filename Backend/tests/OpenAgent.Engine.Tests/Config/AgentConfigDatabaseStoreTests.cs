using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Models;
using OpenAgent.Engine.Config;
using StackExchange.Redis;
using Xunit;

namespace OpenAgent.Engine.Tests.Config;

public class AgentConfigDatabaseStoreTests
{
    [Fact]
    public async Task GetRuntimeAsync_CacheMiss_LoadsPostgreSqlAndBackfillsRedisWithTtl()
    {
        var redis = new FakeRedisConnectionProvider();
        var repository = new RecordingRepository(CreateEntity("database"));
        AgentConfigDatabaseStore store = CreateStore(redis, repository);

        AgentConfigEntity? result = await store.GetRuntimeAsync(
            "tenant-a", "support", CancellationToken.None);
        RedisValue cached = redis.StringGet(
            AgentConfigDatabaseStore.BuildCacheKey("tenant-a", "support"));

        Assert.Equal("database", result?.Config.Instructions);
        Assert.False(cached.IsNullOrEmpty);
        Assert.Equal(TimeSpan.FromSeconds(300), redis.LastStringExpiry);
    }

    [Fact]
    public async Task GetRuntimeAsync_SameAgentId_IsTenantScoped()
    {
        var redis = new FakeRedisConnectionProvider();
        var repository = new MultiTenantRepository(
            CreateEntity("tenant-a"),
            CreateEntity("tenant-b", "tenant-b"));
        var provider = new ConfigProvider(CreateStore(redis, repository));

        AgentConfig? tenantA = await provider.GetConfigAsync("support", "tenant-a");
        AgentConfig? tenantB = await provider.GetConfigAsync("support", "tenant-b");

        Assert.Equal("tenant-a", tenantA?.Instructions);
        Assert.Equal("tenant-b", tenantB?.Instructions);
        Assert.NotEqual(
            AgentConfigDatabaseStore.BuildCacheKey("tenant-a", "support"),
            AgentConfigDatabaseStore.BuildCacheKey("tenant-b", "support"));
    }

    [Fact]
    public async Task SaveAsync_CommitsPostgreSqlThenRefreshesRedisImmediately()
    {
        var redis = new FakeRedisConnectionProvider();
        var repository = new RecordingRepository();
        AgentConfigDatabaseStore store = CreateStore(redis, repository);

        AgentConfigEntity? saved = await store.SaveAsync(
            "tenant-a", "support", CreateEntity("updated"), null, CancellationToken.None);
        RedisValue cached = redis.StringGet(
            AgentConfigDatabaseStore.BuildCacheKey("tenant-a", "support"));

        Assert.Equal("1", saved?.CurrentVersion);
        Assert.Equal("updated", repository.Current?.Config.Instructions);
        Assert.Contains("updated", cached.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveAsync_RedisUnavailable_StillCommitsPostgreSql()
    {
        var redis = new FakeRedisConnectionProvider { IsAvailable = false };
        var repository = new RecordingRepository();
        AgentConfigDatabaseStore store = CreateStore(redis, repository);

        AgentConfigEntity? saved = await store.SaveAsync(
            "tenant-a", "support", CreateEntity("committed"), null, CancellationToken.None);

        Assert.NotNull(saved);
        Assert.Equal("committed", repository.Current?.Config.Instructions);
    }

    [Fact]
    public async Task TryWarmupAsync_BackfillsTenantIndex()
    {
        var redis = new FakeRedisConnectionProvider();
        AgentConfigDatabaseStore store = CreateStore(
            redis,
            new RecordingRepository(CreateEntity("warmup")));

        bool completed = await store.TryWarmupAsync(CancellationToken.None);
        RedisValue[] index = await redis.SetMembersAsync(
            AgentConfigDatabaseStore.BuildCacheIndexKey("tenant-a"));

        Assert.True(completed);
        Assert.Equal("support", Assert.Single(index).ToString());
    }

    private static AgentConfigDatabaseStore CreateStore(
        FakeRedisConnectionProvider redis,
        IAgentConfigRepository repository) => new(
            redis,
            Options.Create(new AgentConfigSourceOptions
            {
                RedisCacheTtlSeconds = 300,
                RedisCacheReconciliationSeconds = 60
            }),
            NullLogger<AgentConfigDatabaseStore>.Instance,
            repository);

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
