using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Infrastructure;
using OpenAgent.Contracts.Security;
using OpenAgent.Contracts.Skills;
using OpenAgent.Infrastructure.Skills;
using OpenAgent.Infrastructure.Security;
using StackExchange.Redis;

namespace OpenAgent.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddOpenAgentInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        StorageOptions storage = configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>() ?? new();
        if (!string.Equals(storage.Provider, "PostgreSql", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(storage.Provider, "Postgres", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"Storage provider '{storage.Provider}' is not registered. Add its Infrastructure adapter first.");
        }

        string connectionString = configuration.GetConnectionString(storage.ConnectionStringName)
            ?? throw new InvalidOperationException($"ConnectionStrings:{storage.ConnectionStringName} is required.");
        services.AddDbContextFactory<OpenAgentDbContext>(options => options.UseNpgsql(connectionString));
        services.Configure<StorageOptions>(options =>
            configuration.GetSection(StorageOptions.SectionName).Bind(options));
        services.Configure<ConversationCacheOptions>(options =>
            configuration.GetSection(ConversationCacheOptions.SectionName).Bind(options));

        // The durable conversation store evaluates tenant/user ownership from
        // the request-scoped current-user context, so it must not be captured
        // by a singleton registration.
        services.AddScoped<EfCoreConversationStore>();
        services.AddScoped<IThirdPartyApiKeyIdentityResolver, EfThirdPartyApiKeyIdentityResolver>();
        services.AddSingleton<IFileAssetRepository, EfCoreFileAssetRepository>();
        services.AddSingleton<ISkillDefinitionRepository, EfCoreSkillDefinitionRepository>();

        ConversationCacheOptions cache = configuration.GetSection(ConversationCacheOptions.SectionName)
            .Get<ConversationCacheOptions>() ?? new();
        string? redisConnectionString = !string.IsNullOrWhiteSpace(cache.ConnectionString)
            ? cache.ConnectionString
            : configuration.GetConnectionString("Redis");
        if (cache.Enabled && !string.IsNullOrWhiteSpace(redisConnectionString))
        {
            services.TryAddSingleton<IConnectionMultiplexer>(serviceProvider =>
            {
                ConfigurationOptions options = ConfigurationOptions.Parse(redisConnectionString);
                options.AbortOnConnectFail = false;
                options.ConnectRetry = 3;
                options.ConnectTimeout = 5_000;
                return ConnectionMultiplexer.Connect(options);
            });
            services.AddSingleton<IConversationCache, RedisConversationCache>();
            services.AddScoped<IConversationStore>(serviceProvider => new WriteThroughConversationStore(
                serviceProvider.GetRequiredService<EfCoreConversationStore>(),
                serviceProvider.GetRequiredService<IConversationCache>(),
                serviceProvider.GetRequiredService<ILogger<WriteThroughConversationStore>>()));
            services.Replace(ServiceDescriptor.Singleton<IConversationLock, RedisConversationLock>());
        }
        else
        {
            services.AddScoped<IConversationStore>(serviceProvider =>
                serviceProvider.GetRequiredService<EfCoreConversationStore>());
        }

        return services;
    }
}
