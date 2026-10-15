using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Application.Conversation;
using OpenAgent.Application.Execution;
using OpenAgent.Capabilities.Files.Requests;
using OpenAgent.Application.Runtime.Agent;

namespace OpenAgent.Application.Extensions;

internal static class RuntimeServiceExtensions
{
    internal static IServiceCollection AddRuntimeServices(this IServiceCollection services)
    {
        services.TryAddScoped<AgentRuntimeResolver>();
        services.TryAddScoped<IAgentRuntimeResolver>(provider => provider.GetRequiredService<AgentRuntimeResolver>());
        services.TryAddScoped<AgentFactory>();
        services.TryAddScoped(provider => new AgentExecutor(
            provider.GetRequiredService<IAgentRuntimeResolver>(),
            provider.GetRequiredService<AgentFactory>(),
            provider.GetRequiredService<ConversationAgentResolver>(),
            provider.GetRequiredService<FileAssetRequestResolver>()));
        return services;
    }
}
