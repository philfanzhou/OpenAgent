using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Application.Conversation;
using OpenAgent.Application.Execution;
using OpenAgent.Capabilities.Files.Requests;

namespace OpenAgent.Application.Extensions;

internal static class RuntimeServiceExtensions
{
    internal static IServiceCollection AddRuntimeServices(this IServiceCollection services)
    {
        services.TryAddScoped<AgentRuntimeResolver>();
        services.TryAddScoped<IAgentRuntimeResolver>(provider => provider.GetRequiredService<AgentRuntimeResolver>());
        // 宿主注入 IAgentRuntimeDriver；未注册时明确失败，不静默回退到进程内推理。
        services.TryAddScoped(provider => new RuntimeAgentExecutor(
            provider.GetRequiredService<IAgentRuntimeResolver>(),
            provider.GetRequiredService<IAgentRuntimeDriver>(),
            provider.GetRequiredService<ConversationAgentResolver>(),
            provider.GetRequiredService<FileAssetRequestResolver>(),
            provider.GetRequiredService<RuntimePreparation>(),
            provider.GetService<IRuntimeProfileRepository>()));
        services.TryAddScoped<RuntimePreparation>();
        return services;
    }
}
