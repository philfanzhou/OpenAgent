using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace OpenAgent.Hosting.Authorization;

internal sealed class GatewayAuthorizationPolicyProvider(
    IOptions<AuthorizationOptions> options) : DefaultAuthorizationPolicyProvider(options)
{
    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        AuthorizationPolicy? configured = await base.GetPolicyAsync(policyName).ConfigureAwait(false);
        if (configured != null)
        {
            return configured;
        }

        return new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new GatewayPermissionRequirement(policyName))
            .Build();
    }
}
