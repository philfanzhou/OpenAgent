using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Core.Conversation.Compaction;
using OpenAgent.Core.Conversation.History;
using OpenAgent.Core.Conversation.Lock;
using OpenAgent.Core.Conversation.Store;
using OpenAgent.Core.Conversation;

namespace OpenAgent.Core.Extensions;

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
