using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Hosting.Security;

namespace OpenAgent.Hosting.Authentication;

internal static class AgentAuthenticationExtensions
{
    internal static IServiceCollection AddAgentAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<AgentAuthenticationOptions>()
            .Bind(configuration.GetSection("Authentication"));
        AgentAuthenticationOptions options = configuration
            .GetSection("Authentication")
            .Get<AgentAuthenticationOptions>() ?? new AgentAuthenticationOptions();

        switch (options.Mode)
        {
            case AgentAuthenticationMode.JwtBearer:
                services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                    .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, jwt =>
                    {
                        jwt.Authority = options.Authority;
                        jwt.Audience = options.Audience;
                        jwt.RequireHttpsMetadata = options.RequireHttpsMetadata;
                        jwt.MapInboundClaims = false;
                        jwt.TokenValidationParameters.NameClaimType = "name";
                        jwt.TokenValidationParameters.RoleClaimType = "roles";
                    });
                break;
            case AgentAuthenticationMode.OpaqueIntrospection:
                services.AddHttpClient(OpaqueIntrospectionAuthenticationHandler.ClientName);
                services.AddAuthentication(OpaqueIntrospectionAuthenticationHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, OpaqueIntrospectionAuthenticationHandler>(
                        OpaqueIntrospectionAuthenticationHandler.SchemeName, _ => { });
                break;
            case AgentAuthenticationMode.ApiKey:
                services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
                        ApiKeyAuthenticationHandler.SchemeName, _ => { });
                break;
            default:
                services.AddAuthentication(PassThroughAuthenticationHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, PassThroughAuthenticationHandler>(
                        PassThroughAuthenticationHandler.SchemeName, _ => { });
                break;
        }

        services.AddAuthorization(authorization =>
        {
            foreach (string scope in new[]
            {
                "agent.read", "agent.config.read", "agent.config.write", "mcp.config.write",
                "skill.config.write", "capability.test", "conversation.read", "conversation.delete"
            })
            {
                authorization.AddPolicy(scope, policy => policy
                    .RequireAuthenticatedUser()
                    .RequireAssertion(context => HasScope(context.User, scope)));
            }
        });

        return services;
    }

    private static bool HasScope(System.Security.Claims.ClaimsPrincipal principal, string requiredScope)
    {
        if (principal.Identity?.AuthenticationType == PassThroughAuthenticationHandler.SchemeName)
        {
            return true;
        }
        if (principal.IsInRole("Admin")) return true;

        return principal.Claims
            .Where(claim => claim.Type is "scope" or "scp" or "permissions")
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Contains(requiredScope, StringComparer.OrdinalIgnoreCase);
    }
}
