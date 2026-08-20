using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Core.Abstract;
using OpenAgent.Core.Conversation;
using OpenAgent.Core.Files;
using OpenAgent.Core.Models;
using OpenAgent.Core.Runtime.Agent;
using OpenAgent.Core.Security;
using OpenAgent.Contracts.Approvals;
using OpenAgent.Core.Approvals;
using Microsoft.Extensions.Hosting;

namespace OpenAgent.Core.Exten;

internal static class RuntimeServiceExtensions
{
    internal static IServiceCollection AddRuntimeServices(this IServiceCollection services)
    {
        services.AddSingleton<ILlmRegistry, LlmRegistry>();
        services.AddSingleton<IAgentChatClientFactory, AgentChatClientFactory>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IHumanApprovalStore, InMemoryHumanApprovalStore>();

        services.TryAddScoped<IAgentAuthorizationService>(serviceProvider =>
        {
            AgentAuthorizationMode mode = serviceProvider
                .GetRequiredService<IOptions<AgentAuthorizationOptions>>()
                .Value.Mode;
            return mode == AgentAuthorizationMode.Claims
                ? new ClaimsAgentAuthorizationService()
                : new AllowAllAgentAuthorizationService();
        });
        services.AddScoped<AgentAuthorizationGate>();
        services.AddScoped<AgentRuntimeResolver>();
        services.AddScoped<IAgentRuntimeResolver>(serviceProvider =>
            serviceProvider.GetRequiredService<AgentRuntimeResolver>());
        services.AddScoped<AgentFactory>();
        services.AddScoped<HumanApprovalService>();
        services.AddScoped<IHumanApprovalService>(serviceProvider =>
            serviceProvider.GetRequiredService<HumanApprovalService>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, HumanApprovalExpirationService>());
        services.AddScoped(serviceProvider => new AgentExecutor(
            serviceProvider.GetRequiredService<IAgentRuntimeResolver>(),
            serviceProvider.GetRequiredService<AgentFactory>(),
            serviceProvider.GetRequiredService<ConversationAgentResolver>(),
            serviceProvider.GetRequiredService<FileAssetRequestResolver>(),
            serviceProvider.GetRequiredService<HumanApprovalService>()));
        return services;
    }
}
