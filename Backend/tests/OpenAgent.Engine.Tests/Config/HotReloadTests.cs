using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Models;
using OpenAgent.Core.Abstract;
using OpenAgent.Engine.Config;
using OpenAgent.Engine.Models;
using OpenAgent.Engine.Reload;
using Xunit;

namespace OpenAgent.Engine.Tests.Config;

public class HotReloadTests
{
    [Fact]
    public void ProcessMessage_LegacyRefresh_AcceptsStringEnumValues()
    {
        var snapshot = CreateSnapshot();
        var redis = new FakeRedisConnectionProvider();
        redis.SetString(
            "agent:config:agent-string-enum",
            """
            {
              "agentId": "agent-string-enum",
              "status": "Snapshot",
              "currentVersion": "3",
              "config": {
                "framework": "Mock",
                "llm": {
                  "provider": "provider-string-enum",
                  "modelId": "model-string-enum"
                }
              }
            }
            """);
        var service = CreateService(redis, snapshot);

        service.ProcessMessage("agent:config:changed", "agent-string-enum");

        var config = snapshot.GetConfig<AgentConfig>("agent-string-enum", "FullAgentConfig");
        Assert.NotNull(config);
        Assert.Equal("provider-string-enum", config.Llm.Provider);
    }

    [Fact]
    public void ProcessMessage_RefreshesSnapshotFromLegacyAgentChannel()
    {
        var snapshot = CreateSnapshot();
        var redis = new FakeRedisConnectionProvider();
        SeedRedisConfig(redis, "agent-a", "provider-a", "model-a");

        var service = CreateService(redis, snapshot);
        service.ProcessMessage("agent:config:changed", "agent-a");

        var fullConfig = snapshot.GetConfig<AgentConfig>("agent-a", "FullAgentConfig");
        Assert.NotNull(fullConfig);
        Assert.Equal("provider-a", fullConfig!.Llm.Provider);
    }

    [Fact]
    public void ProcessMessage_TypedUpdate_RefreshesFullConfigFromRedis()
    {
        var snapshot = CreateSnapshot();
        var redis = new FakeRedisConnectionProvider();
        SeedRedisConfig(redis, "agent-b", "provider-b", "model-b");

        var service = CreateService(redis, snapshot);
        service.ProcessMessage(
            HotReloadService.CurrentUpdatesChannel,
            """
            {
              "agentId": "agent-b",
              "type": "IncrementalUpdate",
              "configType": "LLMSettings",
              "version": 9,
              "timestamp": "2026-01-01T00:00:00Z",
              "data": {
                "provider": "ignored-provider",
                "modelId": "ignored-model"
              }
            }
            """);

        var llmConfig = snapshot.GetConfig<LlmConfig>("agent-b", "LLMSettings");
        Assert.NotNull(llmConfig);
        Assert.Equal("provider-b", llmConfig!.Provider);
        Assert.Equal("model-b", llmConfig.ModelId);
    }

    [Fact]
    public void ProcessMessage_TypedUpdateWithLowerVersion_StillRefreshesFromRedis()
    {
        var snapshot = CreateSnapshot();
        snapshot.SetFullConfig("agent-c", new AgentConfig
        {
            Llm = new LlmConfig { Provider = "stale-provider", ModelId = "stale-model" }
        });
        var redis = new FakeRedisConnectionProvider();
        SeedRedisConfig(redis, "agent-c", "current-provider", "current-model");

        var service = CreateService(redis, snapshot);
        service.ProcessMessage(
            HotReloadService.CurrentUpdatesChannel,
            """
            {
              "agentId": "agent-c",
              "type": "IncrementalUpdate",
              "configType": "LLMSettings",
              "version": 1,
              "data": { "provider": "stale-provider", "modelId": "stale-model" }
            }
            """);

        var llmConfig = snapshot.GetConfig<LlmConfig>("agent-c", "LLMSettings");
        Assert.NotNull(llmConfig);
        Assert.Equal("current-provider", llmConfig!.Provider);
        Assert.Equal("current-model", llmConfig.ModelId);
    }

