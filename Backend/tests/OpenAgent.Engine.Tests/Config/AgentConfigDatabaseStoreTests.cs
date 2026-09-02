using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Models;
using OpenAgent.Core.Abstract;
using OpenAgent.Engine.Config;
using OpenAgent.Engine.Models;
using OpenAgent.Engine.Reload;
using StackExchange.Redis;
using Xunit;

namespace OpenAgent.Engine.Tests.Config;

public class AgentConfigDatabaseStoreTests
{
    [Fact]
    public async Task GetConfigAsync_PostgreSqlCacheMiss_LoadsDatabaseAndBackfillsRedis()
    {
        var redis = new FakeRedisConnectionProvider();
        var repository = new RecordingRepository(CreateEntity("database"));
        ConfigSnapshot snapshot = CreateSnapshot();
        AgentConfigDatabaseStore store = CreateStore(redis, repository, snapshot);
        ConfigProvider provider = CreateProvider(redis, snapshot, store);

        AgentConfig? result = await provider.GetConfigAsync("support", "tenant-a");
        RedisValue cached = redis.StringGet(
            AgentConfigDatabaseStore.BuildCacheKey("tenant-a", "support"));

        Assert.Equal("database", result?.Instructions);
        Assert.False(cached.IsNullOrEmpty);
        Assert.Contains("database", cached.ToString(), StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromSeconds(300), redis.LastStringExpiry);
    }

    [Fact]
    public async Task GetConfigAsync_SameAgentId_UsesTenantScopedCacheEntries()
    {
        var redis = new FakeRedisConnectionProvider();
        var repository = new MultiTenantRepository(
            CreateEntity("tenant-a"),
            CreateEntity("tenant-b", "tenant-b"));
        ConfigSnapshot snapshot = CreateSnapshot();
        ConfigProvider provider = CreateProvider(
            redis,
            snapshot,
            CreateStore(redis, repository, snapshot));

        AgentConfig? tenantA = await provider.GetConfigAsync("support", "tenant-a");
        AgentConfig? tenantB = await provider.GetConfigAsync("support", "tenant-b");

        Assert.Equal("tenant-a", tenantA?.Instructions);
        Assert.Equal("tenant-b", tenantB?.Instructions);
        Assert.False(redis.StringGet(
            AgentConfigDatabaseStore.BuildCacheKey("tenant-a", "support")).IsNullOrEmpty);
        Assert.False(redis.StringGet(
            AgentConfigDatabaseStore.BuildCacheKey("tenant-b", "support")).IsNullOrEmpty);
        Assert.NotEqual(
            AgentConfigDatabaseStore.BuildSnapshotScope("tenant-a", "support"),
            AgentConfigDatabaseStore.BuildSnapshotScope("tenant-b", "support"));
    }

    [Fact]
    public async Task SaveAsync_RedisUnavailable_CommitsDatabaseAndRefreshesSnapshot()
    {
        var redis = new FakeRedisConnectionProvider { IsAvailable = false };
        var repository = new RecordingRepository();
        ConfigSnapshot snapshot = CreateSnapshot();
        AgentConfigDatabaseStore store = CreateStore(redis, repository, snapshot);
        AgentConfigManagementService manager = CreateManager(redis, store);

        AgentConfigEntity? saved = await manager.SaveAsync(
            "support",
            "tenant-a",
            CreateEntity("committed"),
            expectedVersion: null);
        bool found = snapshot.TryGetConfig(
            AgentConfigDatabaseStore.BuildSnapshotScope("tenant-a", "support"),
            "FullAgentConfig",
            out AgentConfig? cached);

        Assert.NotNull(saved);
        Assert.Equal("1", saved.CurrentVersion);
        Assert.Equal("committed", repository.Current?.Config.Instructions);
        Assert.True(found);
        Assert.Equal("committed", cached?.Instructions);
    }

    [Fact]
    public void Process_PostgreSqlAgentNotification_UpdatesSnapshot()
    {
        var redis = new FakeRedisConnectionProvider();
        var repository = new RecordingRepository();
        ConfigSnapshot snapshot = CreateSnapshot();
        AgentConfigDatabaseStore store = CreateStore(redis, repository, snapshot);
        AgentConfigEntity entity = CreateEntity("reloaded");
        entity.CurrentVersion = "7";
        redis.SetString(
            AgentConfigDatabaseStore.BuildCacheKey("tenant-a", "support"),
            JsonSerializer.Serialize(entity));

        var llmRegistry = new Mock<ILlmRegistry>();
        var fullConfig = new FullConfigRefresher(
            redis,
            snapshot,
            NullLogger<FullConfigRefresher>.Instance,
            store);
        var llmProfiles = new LlmProfileRefresher(
            redis,
            llmRegistry.Object,
            NullLogger<LlmProfileRefresher>.Instance);
        var dispatcher = new ConfigUpdateDispatcher(
            fullConfig,
            llmProfiles,
            new LegacyMessageHandler(
                fullConfig,
                llmProfiles,
                NullLogger<LegacyMessageHandler>.Instance),
            snapshot,
            NullLogger<ConfigUpdateDispatcher>.Instance);

        bool refreshed = dispatcher.Process(
            HotReloadService.CurrentUpdatesChannel,
            """{"resourceType":"PostgreSqlAgent","tenantId":"tenant-a","resourceId":"support","operation":"Upsert","version":"7"}""");
        bool found = snapshot.TryGetConfig(
            AgentConfigDatabaseStore.BuildSnapshotScope("tenant-a", "support"),
            "FullAgentConfig",
            out AgentConfig? cached);

        Assert.True(refreshed);
        Assert.True(found);
        Assert.Equal("reloaded", cached?.Instructions);
    }

