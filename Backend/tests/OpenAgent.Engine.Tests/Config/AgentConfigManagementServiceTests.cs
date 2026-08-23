using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Models;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Abstract;
using OpenAgent.Engine.Abstractions;
using OpenAgent.Engine.Config;
using OpenAgent.Engine.Models;
using OpenAgent.Engine.Reload;
using StackExchange.Redis;
using Xunit;

namespace OpenAgent.Engine.Tests.Config;

public class AgentConfigManagementServiceTests
{
    [Fact]
    public async Task SaveAsync_WithoutRedis_WritesLocalStore()
    {
        (AgentConfigManagementService manager, AgentConfigLocalStore localStore) = CreateManager();

        AgentConfigEntity? saved = await manager.SaveAsync(
            "support",
            new AgentConfigEntity
            {
                AgentId = "support",
                Config = new AgentConfig { Instructions = "new" }
            },
            expectedVersion: null);

        Assert.NotNull(saved);
        Assert.Equal("new", localStore.Get("support")?.Config.Instructions);
    }

    [Fact]
    public async Task ScopedAccess_DifferentTenant_CannotReadOrOverwriteAgentConfig()
    {
        (AgentConfigManagementService manager, AgentConfigLocalStore localStore) = CreateManager();
        AgentConfigEntity? saved = await manager.SaveAsync(
            "support",
            "tenant-a",
            new AgentConfigEntity
            {
                AgentId = "support",
                Config = new AgentConfig { Instructions = "tenant-a" }
            },
            expectedVersion: null);

        AgentConfigEntity? read = await manager.GetAsync("support", "tenant-b");
        AgentConfigEntity? overwritten = await manager.SaveAsync(
            "support",
            "tenant-b",
            new AgentConfigEntity
            {
                AgentId = "support",
                Config = new AgentConfig { Instructions = "tenant-b" }
            },
            expectedVersion: null);

        Assert.NotNull(saved);
        Assert.Null(read);
        Assert.Null(overwritten);
        Assert.Equal("tenant-a", localStore.Get("support")?.TenantId);
        Assert.Equal("tenant-a", localStore.Get("support")?.Config.Instructions);
    }

    [Fact]
    public async Task ProfileAccess_DifferentTenant_CannotReadOverwriteOrDeleteProfiles()
    {
        var redis = new UnavailableRedisConnectionProvider();
        LlmProviderProfile? llmProfile = null;
        var llmRegistry = new Mock<ILlmRegistry>();
        llmRegistry.Setup(item => item.GetProfile(It.IsAny<string>()))
            .Returns(() => llmProfile);
        llmRegistry.Setup(item => item.Register(It.IsAny<LlmProviderProfile>()))
            .Callback<LlmProviderProfile>(profile => llmProfile = profile);
        llmRegistry.Setup(item => item.Remove(It.IsAny<string>()))
            .Returns<string>(id =>
            {
                bool removed = string.Equals(llmProfile?.Id, id, StringComparison.Ordinal);
                if (removed)
                {
                    llmProfile = null;
                }
                return removed;
            });
        McpServerConfig? mcpProfile = null;
        var mcpRegistry = new Mock<IMcpRegistry>();
        mcpRegistry.Setup(item => item.Get(It.IsAny<string>()))
            .Returns(() => mcpProfile);
        mcpRegistry.Setup(item => item.Register(It.IsAny<McpServerConfig>()))
            .Callback<McpServerConfig>(profile => mcpProfile = profile);
        var llm = new LlmProfileManagementService(
            redis,
            llmRegistry.Object,
            configUpdates: null!);
        var mcp = new McpProfileManagementService(redis, mcpRegistry.Object);
        await llm.SaveAsync(
            new LlmProviderProfile { Id = "private-llm", Name = "Private" },
            "tenant-a");
        await mcp.SaveAsync(
            new McpServerConfig { Name = "private-mcp", Url = "https://mcp.example.com" },
            "tenant-a");

        LlmProviderProfile? llmRead = await llm.GetAsync("private-llm", "tenant-b");
        McpServerConfig? mcpRead = await mcp.GetAsync("private-mcp", "tenant-b");

        Assert.Null(llmRead);
        Assert.Null(mcpRead);
        await Assert.ThrowsAsync<TenantDataIsolationException>(() => llm.SaveAsync(
            new LlmProviderProfile { Id = "private-llm", Name = "Other" },
            "tenant-b"));
        await Assert.ThrowsAsync<TenantDataIsolationException>(() => mcp.SaveAsync(
            new McpServerConfig { Name = "private-mcp", Url = "https://other.example.com" },
            "tenant-b"));
        Assert.False(await llm.DeleteAsync("private-llm", "tenant-b"));
        Assert.NotNull(llmProfile);
        Assert.True(await llm.DeleteAsync("private-llm", "tenant-a"));
        Assert.Null(llmProfile);
    }

