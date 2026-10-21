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
        // 宿主注入 IAgentRuntimeDriver；未注册时明确失败，不静默回退到 MAF。
        services.TryAddScoped<IAgentExecution>(provider => ActivatorUtilities.CreateInstance<RuntimeAgentExecutor>(provider));
        services.TryAddScoped<RuntimePreparation>();
        services.TryAddScoped(provider => new AgentExecutor(provider.GetRequiredService<IAgentExecution>()));
        return services;
    }
}
