using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Models;
using OpenAgent.Core.Abstract;
using OpenAgent.Engine.Abstractions;
using OpenAgent.Engine.Extensions;
using OpenAgent.Engine.Models;
using OpenAgent.Engine.Reload;
using Xunit;

namespace OpenAgent.Engine.Tests.Config;

public class ConfigUpdateRegistrationTests
{
    [Fact]
    public void AddAgentEngine_RegistersReloadPipelineAsSingletons()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentEngine(new ConfigurationBuilder().Build());
        services.AddSingleton<IRedisConnectionProvider, FakeRedisConnectionProvider>();
        services.AddSingleton(Mock.Of<ILlmRegistry>());

        using var provider = services.BuildServiceProvider(validateScopes: true);

        var first = provider.GetRequiredService<ConfigUpdateDispatcher>();
        var second = provider.GetRequiredService<ConfigUpdateDispatcher>();

        Assert.Same(first, second);
        Assert.NotNull(provider.GetRequiredService<ConfigSnapshot>());
        Assert.NotNull(provider.GetRequiredService<FullConfigRefresher>());
        Assert.NotNull(provider.GetRequiredService<LlmProfileRefresher>());
        Assert.NotNull(provider.GetRequiredService<LegacyMessageHandler>());
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType == typeof(HotReloadService));
    }

    [Fact]
    public void RegisteredDispatcher_TypedUpdateRefreshesSnapshotFromRedis()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentEngine(new ConfigurationBuilder().Build());
        services.AddSingleton<IRedisConnectionProvider, FakeRedisConnectionProvider>();
        services.AddSingleton(Mock.Of<ILlmRegistry>());

        using var provider = services.BuildServiceProvider(validateScopes: true);
        var redis = (FakeRedisConnectionProvider)provider.GetRequiredService<IRedisConnectionProvider>();
        var config = new AgentConfigEntity
        {
            AgentId = "agent-from-di",
            Config = new AgentConfig
            {
                Llm = new LlmConfig { Provider = "provider-from-di", ModelId = "model-from-di" }
            },
            CurrentVersion = "3"
        };
        redis.SetString("agent:config:agent-from-di", JsonSerializer.Serialize(config));
        var dispatcher = provider.GetRequiredService<ConfigUpdateDispatcher>();
        var snapshot = provider.GetRequiredService<ConfigSnapshot>();

        dispatcher.Process(
            HotReloadService.CurrentUpdatesChannel,
            """
            {
              "agentId": "agent-from-di",
              "type": "IncrementalUpdate",
              "configType": "LLMSettings",
              "version": 1,
              "data": {
                "provider": "ignored-provider",
                "modelId": "ignored-model"
              }
            }
            """);

        var llmConfig = snapshot.GetConfig<LlmConfig>("agent-from-di", "LLMSettings");
        Assert.NotNull(llmConfig);
        Assert.Equal("provider-from-di", llmConfig!.Provider);
        Assert.Equal("model-from-di", llmConfig.ModelId);
    }
}
