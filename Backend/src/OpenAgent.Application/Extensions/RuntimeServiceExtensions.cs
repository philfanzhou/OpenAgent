using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Application.Conversation;
using OpenAgent.Application.Execution;
using OpenAgent.Application.Observability;
using OpenAgent.Capabilities.Files.Requests;

namespace OpenAgent.Application.Extensions;

internal static class RuntimeServiceExtensions
{
    internal static IServiceCollection AddRuntimeServices(this IServiceCollection services)
    {
        services.TryAddScoped<AgentRuntimeResolver>();
        services.TryAddScoped<IAgentRuntimeResolver>(provider => provider.GetRequiredService<AgentRuntimeResolver>());
        services.TryAddSingleton(provider => new TurnAuditWarning(
            provider.GetService<ILogger<RuntimeAgentExecutor>>() ?? NullLogger<RuntimeAgentExecutor>.Instance,
            provider.GetService<TraceExportStatus>()?.IsConfigured == true));
        // 宿主注入 IAgentRuntimeDriver；未注册时明确失败，不静默回退到进程内推理。
        services.TryAddScoped(provider => new RuntimeAgentExecutor(
            provider.GetRequiredService<IAgentRuntimeResolver>(),
            provider.GetServices<IAgentRuntimeDriver>(),
            provider.GetRequiredService<ConversationAgentResolver>(),
            provider.GetRequiredService<FileAssetRequestResolver>(),
            provider.GetRequiredService<RuntimePreparation>(),
            provider.GetRequiredService<TurnAuditWarning>(),
            provider.GetService<IRuntimeProfileRepository>(),
            provider.GetService<IConfiguredRuntimeProfiles>()));
        services.TryAddScoped<RuntimePreparation>();
        return services;
    }
}
