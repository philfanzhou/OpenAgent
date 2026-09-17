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
        // 默认进程内实现，生产环境由 Infrastructure 的 EF Core 实现替换。
        services.TryAddSingleton<ILlmInteractionStore, InMemoryLlmInteractionStore>();
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
