using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenAgent.Core.Abstract;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Core.Models;
using OpenAgent.Core.Runtime.Agent;
using OpenAgent.Core.Security;

namespace OpenAgent.Core.Exten;

internal static class RuntimeServiceExtensions
{
    internal static IServiceCollection AddRuntimeServices(this IServiceCollection services)
    {
        services.AddSingleton<ILlmRegistry, LlmRegistry>();
        services.AddSingleton<AgentChatClientFactory>();

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
        services.AddScoped<AgentFactory>();
        services.AddScoped(serviceProvider => new AgentExecutor(
            serviceProvider.GetRequiredService<IAgentConfigProvider>(),
            serviceProvider.GetRequiredService<AgentAuthorizationGate>(),
            serviceProvider.GetRequiredService<AgentFactory>()));
        return services;
    }
}
