using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Application.Conversation;
using OpenAgent.Application.Execution;
using OpenAgent.Application.Execution.Admission;
using OpenAgent.Capabilities.Files.Requests;

namespace OpenAgent.Application.Extensions;

internal static class RuntimeServiceExtensions
{
    internal static IServiceCollection AddRuntimeServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AdmissionOptions>().Bind(configuration.GetSection(AdmissionOptions.SectionName))
            .Validate(limits => limits.MaxConcurrentTurns is > 0 and <= 128
                && limits.MaxQueuedTurns is >= 0 and <= 4096
                && limits.MaxConcurrentPerTenant > 0 && limits.MaxConcurrentPerTenant <= limits.MaxConcurrentTurns
                && limits.MaxQueuedPerTenant >= 0 && limits.MaxQueuedPerTenant <= limits.MaxQueuedTurns
                && limits.QueueTimeoutSeconds is > 0 and <= 3600
                && limits.ExecutionTimeoutSeconds is > 0 and <= 86400
                && limits.RetryAfterSeconds is > 0 and <= 3600, "Invalid runtime admission limits.")
            .ValidateOnStart();
        services.TryAddSingleton<TurnAdmission>();
        services.TryAddScoped<AgentRuntimeResolver>();
        services.TryAddScoped<IAgentRuntimeResolver>(provider => provider.GetRequiredService<AgentRuntimeResolver>());
        // 宿主注入 IAgentRuntimeDriver；未注册时明确失败，不静默回退到进程内推理。
        services.TryAddScoped(provider => new RuntimeAgentExecutor(
            provider.GetRequiredService<IAgentRuntimeResolver>(),
            provider.GetRequiredService<IAgentRuntimeDriver>(),
            provider.GetRequiredService<ConversationAgentResolver>(),
            provider.GetRequiredService<FileAssetRequestResolver>(),
            provider.GetRequiredService<RuntimePreparation>(),
            provider.GetRequiredService<TurnAdmission>(),
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AdmissionOptions>>(),
            provider.GetService<IRuntimeProfileRepository>()));
        services.TryAddScoped<RuntimePreparation>();
        return services;
    }
}
