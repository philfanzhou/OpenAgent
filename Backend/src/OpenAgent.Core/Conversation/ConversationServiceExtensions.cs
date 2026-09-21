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
        // 窄契约转发到组合契约的同一 scope 实例（惰性解析，注册顺序无关）。
        services.TryAddScoped<IConversationReader>(serviceProvider =>
            serviceProvider.GetRequiredService<IConversationStore>());
        services.TryAddScoped<IConversationWriter>(serviceProvider =>
            serviceProvider.GetRequiredService<IConversationStore>());
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
