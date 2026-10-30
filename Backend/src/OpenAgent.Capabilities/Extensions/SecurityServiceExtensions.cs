using Microsoft.Extensions.Configuration;
using OpenAgent.Contracts.Security;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenAgent.Capabilities.Security;

namespace OpenAgent.Capabilities.Extensions;

internal static class SecurityServiceExtensions
{
    internal static IServiceCollection AddSecurityServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AgentAuthorizationOptions>(configuration.GetSection("Authorization"));
        services.TryAddScoped<IAgentAuthorizationService>(provider =>
            provider.GetRequiredService<IOptions<AgentAuthorizationOptions>>().Value.Mode == AgentAuthorizationMode.Claims
                || provider.GetRequiredService<IOptions<AgentAuthorizationOptions>>().Value.RequireResourcePolicy
                ? new ResourcePolicyAuthorizationService(provider.GetRequiredService<IResourcePolicyStore>(),
                    provider.GetRequiredService<IOptions<AgentAuthorizationOptions>>())
                : new AllowAllAgentAuthorizationService());
        services.TryAddScoped<IResourceAuthorization>(provider => provider.GetRequiredService<IAgentAuthorizationService>());
        services.TryAddScoped<AgentAuthorizationGate>();
        return services;
    }
}
