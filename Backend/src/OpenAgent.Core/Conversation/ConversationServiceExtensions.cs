using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Core.Conversation;
using OpenAgent.Core.Conversation.Lock;
using OpenAgent.Core.Conversation.Repository;
using OpenAgent.Core.Conversation.Store;
using StackExchange.Redis;

namespace OpenAgent.Core.Exten;

internal static class ConversationServiceExtensions
{
    internal static IServiceCollection AddConversationServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton<ConversationStoreMetrics>();
        services.Configure<ConversationStoreOptions>(
            configuration.GetSection(ConversationStoreOptions.SectionName));
        services.AddKeyedSingleton<IConversationRepository, SqlServerConversationRepository>("SqlServer");
        services.AddKeyedSingleton<IConversationRepository, SqliteConversationRepository>("Sqlite");

        ConversationStoreOptions options = configuration
            .GetSection(ConversationStoreOptions.SectionName)
            .Get<ConversationStoreOptions>()
            ?? new ConversationStoreOptions();
        string? redisConnectionString = !string.IsNullOrWhiteSpace(options.RedisConnectionString)
            ? options.RedisConnectionString
            : configuration.GetConnectionString("Redis");

        if (string.IsNullOrWhiteSpace(redisConnectionString))
        {
            services.AddSingleton<IConversationStore, InMemoryConversationStore>();
            services.AddSingleton<IConversationLock, InMemoryConversationLock>();
        }
        else
        {
            AddRedisConversationServices(
                services,
                redisConnectionString,
                options.EnableColdArchive);
        }

        services.AddScoped<ConversationSessionStore>();
        services.AddScoped<ConversationAgentResolver>();
        services.AddScoped<ConversationHistoryFactory>();
        services.AddScoped<IConversationQueryService>(CreateQueryService);
        services.AddHostedService<ConversationArchiveMigrationService>();
        return services;
    }

    private static void AddRedisConversationServices(
        IServiceCollection services,
        string redisConnectionString,
        bool enableColdArchive)
    {
        services.TryAddSingleton<IConnectionMultiplexer>(serviceProvider =>
        {
            try
            {
                ConfigurationOptions redis = ConfigurationOptions.Parse(redisConnectionString);
                redis.AbortOnConnectFail = false;
                redis.ConnectRetry = 3;
                redis.ConnectTimeout = 5000;
                return ConnectionMultiplexer.Connect(redis);
            }
            catch (Exception ex)
            {
                serviceProvider
                    .GetRequiredService<ILogger<IConnectionMultiplexer>>()
                    .LogWarning(ex, "Redis unavailable; using in-memory conversation isolation.");
                return null!;
            }
        });

        services.AddSingleton(serviceProvider => new RedisTenantIndexManager(
            serviceProvider.GetRequiredService<IOptions<ConversationStoreOptions>>()));
        services.AddSingleton<RedisConversationStore>();
        services.AddSingleton(serviceProvider => new ConversationWarmer(
            serviceProvider.GetRequiredService<RedisConversationStore>(),
            ResolveArchive(serviceProvider),
            serviceProvider.GetRequiredService<ILogger<ConversationWarmer>>()));
        services.AddSingleton(serviceProvider => new CompensationArchiveService(
            ResolveArchive(serviceProvider),
            serviceProvider.GetRequiredService<ILogger<CompensationArchiveService>>()));
        services.AddSingleton<IConversationStore>(serviceProvider =>
        {
            if (serviceProvider.GetService<IConnectionMultiplexer>() == null)
            {
                return new InMemoryConversationStore();
            }

            RedisConversationStore hot = serviceProvider.GetRequiredService<RedisConversationStore>();
            if (!enableColdArchive)
            {
                return hot;
            }

            return new DualWriteConversationStore(
                hot,
                ResolveArchive(serviceProvider),
                serviceProvider.GetRequiredService<IOptions<ConversationStoreOptions>>(),
                serviceProvider.GetRequiredService<ILogger<DualWriteConversationStore>>(),
                serviceProvider.GetRequiredService<ConversationWarmer>(),
                serviceProvider.GetRequiredService<CompensationArchiveService>());
        });
        services.AddSingleton<IConversationLock>(serviceProvider =>
        {
            IConnectionMultiplexer? redis = serviceProvider.GetService<IConnectionMultiplexer>();
            return redis == null
                ? new InMemoryConversationLock()
                : new RedisConversationLock(
                    redis,
                    serviceProvider.GetRequiredService<ILogger<RedisConversationLock>>());
        });
    }

    private static IConversationQueryService CreateQueryService(IServiceProvider serviceProvider)
    {
        IOptions<ConversationStoreOptions> options =
            serviceProvider.GetRequiredService<IOptions<ConversationStoreOptions>>();
        IConversationRepository? archive = options.Value.EnableColdArchive
            ? ResolveArchive(serviceProvider)
            : null;
        return new ConversationQueryService(
            serviceProvider.GetRequiredService<IConversationStore>(),
            serviceProvider.GetRequiredService<ILogger<ConversationQueryService>>(),
            archive);
    }

    private static IConversationRepository ResolveArchive(IServiceProvider serviceProvider)
    {
        string provider = serviceProvider
            .GetRequiredService<IOptions<ConversationStoreOptions>>()
            .Value
            .ColdArchiveProvider;
        return serviceProvider.GetRequiredKeyedService<IConversationRepository>(provider);
    }
}
