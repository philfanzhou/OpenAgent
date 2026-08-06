using OpenAgent.Contracts.Routing;
using OpenAgent.Core.Routing;
using StackExchange.Redis;

namespace OpenAgent.Router;

public static class RouterServiceCollectionExtensions
{
    public static IServiceCollection AddRouterRuntime(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton<IConsistentHashRing, JumpHashConsistentHashRing>();

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
