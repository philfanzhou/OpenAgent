using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Engine.Abstractions;
using OpenAgent.Engine.Models;
using OpenAgent.Engine.Redis;
using OpenAgent.Engine.Config;
using OpenAgent.Engine.Reload;
using OpenAgent.Engine.Registry;
using OpenAgent.Engine.Runtime;
using StackExchange.Redis;

namespace OpenAgent.Engine.Extensions;

internal static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAgentEngine(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<HeartbeatOptions>(configuration.GetSection("Heartbeat"));
        services.Configure<ConfigSnapshotOptions>(configuration.GetSection("ConfigSnapshot"));

        // Factory uses GetService so island mode works even when IConnectionMultiplexer
        // is not registered (e.g. Core's Redis connection string is empty).
        services.AddSingleton<IRedisConnectionProvider>(sp =>
            new RedisConnectionProvider(sp.GetService<IConnectionMultiplexer>()));

        // ConfigProvider helpers — were hand-newed inside ConfigProvider; now injected.
        services.AddSingleton<SecretInjector>();
        services.AddSingleton<MockAgentResolver>();
        services.AddSingleton<AgentListQuery>();

        services.AddSingleton<IAgentConfigProvider, ConfigProvider>();

        // Named client inherits Core's ConfigureHttpClientDefaults (skip-cert handler),
        // which the previous static HttpClient bypassed.
        services.AddHttpClient("SkillEndpoint");
        services.AddHostedService<RedisSkillRegistrar>();
        services.AddHostedService<RedisRagRegistrar>();
        services.AddHostedService<RedisLlmRegistrar>();

        services.AddHealthChecks()
            .AddCheck<RedisHealthCheck>("redis", tags: new[] { "infrastructure", "ready", "live" })
            .AddCheck<ConfigHealthCheck>("agent-config", tags: new[] { "ready" })
            .AddCheck<LlmHealthCheck>("llm-connectivity", tags: new[] { "live" });

        services.AddSingleton<IEngineRegistry, RedisRegistry>();
        services.AddHostedService<HeartbeatService>();

        // Container-owned IMemoryCache (Engine-wide). ConfigSnapshot consumes it;
        // key namespace "agent:{id}:config:{type}" is unique within Engine.
        services.AddMemoryCache();
        services.AddSingleton<ConfigSnapshot>();
        services.AddSingleton<FullConfigRefresher>();
        services.AddSingleton<LegacyMessageHandler>();
        services.AddSingleton<ConfigUpdateDispatcher>();
        services.AddHostedService<HotReloadService>();

        services.AddSingleton<ShutdownService>();
        services.AddHostedService(sp => sp.GetRequiredService<ShutdownService>());

        return services;
    }
}