    [Fact]
    public void ProcessMessage_ConfigUpdate_RefreshesFullConfigFromRedis()
    {
        var snapshot = CreateSnapshot();
        var redis = new FakeRedisConnectionProvider();
        SeedRedisConfig(redis, "agent-d", "provider-d", "model-d");

        var service = CreateService(redis, snapshot);
        service.ProcessMessage(
            HotReloadService.CurrentUpdatesChannel,
            """
            {
              "agentId": "agent-d",
              "type": "ConfigUpdate",
              "configType": "FullAgentConfig",
              "version": 12
            }
            """);

        var fullConfig = snapshot.GetConfig<AgentConfig>("agent-d", "FullAgentConfig");
        Assert.NotNull(fullConfig);
        Assert.Equal("provider-d", fullConfig!.Llm.Provider);
        Assert.Equal("model-d", fullConfig.Llm.ModelId);
    }

    [Fact]
    public void ProcessMessage_UnknownConfigType_RefreshesFullConfigFromRedis()
    {
        var snapshot = CreateSnapshot();
        var redis = new FakeRedisConnectionProvider();
        SeedRedisConfig(redis, "agent-unknown-type", "provider-known", "model-known");

        var service = CreateService(redis, snapshot);
        service.ProcessMessage(
            HotReloadService.CurrentUpdatesChannel,
            """
            {
              "agentId": "agent-unknown-type",
              "type": "IncrementalUpdate",
              "configType": "UnsupportedSettings",
              "version": 3,
              "data": { "enabled": true }
            }
            """);

        var fullConfig = snapshot.GetConfig<AgentConfig>("agent-unknown-type", "FullAgentConfig");
        Assert.NotNull(fullConfig);
        Assert.Equal("provider-known", fullConfig!.Llm.Provider);
    }

    [Fact]
    public void ProcessMessage_FullSync_ClearsSnapshot()
    {
        var snapshot = CreateSnapshot();
        snapshot.SetFullConfig("agent-full-sync", new AgentConfig
        {
            Llm = new LlmConfig { Provider = "stable-provider", ModelId = "stable-model" }
        });
        var service = CreateService(new FakeRedisConnectionProvider(), snapshot);

        service.ProcessMessage(
            HotReloadService.CurrentUpdatesChannel,
            """
            {
              "agentId": "agent-full-sync",
              "type": "FullSync",
              "version": 9
            }
            """);

        Assert.False(snapshot.TryGetConfig<AgentConfig>("agent-full-sync", "FullAgentConfig", out _));
        Assert.False(snapshot.TryGetConfig<LlmConfig>("agent-full-sync", "LLMSettings", out _));
    }

    [Fact]
    public void ProcessMessage_FullSyncWithoutAgentId_ClearsSnapshot()
    {
        var snapshot = CreateSnapshot();
        snapshot.SetFullConfig("agent-full-sync-broadcast", new AgentConfig
        {
            Llm = new LlmConfig { Provider = "stable-provider", ModelId = "stable-model" }
        });
        var service = CreateService(new FakeRedisConnectionProvider(), snapshot);

        service.ProcessMessage(
            HotReloadService.CurrentUpdatesChannel,
            """
            {
              "type": "FullSync"
            }
            """);

        Assert.False(snapshot.TryGetConfig<AgentConfig>("agent-full-sync-broadcast", "FullAgentConfig", out _));
        Assert.False(snapshot.TryGetConfig<LlmConfig>("agent-full-sync-broadcast", "LLMSettings", out _));
    }

    [Fact]
    public void ProcessMessage_TypedUpdateWithoutRedisConfig_EvictsSnapshot()
    {
        var snapshot = CreateSnapshot();
        snapshot.SetFullConfig("agent-deleted", new AgentConfig
        {
            Llm = new LlmConfig { Provider = "deleted-provider", ModelId = "deleted-model" }
        });
        var service = CreateService(new FakeRedisConnectionProvider(), snapshot);

        service.ProcessMessage(
            HotReloadService.CurrentUpdatesChannel,
            """
            {
              "agentId": "agent-deleted",
              "type": "ConfigUpdate",
              "configType": "FullAgentConfig",
              "version": 2
            }
            """);

        Assert.False(snapshot.TryGetConfig<AgentConfig>("agent-deleted", "FullAgentConfig", out _));
        Assert.False(snapshot.TryGetConfig<LlmConfig>("agent-deleted", "LLMSettings", out _));
    }

