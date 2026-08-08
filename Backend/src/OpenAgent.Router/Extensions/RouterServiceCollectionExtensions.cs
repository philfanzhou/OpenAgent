using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Routing;
using OpenAgent.Core.Routing;
using OpenAgent.Router.Endpoints;
using OpenAgent.Router.Options;
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
            .Validate(
                options => !options.Enabled || !string.IsNullOrWhiteSpace(options.AgentId),
                "Intent recognition AgentId is required when intent recognition is enabled")
            .Validate(
                options => options.TimeoutMs > 0,
                "Intent recognition TimeoutMs must be greater than zero")
            .Validate(
                options => options.MinimumConfidence is >= 0 and <= 1,
                "Intent recognition MinimumConfidence must be between zero and one")
            .Validate(
                options => options.MaxCandidates > 0
                    && options.MaxMessageCharacters > 0
                    && options.CatalogCacheSeconds > 0,
                "Intent recognition input limits must be greater than zero")
            .ValidateOnStart();
        services.AddOptions<ExternalAgentRoutingOptions>()
            .Bind(configuration.GetSection(ExternalAgentRoutingOptions.SectionName))
            .Validate(
                ValidateExternalAgents,
                "External Agent entries require unique IDs, valid HTTP endpoints, absolute chat paths, and valid authentication header names")
            .ValidateOnStart();
        services.AddMemoryCache();
        services.AddHttpClient("RouterEngineAgent", client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
        services.AddSingleton<IEngineAgentClient>(provider => new EngineAgentClient(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("RouterEngineAgent")));
        services.AddSingleton<IExternalAgentRegistry, ExternalAgentRegistry>();
        services.AddSingleton<IExternalAgentAdapter, OpenAgentExternalAdapter>();
        services.AddSingleton<IValidateOptions<ExternalAgentRoutingOptions>, ExternalAgentOptionsValidator>();
        services.AddSingleton<IExternalAgentForwarder, ExternalAgentForwarder>();
        services.AddSingleton<IAgentCatalog, AgentCatalog>();
        services.AddSingleton<IIntentAgentSelector, IntentAgentSelector>();

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

    private static bool ValidateExternalAgents(ExternalAgentRoutingOptions options)
    {
        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        foreach (ExternalAgentOptions agent in options.Agents)
        {
            if (string.IsNullOrWhiteSpace(agent.AgentId)
                || !ids.Add(agent.AgentId)
                || string.IsNullOrWhiteSpace(agent.Adapter)
                || !Uri.TryCreate(agent.BaseUrl, UriKind.Absolute, out Uri? endpoint)
                || (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                || string.IsNullOrWhiteSpace(agent.ChatPath)
                || !agent.ChatPath.StartsWith("/", StringComparison.Ordinal)
                || agent.Authentication == null
                || !IsValidHeaderName(agent.Authentication.HeaderName))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidHeaderName(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');
}
