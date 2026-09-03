using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using System.IdentityModel.Tokens.Jwt;
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

        string primaryScheme = options.Mode == AgentAuthenticationMode.Basic
            ? BasicAuthenticationHandler.SchemeName
            : JwtBearerDefaults.AuthenticationScheme;
        string defaultScheme = options.EnableApiKey ? "Agent" : primaryScheme;
        AuthenticationBuilder authentication = services.AddAuthentication(defaultScheme);
        if (options.Mode == AgentAuthenticationMode.Basic)
        {
            authentication
                .AddScheme<AuthenticationSchemeOptions, BasicAuthenticationHandler>(
                    BasicAuthenticationHandler.SchemeName, _ => { });
        }
        else
        {
            authentication
                .AddJwtBearer(jwt =>
                {
                    jwt.Authority = options.Authority;
                    if (!string.IsNullOrWhiteSpace(options.MetadataAddress))
                    {
                        jwt.MetadataAddress = options.MetadataAddress;
                    }
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

        var authenticationSchemes = new List<string> { primaryScheme };
        if (options.EnableApiKey)
        {
            authentication
                .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
                    ApiKeyAuthenticationHandler.SchemeName, _ => { });
            authentication.AddPolicyScheme("Agent", null, policy =>
            {
                policy.ForwardDefaultSelector = context =>
                {
                    string? authorization = context.Request.Headers.Authorization.FirstOrDefault();
                    if (authorization?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        string token = authorization["Bearer ".Length..].Trim();
                        return options.Mode == AgentAuthenticationMode.JwtBearer
                            && new JwtSecurityTokenHandler().CanReadToken(token)
                            ? JwtBearerDefaults.AuthenticationScheme
                            : ApiKeyAuthenticationHandler.SchemeName;
                    }

                    return primaryScheme;
                };
            });
            authenticationSchemes.Add(ApiKeyAuthenticationHandler.SchemeName);
        }

        // Authentication only establishes an identity for now. Resource and
        // capability authorization will be implemented separately.
        services.AddAuthorization(authorization =>
        {
            AuthorizationPolicy defaultPolicy = new AuthorizationPolicyBuilder(authenticationSchemes.ToArray())
                .RequireAuthenticatedUser()
                .Build();
            authorization.DefaultPolicy = defaultPolicy;

            foreach (string policyName in new[]
            {
                "agent.read", "agent.config.read", "agent.config.write", "mcp.config.write",
                "skill.config.write", "capability.test", "conversation.read", "conversation.delete"
            })
            {
                authorization.AddPolicy(policyName, policy =>
                {
                    policy.AddAuthenticationSchemes(authenticationSchemes.ToArray());
                    policy.RequireAuthenticatedUser();
                });
            }
        });

        return services;
    }
}
