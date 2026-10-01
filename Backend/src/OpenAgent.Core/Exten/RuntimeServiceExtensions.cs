using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Core.Abstract;
using OpenAgent.Core.Conversation;
using OpenAgent.Core.Files;
using OpenAgent.Core.Runtime.Agent;
using OpenAgent.Core.Security;

namespace OpenAgent.Core.Exten;

internal static class RuntimeServiceExtensions
{
    internal static IServiceCollection AddRuntimeServices(this IServiceCollection services)
    {
        services.TryAddSingleton<IAgentChatClientFactory, AgentChatClientFactory>();
        services.TryAddScoped<IAgentAuthorizationService>(serviceProvider =>
        {
            AgentAuthorizationMode mode = serviceProvider
                .GetRequiredService<IOptions<AgentAuthorizationOptions>>()
                .Value.Mode;
            return mode == AgentAuthorizationMode.Claims
                ? new ClaimsAgentAuthorizationService()
                : new AllowAllAgentAuthorizationService();
        });
        services.TryAddScoped<AgentAuthorizationGate>();
        services.TryAddScoped<AgentRuntimeResolver>();
        services.TryAddScoped<IAgentRuntimeResolver>(serviceProvider =>
            serviceProvider.GetRequiredService<AgentRuntimeResolver>());
        services.TryAddScoped<AgentFactory>();
        services.TryAddScoped(serviceProvider => new AgentExecutor(
            serviceProvider.GetRequiredService<IAgentRuntimeResolver>(),
            serviceProvider.GetRequiredService<AgentFactory>(),
            serviceProvider.GetRequiredService<ConversationAgentResolver>(),
            serviceProvider.GetRequiredService<FileAssetRequestResolver>()));
        return services;
    }
}
