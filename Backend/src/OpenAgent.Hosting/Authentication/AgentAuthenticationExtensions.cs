using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
        if (options.Mode == AgentAuthenticationMode.JwtBearer
            && (string.IsNullOrWhiteSpace(options.Authority)
                || string.IsNullOrWhiteSpace(options.Audience)))
        {
            throw new InvalidOperationException(
                "JWT Bearer authentication requires Authentication:Authority and Authentication:Audience.");
        }

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
                }),
            AgentAuthenticationMode.Gateway => services
                .AddAuthentication(GatewayAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, GatewayAuthenticationHandler>(
                    GatewayAuthenticationHandler.SchemeName, _ => { }),
            _ => throw new InvalidOperationException(
                $"Unsupported authentication mode '{options.Mode}'.")
        };

        services.AddOptions<AgentAuthenticationOptions>()
            .Bind(configuration.GetSection("Authentication"));
        services.AddAuthorization();
        services.AddSingleton<IAuthorizationPolicyProvider, GatewayAuthorizationPolicyProvider>();
        services.AddSingleton<IAuthorizationHandler, GatewayPermissionAuthorizationHandler>();

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
        services.AddSingleton<IPermissionAuthorizer>(provider =>
            provider.GetRequiredService<GatewayAuthorizationService>());
        services.AddSingleton<IDelegatedPermissionGrantIssuer>(provider =>
            provider.GetRequiredService<GatewayAuthorizationService>());
        return services;
    }
}
