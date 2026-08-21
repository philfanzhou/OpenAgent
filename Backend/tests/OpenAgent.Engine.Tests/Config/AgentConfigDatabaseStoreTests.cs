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

        AgentConfig? result = await provider.GetConfigAsync("support");
        RedisValue cached = redis.StringGet($"{AgentConfigDatabaseStore.CacheKeyPrefix}support");

        Assert.Equal("database", result?.Instructions);
        Assert.False(cached.IsNullOrEmpty);
        Assert.Contains("database", cached.ToString(), StringComparison.Ordinal);
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
            CreateEntity("committed"),
            expectedVersion: null);
        bool found = snapshot.TryGetConfig(
            "support",
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
            $"{AgentConfigDatabaseStore.CacheKeyPrefix}support",
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
            """{"resourceType":"PostgreSqlAgent","resourceId":"support","operation":"Upsert","version":"7"}""");
        bool found = snapshot.TryGetConfig(
            "support",
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
        RedisValue cached = redis.StringGet($"{AgentConfigDatabaseStore.CacheKeyPrefix}support");
        RedisValue[] index = await redis.SetMembersAsync(AgentConfigDatabaseStore.CacheIndexKey);

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
                UsePostgreSqlForAgents = true,
                RedisCacheTtlSeconds = 300
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
            new SecretInjector(),
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

    private static AgentConfigEntity CreateEntity(string instructions) => new()
    {
        AgentId = "support",
        TenantId = "tenant-a",
        Config = new AgentConfig
        {
            TenantId = "tenant-a",
            Instructions = instructions
        }
    };

    private sealed class RecordingRepository(AgentConfigEntity? current = null) : IAgentConfigRepository
    {
        internal AgentConfigEntity? Current { get; private set; } = current;

        public Task<AgentConfigEntity?> GetAsync(
            string agentId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Current);

        public Task<IReadOnlyList<AgentConfigEntity>> ListAsync(
            string? tenantId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AgentConfigEntity>>(
                Current == null ? [] : [Current]);

        public Task<AgentConfigEntity?> UpsertAsync(
            string agentId,
            AgentConfigEntity entity,
            string? expectedVersion,
            CancellationToken cancellationToken = default)
        {
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
