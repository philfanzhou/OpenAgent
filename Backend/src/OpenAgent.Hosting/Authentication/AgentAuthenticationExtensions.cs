using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenAgent.Hosting.Security;
using System.Security.Claims;

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
        services.AddOptions<AgentAuthenticationOptions>()
            .Bind(configuration.GetSection("Authentication"))
            .Validate(
                value => value.Mode != AgentAuthenticationMode.JwtBearer
                    || (!string.IsNullOrWhiteSpace(value.Authority)
                        && !string.IsNullOrWhiteSpace(value.Audience)
                        && !string.IsNullOrWhiteSpace(value.ClientId)),
                "JWT Bearer authentication requires Authority, Audience, and ClientId.")
            .Validate(
                value => value.ClockSkewSeconds is >= 0 and <= 300,
                "Authentication ClockSkewSeconds must be between 0 and 300.")
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AgentAuthenticationOptions>>(provider =>
            new AgentAuthenticationOptionsValidator(provider.GetService<IHostEnvironment>()));

        if (options.Mode == AgentAuthenticationMode.Basic)
        {
            services.AddAuthentication(BasicAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, BasicAuthenticationHandler>(
                    BasicAuthenticationHandler.SchemeName, _ => { });
        }
        else
        {
            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(jwt =>
                {
                    jwt.Authority = options.Authority;
                    jwt.Audience = options.Audience;
                    jwt.RequireHttpsMetadata = options.RequireHttpsMetadata;
                    jwt.MapInboundClaims = false;
                    jwt.SaveToken = false;
                    jwt.TokenValidationParameters = new TokenValidationParameters
                    {
                        NameClaimType = "name",
                        RoleClaimType = "roles",
                        ValidateIssuer = true,
                        ValidateAudience = true,
                        ValidateLifetime = true,
                        ValidateIssuerSigningKey = true,
                        ClockSkew = TimeSpan.FromSeconds(options.ClockSkewSeconds)
                    };
                });
        }

        // Authentication only establishes an identity for now. Resource and
        // capability authorization will be implemented separately.
        services.AddAuthorization(authorization =>
        {
            foreach (string policyName in new[]
            {
                "agent.read", "agent.config.read", "agent.config.write", "mcp.config.write",
                "skill.config.write", "capability.test", "conversation.read", "conversation.delete"
            })
            {
                authorization.AddPolicy(policyName, policy => policy.RequireAuthenticatedUser());
            }
            authorization.AddPolicy("approval.decide", policy => policy.RequireAssertion(context =>
                HasApprovalPermission(context.User)));
        });

        return services;
    }

    private static bool HasApprovalPermission(ClaimsPrincipal user)
    {
        if (user.IsInRole("Admin") || user.IsInRole("ApprovalApprover"))
        {
            return true;
        }

        return user.Claims
            .Where(claim =>
                string.Equals(claim.Type, "scope", StringComparison.OrdinalIgnoreCase)
                || string.Equals(claim.Type, "scp", StringComparison.OrdinalIgnoreCase)
                || string.Equals(claim.Type, "permissions", StringComparison.OrdinalIgnoreCase))
            .SelectMany(claim => claim.Value.Split(
                [' ', ','],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Contains("approval.decide", StringComparer.OrdinalIgnoreCase);
    }
}