    [Fact]
    public void ProcessMessage_IgnoresBlankPayload()
    {
        var snapshot = CreateSnapshot();
        var redis = new FakeRedisConnectionProvider();
        var service = CreateService(redis, snapshot);

        service.ProcessMessage(HotReloadService.CurrentUpdatesChannel, "   ");

        Assert.Null(snapshot.GetConfig<AgentConfig>("agent-empty", "FullAgentConfig"));
    }

    [Fact]
    public void ProcessMessage_InvalidJson_DoesNotOverwriteExistingSnapshot()
    {
        var snapshot = CreateSnapshot();
        snapshot.SetConfig("agent-e", "LLMSettings", new LlmConfig { Provider = "stable-provider", ModelId = "stable-model" });

        var redis = new FakeRedisConnectionProvider();
        var service = CreateService(redis, snapshot);

        service.ProcessMessage(HotReloadService.CurrentUpdatesChannel, "{ invalid json");

        var llmConfig = snapshot.GetConfig<LlmConfig>("agent-e", "LLMSettings");
        Assert.NotNull(llmConfig);
        Assert.Equal("stable-provider", llmConfig!.Provider);
        Assert.Equal("stable-model", llmConfig.ModelId);
    }

    [Fact]
    public void ProcessMessage_LegacyRegistryChannel_DoesNotMutateSnapshot()
    {
        var snapshot = CreateSnapshot();
        snapshot.SetConfig("agent-f", "LLMSettings", new LlmConfig { Provider = "existing-provider", ModelId = "existing-model" });

        var redis = new FakeRedisConnectionProvider();
        var service = CreateService(redis, snapshot);

        service.ProcessMessage("skill:registry:changed", "agent-f");

        var llmConfig = snapshot.GetConfig<LlmConfig>("agent-f", "LLMSettings");
        Assert.NotNull(llmConfig);
        Assert.Equal("existing-provider", llmConfig!.Provider);
        Assert.Equal("existing-model", llmConfig.ModelId);
    }

    [Fact]
    public void ProcessMessage_LlmUpdate_ReloadsProfileFromRedis()
    {
        var redis = new FakeRedisConnectionProvider();
        var registry = new TestLlmRegistry();
        registry.Register(new LlmProviderProfile
        {
            Id = "provider-a",
            Endpoint = "https://old.example.com"
        });
        redis.SetString(
            "llm:registry:provider-a",
            JsonSerializer.Serialize(new LlmProviderProfile
            {
                Id = "provider-a",
                Endpoint = "https://new.example.com",
                ApiKey = "new-key"
            }));
        var service = CreateService(redis, CreateSnapshot(), registry);

        service.ProcessMessage(
            HotReloadService.CurrentUpdatesChannel,
            """
            {
              "resourceType": "Llm",
              "resourceId": "provider-a",
              "operation": "Upsert",
              "version": "1723456789012"
            }
            """);

        LlmProviderProfile? profile = registry.GetProfile("provider-a");
        Assert.NotNull(profile);
        Assert.Equal("https://new.example.com", profile!.Endpoint);
        Assert.Equal("new-key", profile.ApiKey);
    }

    [Fact]
    public void ProcessMessage_LlmDelete_RemovesProfileWhenRedisKeyIsMissing()
    {
        var registry = new TestLlmRegistry();
        registry.Register(new LlmProviderProfile { Id = "provider-deleted" });
        var service = CreateService(
            new FakeRedisConnectionProvider(),
            CreateSnapshot(),
            registry);

        service.ProcessMessage(
            HotReloadService.CurrentUpdatesChannel,
            """
            {
              "resourceType": "Llm",
              "resourceId": "provider-deleted",
              "operation": "Delete"
            }
            """);

        Assert.Null(registry.GetProfile("provider-deleted"));
    }

    [Fact]
    public void ProcessMessage_LegacyLlmChannel_ReloadsProfileFromRedis()
    {
        var redis = new FakeRedisConnectionProvider();
        var registry = new TestLlmRegistry();
        redis.SetString(
            "llm:registry:legacy-provider",
            JsonSerializer.Serialize(new LlmProviderProfile
            {
                Id = "legacy-provider",
                ModelId = "current-model"
            }));
        var service = CreateService(redis, CreateSnapshot(), registry);

        service.ProcessMessage("llm:registry:changed", "legacy-provider");

        Assert.Equal("current-model", registry.GetProfile("legacy-provider")?.ModelId);
    }

