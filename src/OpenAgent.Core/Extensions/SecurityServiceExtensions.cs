using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenAgent.Core.Security;

namespace OpenAgent.Core.Extensions;

internal static class SecurityServiceExtensions
{
    internal static IServiceCollection AddSecurityServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AgentAuthorizationOptions>(configuration.GetSection("Authorization"));
        services.TryAddScoped<IAgentAuthorizationService>(provider =>
            provider.GetRequiredService<IOptions<AgentAuthorizationOptions>>().Value.Mode == AgentAuthorizationMode.Claims
                ? new ClaimsAgentAuthorizationService()
                : new AllowAllAgentAuthorizationService());
        services.TryAddScoped<AgentAuthorizationGate>();
        return services;
    }
}
