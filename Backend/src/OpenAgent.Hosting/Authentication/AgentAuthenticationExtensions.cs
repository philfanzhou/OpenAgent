using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
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
                AddJwtAuthentication(services, options);
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
            case AgentAuthenticationMode.PassThrough:
                if (!options.AllowDevelopmentPassThrough || !IsDevelopment(configuration))
                {
                    throw new InvalidOperationException(
                        "Authentication:Mode=PassThrough is allowed only when "
                        + "Authentication:AllowDevelopmentPassThrough=true in Development.");
                }

                services.AddAuthentication(PassThroughAuthenticationHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, PassThroughAuthenticationHandler>(
                        PassThroughAuthenticationHandler.SchemeName, _ => { });
                break;
            default:
                throw new InvalidOperationException($"Unsupported authentication mode: {options.Mode}");
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

    private static void AddJwtAuthentication(
        IServiceCollection services,
        AgentAuthenticationOptions options)
    {
        List<(string Scheme, AuthenticationProviderOptions Provider)> providers = options.Providers
            .Where(item => !string.IsNullOrWhiteSpace(item.Value.Authority))
            .Select(item => ($"Jwt:{item.Key}", item.Value))
            .ToList();

        if (providers.Count == 0)
        {
            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, jwt => ConfigureJwt(jwt, options, options.Authority, options.Audience, options.RequireHttpsMetadata));
            return;
        }

        AuthenticationBuilder authentication = services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme);
        foreach ((string scheme, AuthenticationProviderOptions provider) in providers)
        {
            authentication.AddJwtBearer(scheme, jwt => ConfigureJwt(
                jwt,
                options,
                provider.Authority,
                string.IsNullOrWhiteSpace(provider.Audience) ? options.Audience : provider.Audience,
                provider.RequireHttpsMetadata));
        }

        authentication.AddPolicyScheme(
            JwtBearerDefaults.AuthenticationScheme,
            "JWT providers",
            policy => policy.ForwardDefaultSelector = context => SelectJwtScheme(context, providers));
    }

    private static void ConfigureJwt(
        JwtBearerOptions jwt,
        AgentAuthenticationOptions root,
        string authority,
        string audience,
        bool requireHttpsMetadata)
    {
        jwt.Authority = authority;
        jwt.Audience = audience;
        jwt.RequireHttpsMetadata = requireHttpsMetadata && root.RequireHttpsMetadata;
        jwt.MapInboundClaims = false;
        jwt.TokenValidationParameters.NameClaimType = "name";
        jwt.TokenValidationParameters.RoleClaimType = "roles";
    }

    private static string SelectJwtScheme(
        HttpContext context,
        IReadOnlyList<(string Scheme, AuthenticationProviderOptions Provider)> providers)
    {
        string? issuer = ReadIssuer(context.Request.Headers.Authorization.FirstOrDefault());
        if (!string.IsNullOrWhiteSpace(issuer))
        {
            (string Scheme, AuthenticationProviderOptions Provider)? match = providers.FirstOrDefault(item =>
                string.Equals(Normalize(item.Provider.Issuer), Normalize(issuer), StringComparison.OrdinalIgnoreCase)
                || string.Equals(Normalize(item.Provider.Authority), Normalize(issuer), StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(match?.Scheme)) return match.Value.Scheme;
        }

        return providers[0].Scheme;
    }

    private static string? ReadIssuer(string? authorization)
    {
        if (string.IsNullOrWhiteSpace(authorization)
            || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;

        string[] parts = authorization[7..].Split('.');
        if (parts.Length < 2) return null;
        try
        {
            string payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
            using JsonDocument document = JsonDocument.Parse(Convert.FromBase64String(payload));
            return document.RootElement.TryGetProperty("iss", out JsonElement issuer)
                ? issuer.GetString()
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsDevelopment(IConfiguration configuration)
    {
        string environment = configuration["DOTNET_ENVIRONMENT"]
            ?? configuration["ASPNETCORE_ENVIRONMENT"]
            ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? "Production";
        return environment.Equals("Development", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string? value) => value?.TrimEnd('/') ?? string.Empty;

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