    [Fact]
    public async Task SetConfig_AfterAbsoluteExpiration_ExpiresEntry()
    {
        var snapshot = CreateSnapshot(ttlMinutes: 0.01);
        snapshot.SetConfig("agent-ttl", "LLMSettings", new LlmConfig { Provider = "ttl-provider", ModelId = "ttl-model" });

        Assert.NotNull(snapshot.GetConfig<LlmConfig>("agent-ttl", "LLMSettings"));

        await Task.Delay(TimeSpan.FromMilliseconds(1200));

        Assert.False(snapshot.TryGetConfig<LlmConfig>("agent-ttl", "LLMSettings", out _));
    }

    [Fact]
    public void Constructor_ZeroTtl_ThrowsArgumentOutOfRangeException()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ConfigSnapshot(
                Options.Create(new ConfigSnapshotOptions { AbsoluteExpirationMinutes = 0 }),
                new MemoryCache(new MemoryCacheOptions()),
                NullLogger<ConfigSnapshot>.Instance));

        Assert.Equal("ConfigSnapshot:AbsoluteExpirationMinutes", ex.ParamName);
    }

    [Fact]
    public void Constructor_NegativeTtl_ThrowsArgumentOutOfRangeException()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ConfigSnapshot(
                Options.Create(new ConfigSnapshotOptions { AbsoluteExpirationMinutes = -1 }),
                new MemoryCache(new MemoryCacheOptions()),
                NullLogger<ConfigSnapshot>.Instance));

        Assert.Equal("ConfigSnapshot:AbsoluteExpirationMinutes", ex.ParamName);
    }

    private static ConfigSnapshot CreateSnapshot(double ttlMinutes = 5)
    {
        return new ConfigSnapshot(
            Options.Create(new ConfigSnapshotOptions
            {
                AbsoluteExpirationMinutes = ttlMinutes
            }),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<ConfigSnapshot>.Instance);
    }

    private static void SeedRedisConfig(FakeRedisConnectionProvider redis, string agentId, string provider, string modelId)
    {
        var config = new AgentConfigEntity
        {
            AgentId = agentId,
            Config = new AgentConfig
            {
                Llm = new LlmConfig { Provider = provider, ModelId = modelId }
            },
            CurrentVersion = "7"
        };
        redis.SetString($"agent:config:{agentId}", JsonSerializer.Serialize(config));
    }

    private static HotReloadService CreateService(
        FakeRedisConnectionProvider redis,
        ConfigSnapshot snapshot,
        ILlmRegistry? llmRegistry = null)
    {
        llmRegistry ??= new TestLlmRegistry();
        var refresher = new FullConfigRefresher(
            redis,
            snapshot,
            NullLogger<FullConfigRefresher>.Instance);
        var llmRefresher = new LlmProfileRefresher(
            redis,
            llmRegistry,
            NullLogger<LlmProfileRefresher>.Instance);
        var dispatcher = new ConfigUpdateDispatcher(
            refresher,
            llmRefresher,
            new LegacyMessageHandler(
                refresher,
                llmRefresher,
                NullLogger<LegacyMessageHandler>.Instance),
            snapshot,
            NullLogger<ConfigUpdateDispatcher>.Instance);

        return new HotReloadService(
            redis,
            dispatcher,
            NullLogger<HotReloadService>.Instance);
    }

    private sealed class TestLlmRegistry : ILlmRegistry
    {
        private readonly Dictionary<string, LlmProviderProfile> _profiles =
            new(StringComparer.OrdinalIgnoreCase);

        public List<LlmProviderProfile> GetAllProfiles() => _profiles.Values.ToList();

        public LlmProviderProfile? GetProfile(string id) => _profiles.GetValueOrDefault(id);

        public void Register(LlmProviderProfile profile) => _profiles[profile.Id] = profile;

        public bool Remove(string id) => _profiles.Remove(id);

        public LlmConfig ResolveConfig(LlmConfig llmConfig) => llmConfig;
    }
}
