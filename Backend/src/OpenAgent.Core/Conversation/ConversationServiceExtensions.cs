using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Core.Conversation;
using OpenAgent.Core.Conversation.Lock;
using OpenAgent.Core.Conversation.Store;

namespace OpenAgent.Core.Exten;

internal static class ConversationServiceExtensions
{
    internal static IServiceCollection AddConversationServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<ConversationStoreOptions>(
            configuration.GetSection(ConversationStoreOptions.SectionName));
        services.TryAddSingleton<IConversationLock, InMemoryConversationLock>();
        services.AddScoped<ConversationSessionStore>();
        services.AddScoped<ConversationAgentResolver>();
        services.AddScoped<PlatformChatHistoryDependencies>();
        services.AddScoped<PlatformChatHistoryFactory>();
        services.AddScoped<IPlatformChatHistoryFactory>(serviceProvider =>
            serviceProvider.GetRequiredService<PlatformChatHistoryFactory>());
        services.AddScoped<ConversationHistoryFactory>();
        services.AddScoped<IConversationCompactionService, ConversationCompactionService>();
        services.AddScoped<IConversationQueryService>(CreateQueryService);
        return services;
    }

    private static IConversationQueryService CreateQueryService(IServiceProvider serviceProvider)
    {
        return new ConversationQueryService(
            serviceProvider.GetRequiredService<IConversationStore>());
    }
}
