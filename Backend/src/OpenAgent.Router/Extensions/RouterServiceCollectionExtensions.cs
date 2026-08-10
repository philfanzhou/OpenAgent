using OpenAgent.Contracts.Routing;
using OpenAgent.Core.Routing;
using OpenAgent.Router.Endpoints;
using OpenAgent.Router.Options;
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
            .Validate(
                options => !options.Enabled || !string.IsNullOrWhiteSpace(options.AgentId),
                "Intent recognition AgentId is required when intent recognition is enabled")
            .Validate(
                options => options.TimeoutMs > 0,
                "Intent recognition TimeoutMs must be greater than zero")
            .Validate(
                options => options.MinimumConfidence is >= 0 and <= 1,
                "Intent recognition MinimumConfidence must be between zero and one")
            .ValidateOnStart();
        services.AddHttpClient<IIntentAgentSelector, IntentAgentSelector>(client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
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