    private static (AgentConfigManagementService Manager, AgentConfigLocalStore LocalStore) CreateManager()
    {
        var environment = new Mock<IHostEnvironment>();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Engine:AllowMockAgent"] = "true"
            })
            .Build();
        var snapshot = new ConfigSnapshot(
            Options.Create(new ConfigSnapshotOptions()),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<ConfigSnapshot>.Instance);
        var localStore = new AgentConfigLocalStore();
        var redis = new UnavailableRedisConnectionProvider();
        var manager = new AgentConfigManagementService(
            redis,
            new MockAgentResolver(environment.Object, configuration),
            localStore,
            CreateDispatcher(redis, snapshot));
        return (manager, localStore);
    }

    private static ConfigUpdateDispatcher CreateDispatcher(
        IRedisConnectionProvider redis,
        ConfigSnapshot snapshot)
    {
        var llmRegistry = new Mock<ILlmRegistry>();
        var fullConfig = new FullConfigRefresher(
            redis,
            snapshot,
            NullLogger<FullConfigRefresher>.Instance);
        var llmProfiles = new LlmProfileRefresher(
            redis,
            llmRegistry.Object,
            NullLogger<LlmProfileRefresher>.Instance);
        return new ConfigUpdateDispatcher(
            fullConfig,
            llmProfiles,
            new LegacyMessageHandler(
                fullConfig,
                llmProfiles,
                NullLogger<LegacyMessageHandler>.Instance),
            snapshot,
            NullLogger<ConfigUpdateDispatcher>.Instance);
    }

    private sealed class UnavailableRedisConnectionProvider : IRedisConnectionProvider
    {
        public bool IsAvailable => false;

        public IServer? GetServer(int database = 0) => null;
        public IDatabase GetDatabase(int database = 0) => throw new NotSupportedException();
        public Task<RedisValue> StringGetAsync(RedisKey key, CommandFlags flags = CommandFlags.None) =>
            Task.FromResult(RedisValue.Null);
        public Task<bool> StringSetAsync(RedisKey key, RedisValue value, TimeSpan? expiry = null, CommandFlags flags = CommandFlags.None) =>
            throw new NotSupportedException();
        public Task<bool> KeyDeleteAsync(RedisKey key, CommandFlags flags = CommandFlags.None) =>
            throw new NotSupportedException();
        public Task<RedisValue[]> SetMembersAsync(RedisKey key, CommandFlags flags = CommandFlags.None) =>
            Task.FromResult(Array.Empty<RedisValue>());
        public Task<bool> SetAddAsync(RedisKey key, RedisValue value, CommandFlags flags = CommandFlags.None) =>
            throw new NotSupportedException();
        public Task<bool> SetRemoveAsync(RedisKey key, RedisValue value, CommandFlags flags = CommandFlags.None) =>
            throw new NotSupportedException();
        public Task<TimeSpan> PingAsync(CommandFlags flags = CommandFlags.None) =>
            throw new NotSupportedException();
        public RedisValue StringGet(RedisKey key, CommandFlags flags = CommandFlags.None) => RedisValue.Null;
        public void Subscribe(RedisChannel channel, Action<RedisChannel, RedisValue> handler)
        {
        }
        public void Dispose()
        {
        }
    }
}
