using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
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
        });

        return services;
    }

    private static IServiceCollection AddGatewayAuthorization(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<GatewayAuthorizationOptions>()
            .Bind(configuration.GetSection(GatewayAuthorizationOptions.SectionName))
            .Validate(
                options => options.SigningKey?.Length >= 32,
                "GatewayAuthorization:SigningKey must contain at least 32 characters")
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.Issuer)
                    && !string.IsNullOrWhiteSpace(options.Audience)
                    && options.GrantLifetimeSeconds is >= 10 and <= 300
                    && options.ClockSkewSeconds is >= 0 and <= 60
                    && options.MaxGrantCharacters is >= 1_024 and <= 65_536,
                "Gateway authorization issuer, audience and lifetime settings are invalid")
            .ValidateOnStart();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<GatewayGrantCodec>();
        services.AddSingleton<IGatewayAuthorizationService, GatewayAuthorizationService>();
        return services;
    }
}
