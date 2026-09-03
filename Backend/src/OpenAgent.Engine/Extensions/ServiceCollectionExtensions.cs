using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Core.Abstract;
using OpenAgent.Engine.Abstractions;
using OpenAgent.Engine.Config;
using OpenAgent.Engine.Models;
using OpenAgent.Engine.Redis;
using OpenAgent.Engine.Registry;
using OpenAgent.Engine.Runtime;
using StackExchange.Redis;

namespace OpenAgent.Engine.Extensions;

internal static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAgentEngine(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<HeartbeatOptions>(configuration.GetSection("Heartbeat"));
        services.Configure<AgentConfigSourceOptions>(
            configuration.GetSection(AgentConfigSourceOptions.SectionName));

        // Factory uses GetService so island mode works even when IConnectionMultiplexer
        // is not registered (e.g. Core's Redis connection string is empty).
        services.AddSingleton<IRedisConnectionProvider>(sp =>
            new RedisConnectionProvider(sp.GetService<IConnectionMultiplexer>()));

        services.AddDataProtection();
        services.Replace(ServiceDescriptor.Singleton<IAgentSecretResolver>(serviceProvider =>
            new ConfigurationSecretResolver(
                configuration,
                serviceProvider.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>())));
        services.AddSingleton(serviceProvider => new ConfigurationService(
            serviceProvider.GetRequiredService<IAgentConfigRepository>(),
            serviceProvider.GetRequiredService<ILlmConfigRepository>(),
            serviceProvider.GetRequiredService<IRedisConnectionProvider>(),
            serviceProvider.GetRequiredService<IOptions<AgentConfigSourceOptions>>(),
            serviceProvider.GetRequiredService<IAgentSecretResolver>(),
            serviceProvider.GetRequiredService<ILogger<ConfigurationService>>()));
        services.AddSingleton<ILlmConfigProvider>(serviceProvider =>
            serviceProvider.GetRequiredService<ConfigurationService>());
        services.AddSingleton<McpProfileManagementService>();
        services.AddSingleton<RedisSkillCatalogStore>();
        services.AddSingleton<ISkillCatalogStore>(serviceProvider =>
            serviceProvider.GetRequiredService<RedisSkillCatalogStore>());
        services.Replace(ServiceDescriptor.Singleton<ISkillCatalog>(serviceProvider =>
            serviceProvider.GetRequiredService<RedisSkillCatalogStore>()));

        services.AddSingleton<IAgentConfigProvider>(serviceProvider =>
            serviceProvider.GetRequiredService<ConfigurationService>());

        services.AddHostedService<RedisRagRegistrar>();
        services.AddHostedService<RedisMcpRegistrar>();

        services.AddHealthChecks()
            .AddCheck<RedisHealthCheck>("redis", tags: new[] { "infrastructure", "ready", "live" })
            .AddCheck<ConfigHealthCheck>("agent-config", tags: new[] { "ready" });

        services.AddSingleton<IEngineRegistry, RedisRegistry>();
        services.AddHostedService<HeartbeatService>();

        services.AddSingleton<ShutdownService>();
        services.AddHostedService(sp => sp.GetRequiredService<ShutdownService>());

        return services;
    }
}