    [Fact]
    public async Task TryWarmupAsync_PostgreSqlEnabled_BackfillsRedisCache()
    {
        var redis = new FakeRedisConnectionProvider();
        var repository = new RecordingRepository(CreateEntity("warmup"));
        AgentConfigDatabaseStore store = CreateStore(redis, repository, CreateSnapshot());

        bool completed = await store.TryWarmupAsync(CancellationToken.None);
        RedisValue cached = redis.StringGet(
            AgentConfigDatabaseStore.BuildCacheKey("tenant-a", "support"));
        RedisValue[] index = await redis.SetMembersAsync(
            AgentConfigDatabaseStore.BuildCacheIndexKey("tenant-a"));

        Assert.True(completed);
        Assert.False(cached.IsNullOrEmpty);
        Assert.Equal("support", Assert.Single(index).ToString());
    }

    private static AgentConfigDatabaseStore CreateStore(
        FakeRedisConnectionProvider redis,
        IAgentConfigRepository repository,
        ConfigSnapshot snapshot) => new(
            redis,
            snapshot,
            Options.Create(new AgentConfigSourceOptions
            {
                RedisCacheTtlSeconds = 300,
                RedisCacheReconciliationSeconds = 60
            }),
            NullLogger<AgentConfigDatabaseStore>.Instance,
            repository);

    private static ConfigProvider CreateProvider(
        FakeRedisConnectionProvider redis,
        ConfigSnapshot snapshot,
        AgentConfigDatabaseStore store)
    {
        MockAgentResolver mock = CreateMockResolver();
        AgentConfigLocalStore local = new();
        AgentListQuery list = new(
            redis,
            NullLogger<AgentListQuery>.Instance,
            local,
            store);
        return new ConfigProvider(
            redis,
            NullLogger<ConfigProvider>.Instance,
            snapshot,
            mock,
            list,
            local,
            store);
    }

    private static AgentConfigManagementService CreateManager(
        FakeRedisConnectionProvider redis,
        AgentConfigDatabaseStore store) => new(
            redis,
            CreateMockResolver(),
            new AgentConfigLocalStore(),
            configUpdates: null!,
            store);

    private static MockAgentResolver CreateMockResolver()
    {
        var environment = new Mock<IHostEnvironment>();
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Engine:AllowMockAgent"] = "false"
            })
            .Build();
        return new MockAgentResolver(environment.Object, configuration);
    }

    private static ConfigSnapshot CreateSnapshot() => new(
        Options.Create(new ConfigSnapshotOptions()),
        new MemoryCache(new MemoryCacheOptions()),
        NullLogger<ConfigSnapshot>.Instance);

    private static AgentConfigEntity CreateEntity(
        string instructions,
        string tenantId = "tenant-a") => new()
    {
        AgentId = "support",
        TenantId = tenantId,
        Config = new AgentConfig
        {
            TenantId = tenantId,
            Instructions = instructions
        }
    };

    private sealed class MultiTenantRepository(params AgentConfigEntity[] entities)
        : IAgentConfigRepository
    {
        private readonly Dictionary<(string TenantId, string AgentId), AgentConfigEntity> _entities =
            entities.ToDictionary(entity => (entity.TenantId, entity.AgentId));

        public Task<AgentConfigEntity?> GetAsync(
            string tenantId,
            string agentId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_entities.GetValueOrDefault((tenantId, agentId)));

        public Task<IReadOnlyList<AgentConfigEntity>> ListAsync(
            string? tenantId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AgentConfigEntity>>(_entities.Values
                .Where(entity => tenantId == null || entity.TenantId == tenantId)
                .ToArray());

        public Task<AgentConfigEntity?> UpsertAsync(
            string tenantId,
            string agentId,
            AgentConfigEntity entity,
            string? expectedVersion,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingRepository(AgentConfigEntity? current = null) : IAgentConfigRepository
    {
        internal AgentConfigEntity? Current { get; private set; } = current;

        public Task<AgentConfigEntity?> GetAsync(
            string tenantId,
            string agentId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Current);

        public Task<IReadOnlyList<AgentConfigEntity>> ListAsync(
            string? tenantId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AgentConfigEntity>>(
                Current == null ? [] : [Current]);

        public Task<AgentConfigEntity?> UpsertAsync(
            string tenantId,
            string agentId,
            AgentConfigEntity entity,
            string? expectedVersion,
            CancellationToken cancellationToken = default)
        {
            entity.TenantId = tenantId;
            entity.Config.TenantId = tenantId;
            entity.AgentId = agentId;
            entity.CurrentVersion = Current == null
                ? "1"
                : (long.Parse(Current.CurrentVersion, System.Globalization.CultureInfo.InvariantCulture) + 1)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture);
            Current = entity;
            return Task.FromResult<AgentConfigEntity?>(entity);
        }
    }
}
