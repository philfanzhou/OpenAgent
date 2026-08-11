using OpenAgent.Contracts.Routing;
using OpenAgent.Core.Routing;
using OpenAgent.Router.Endpoints;
using OpenAgent.Router.Options;
using OpenAgent.Router.Providers;
using OpenAgent.Router.Routing;
using StackExchange.Redis;

namespace OpenAgent.Router;

public static class RouterServiceCollectionExtensions
{
    public static IServiceCollection AddRouterRuntime(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton<IConsistentHashRing, JumpHashConsistentHashRing>();
        services.AddOptions<IntentRecognitionOptions>()
            .Bind(configuration.GetSection(IntentRecognitionOptions.SectionName))
            .Validate(IntentRecognitionOptions.IsValid, "Intent recognition configuration is invalid")
            .ValidateOnStart();
        services.AddOptions<AgentProviderOptions>()
            .Bind(configuration.GetSection(AgentProviderOptions.SectionName))
            .Validate(AgentProviderOptions.IsValid, "Agent provider configuration is invalid")
            .ValidateOnStart();
        services.AddSingleton<IAgentProviderFactory, OpenAgentEngineProviderFactory>();
        services.AddSingleton<IAgentProviderRegistry, AgentProviderRegistry>();
        services.AddSingleton<IAgentForwarder, AgentForwarder>();
        services.AddSingleton<IIntentAgentSelector, IntentAgentSelector>();
        services.AddScoped<IAgentSelectionService, AgentSelectionService>();

        var redisConnectionString = configuration.GetConnectionString("Redis");
        if (!string.IsNullOrEmpty(redisConnectionString))
        {
            services.AddSingleton<IConnectionMultiplexer>(_ =>
            {
                var redisOptions = ConfigurationOptions.Parse(redisConnectionString);
                redisOptions.AbortOnConnectFail = false;
                return ConnectionMultiplexer.Connect(redisOptions);
            });
            services.AddStackExchangeRedisCache(options =>
            {
                options.Configuration = redisConnectionString;
            });
            services.AddSingleton<EngineRegistrySnapshotCache>();
            services.AddHostedService(
                provider => provider.GetRequiredService<EngineRegistrySnapshotCache>());
            services.AddSingleton<IRouteTable>(provider =>
            {
                var dynamicRouteTable = new RedisServiceDiscoveryRouteTable(
                    provider.GetRequiredService<IConnectionMultiplexer>(),
                    provider.GetRequiredService<EngineRegistrySnapshotCache>(),
                    provider.GetRequiredService<ILogger<RedisServiceDiscoveryRouteTable>>(),
                    provider.GetRequiredService<IConsistentHashRing>());
                var staticRouteTable = new InMemoryRouteTable(configuration);
                return new CompositeRouteTable(
                    dynamicRouteTable,
                    staticRouteTable,
                    provider.GetRequiredService<ILogger<CompositeRouteTable>>());
            });
        }
        else
        {
            services.AddDistributedMemoryCache();
            services.AddSingleton<IRouteTable, InMemoryRouteTable>();
        }

        services.AddSingleton<IRateLimiter, RedisRateLimiter>();
        return services;
    }
}
