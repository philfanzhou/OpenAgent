using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
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
        AgentAuthenticationOptions options = configuration
            .GetSection("Authentication")
            .Get<AgentAuthenticationOptions>() ?? new AgentAuthenticationOptions();
        if (options.Mode != AgentAuthenticationMode.Basic)
        {
            throw new InvalidOperationException(
                $"Unsupported authentication mode '{options.Mode}'. Only Basic is currently supported.");
        }

        services.AddOptions<AgentAuthenticationOptions>()
            .Bind(configuration.GetSection("Authentication"));
        AgentDelegationTokenOptions tokenOptions = configuration
            .GetSection(AgentDelegationTokenOptions.SectionName)
            .Get<AgentDelegationTokenOptions>() ?? new AgentDelegationTokenOptions();
        AuthenticationBuilder authentication = services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = AgentAuthenticationDefaults.SchemeName;
                options.DefaultChallengeScheme = AgentAuthenticationDefaults.SchemeName;
            })
            .AddPolicyScheme(
                AgentAuthenticationDefaults.SchemeName,
                null,
                options => options.ForwardDefaultSelector = context =>
                    context.Request.Headers.Authorization.FirstOrDefault()?.StartsWith(
                        "Bearer ",
                        StringComparison.OrdinalIgnoreCase) == true
                        ? AgentDelegationTokenDefaults.SchemeName
                        : BasicAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, BasicAuthenticationHandler>(
                BasicAuthenticationHandler.SchemeName,
                _ => { });
        if (!string.IsNullOrWhiteSpace(tokenOptions.SigningKey))
        {
            authentication.AddJwtBearer(
                AgentDelegationTokenDefaults.SchemeName,
                options =>
                {
                    AgentDelegationTokenService tokenService = new(
                        Microsoft.Extensions.Options.Options.Create(tokenOptions));
                    options.MapInboundClaims = false;
                    options.TokenValidationParameters = tokenService.CreateValidationParameters();
                });
        }

        // Authentication only establishes an identity for now. Resource and
        // capability authorization will be implemented separately.
        services.AddAuthorization(authorization =>
        {
            authorization.DefaultPolicy = new AuthorizationPolicyBuilder(
                    BasicAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser()
                .Build();
            authorization.AddPolicy(
                AgentDelegationTokenDefaults.PolicyName,
                policy => policy
                    .AddAuthenticationSchemes(AgentDelegationTokenDefaults.SchemeName)
                    .RequireAuthenticatedUser()
                    .RequireClaim(
                        AgentDelegationTokenClaims.Scope,
                        AgentDelegationTokenClaims.ProviderScope));
            foreach (string policyName in new[]
            {
                "agent.read", "agent.config.read", "agent.config.write", "mcp.config.write",
                "skill.config.write", "capability.test", "conversation.read", "conversation.delete"
            })
            {
                authorization.AddPolicy(policyName, policy => policy.RequireAuthenticatedUser());
            }
        });

        return services;
    }
}
