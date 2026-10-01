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
        services.Configure<LlmInteractionOptions>(
            configuration.GetSection(LlmInteractionOptions.SectionName));
        services.TryAddSingleton<IConversationLock, InMemoryConversationLock>();
        services.TryAddScoped<ConversationSessionStore>();
        services.TryAddScoped<ConversationAgentResolver>();
        services.TryAddScoped<PlatformChatHistoryFactory>();
        services.TryAddScoped<IPlatformChatHistoryFactory>(serviceProvider =>
            serviceProvider.GetRequiredService<PlatformChatHistoryFactory>());
        services.TryAddScoped<ConversationHistoryFactory>();
        services.TryAddScoped<IConversationCompactionService, ConversationCompactionService>();
        services.TryAddScoped<IConversationQueryService>(CreateQueryService);
        return services;
    }

    private static IConversationQueryService CreateQueryService(IServiceProvider serviceProvider)
    {
        return new ConversationQueryService(
            serviceProvider.GetRequiredService<IConversationStore>());
    }
}
