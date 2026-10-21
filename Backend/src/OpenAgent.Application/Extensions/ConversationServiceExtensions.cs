using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Application.Conversation.Compaction;
using OpenAgent.Application.Conversation.History;
using OpenAgent.Application.Conversation.Lock;
using OpenAgent.Application.Conversation.Store;
using OpenAgent.Application.Conversation;

namespace OpenAgent.Application.Extensions;

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
