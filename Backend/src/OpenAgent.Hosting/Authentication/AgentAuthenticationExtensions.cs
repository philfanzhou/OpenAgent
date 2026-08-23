using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenAgent.Authorization;
using OpenAgent.Hosting.Authorization;
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

        services.AddGatewayAuthorization(configuration);

        _ = options.Mode switch
        {
            AgentAuthenticationMode.Basic => services
                .AddAuthentication(BasicAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, BasicAuthenticationHandler>(
                    BasicAuthenticationHandler.SchemeName, _ => { }),
            AgentAuthenticationMode.JwtBearer => services
                .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
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
                }),
            AgentAuthenticationMode.Gateway => services
                .AddAuthentication(GatewayAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, GatewayAuthenticationHandler>(
                    GatewayAuthenticationHandler.SchemeName, _ => { }),
            _ => throw new InvalidOperationException(
                $"Unsupported authentication mode '{options.Mode}'.")
        };

        services.AddAuthorization();
        services.AddSingleton<IAuthorizationPolicyProvider, GatewayAuthorizationPolicyProvider>();
        services.AddSingleton<IAuthorizationHandler, GatewayPermissionAuthorizationHandler>();

        return services;
    }

    private static IServiceCollection AddGatewayAuthorization(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        bool gatewayConfigured = configuration
            .GetSection(GatewayAuthorizationOptions.SectionName)
            .Exists();
        services.AddOptions<GatewayAuthorizationOptions>()
            .Bind(configuration.GetSection(GatewayAuthorizationOptions.SectionName))
            .Validate(
                options => !gatewayConfigured || options.SigningKey?.Length >= 32,
                "GatewayAuthorization:SigningKey must contain at least 32 characters")
            .Validate(
                options => options.AudienceSigningKeys.All(item =>
                    !string.IsNullOrWhiteSpace(item.Key)
                    && item.Value?.Length >= 32
                    && !item.Key.Equals(options.Audience, StringComparison.Ordinal)
                    && !item.Value.Equals(options.SigningKey, StringComparison.Ordinal))
                && options.AudienceSigningKeys.Values
                    .Distinct(StringComparer.Ordinal)
                    .Count() == options.AudienceSigningKeys.Count,
                "GatewayAuthorization:AudienceSigningKeys require distinct non-default audiences and distinct keys of at least 32 characters")
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
        services.AddSingleton<GatewayAuthorizationService>();
        services.AddSingleton<IPermissionAuthorizationService>(provider =>
            provider.GetRequiredService<GatewayAuthorizationService>());
        services.AddSingleton<IDelegatedAuthorizationIssuer>(provider =>
            provider.GetRequiredService<GatewayAuthorizationService>());
        return services;
    }
}
