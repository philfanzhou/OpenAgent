using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenAgent.Hosting.Authorization;
using Xunit;

namespace OpenAgent.Hosting.Tests;

public class GatewayAuthorizationTests
{
    private const string EngineSigningKey = "engine-signing-key-with-at-least-32-characters";
    private const string PartnerSigningKey = "partner-signing-key-with-at-least-32-characters";

    [Fact]
    public void Configuration_RejectsWeakSigningKey()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Mode"] = "Basic",
                ["GatewayAuthorization:SigningKey"] = "too-short"
            })
            .Build();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddAgentHost(configuration, options =>
        {
            options.EnableCors = false;
            options.EnableSwagger = false;
            options.EnableHealthChecks = false;
            options.EnableOpenTelemetry = false;
        });
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<GatewayAuthorizationOptions>>().Value);
    }

    [Fact]
    public void Authorization_CombinesDefaultsRolesAndResourceClaims()
    {
        using ServiceProvider provider = CreateProvider();
        IGatewayAuthorizationService authorization = provider
            .GetRequiredService<IGatewayAuthorizationService>();
        GatewayIdentity identity = new(
            "user-1",
            "tenant-1",
            ["Operator"],
            [],
            new Dictionary<string, string>
            {
                ["scope"] = "agent.execute:finance"
            },
            IsAuthenticated: true);

        Assert.True(authorization.IsAuthorized(identity, "agent.read"));
        Assert.True(authorization.IsAuthorized(identity, "conversation.read"));
        Assert.True(authorization.IsAuthorized(identity, "agent.execute", "finance"));
        Assert.False(authorization.IsAuthorized(identity, "agent.execute", "support"));
        Assert.False(authorization.IsAuthorized(identity, "agent.config.write"));
    }

    [Fact]
    public async Task AgentExecutePolicy_ScopedGrantPassesCoarseEndpointCheck()
    {
        using ServiceProvider provider = CreateProvider();
        IAuthorizationService authorization = provider.GetRequiredService<IAuthorizationService>();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "intent-router"),
            new Claim(GatewayAuthorizationDefaults.PermissionClaimType, "agent.execute:intent-router")
        ], "test"));

        AuthorizationResult result = await authorization.AuthorizeAsync(
            principal,
            resource: null,
            "agent.execute");

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Grant_IsSignedShortLivedAndAudienceBound()
    {
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 8, 8, 0, 0, 0, TimeSpan.Zero));
        using ServiceProvider provider = CreateProvider(time);
        IGatewayAuthorizationService authorization = provider
            .GetRequiredService<IGatewayAuthorizationService>();
        GatewayGrantCodec codec = provider.GetRequiredService<GatewayGrantCodec>();
        GatewayIdentity identity = new(
            "user-1",
            "tenant-1",
            [],
            [],
            new Dictionary<string, string>(),
            IsAuthenticated: true);

        string grant = authorization.IssueGrant(identity);

        Assert.True(codec.TryDecode(grant, "openagent-engine", out GatewayGrantPayload? payload));
        Assert.Equal("user-1", payload?.Subject);
        Assert.False(codec.TryDecode(grant, "external:partner", out _));
        Assert.False(codec.TryDecode(grant + "tampered", "openagent-engine", out _));

        string restrictedGrant = authorization.IssueRestrictedGrant(
            identity,
            ["agent.execute:intent-router", "model.invoke"]);
        Assert.True(codec.TryDecode(
            restrictedGrant,
            "openagent-engine",
            out GatewayGrantPayload? restrictedPayload));
        Assert.Equal(
            ["agent.execute:intent-router", "model.invoke"],
            restrictedPayload?.Permissions);
        Assert.DoesNotContain("agent.read", restrictedPayload?.Permissions ?? []);

        string externalGrant = authorization.IssueRestrictedGrant(
            identity,
            ["agent.execute:support-v2"],
            "external-partner");
        Assert.True(codec.TryDecode(
            externalGrant,
            "external-partner",
            out GatewayGrantPayload? externalPayload));
        Assert.Equal(["agent.execute:support-v2"], externalPayload?.Permissions);
        Assert.False(codec.TryDecode(externalGrant, "openagent-engine", out _));

        using ServiceProvider attackerProvider = CreateProvider(
            signingKey: PartnerSigningKey,
            includePartnerKey: false);
        IGatewayAuthorizationService attacker = attackerProvider
            .GetRequiredService<IGatewayAuthorizationService>();
        string forgedEngineGrant = attacker.IssueRestrictedGrant(identity, ["*"]);
        Assert.False(codec.TryDecode(forgedEngineGrant, "openagent-engine", out _));

        time.Advance(TimeSpan.FromMinutes(2));
        Assert.False(codec.TryDecode(grant, "openagent-engine", out _));
    }

    private static ServiceProvider CreateProvider(
        TimeProvider? timeProvider = null,
        string signingKey = EngineSigningKey,
        bool includePartnerKey = true)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Authentication:Mode"] = "Basic",
            ["GatewayAuthorization:Issuer"] = "openagent-router",
            ["GatewayAuthorization:Audience"] = "openagent-engine",
            ["GatewayAuthorization:SigningKey"] = signingKey,
            ["GatewayAuthorization:GrantLifetimeSeconds"] = "60",
            ["GatewayAuthorization:ClockSkewSeconds"] = "0",
            ["GatewayAuthorization:AuthenticatedPermissions:0"] = "agent.read",
            ["GatewayAuthorization:RolePermissions:Operator:0"] = "conversation.read"
        };
        if (includePartnerKey)
        {
            settings["GatewayAuthorization:AudienceSigningKeys:external-partner"] =
                PartnerSigningKey;
        }

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddAgentHost(configuration, options =>
        {
            options.EnableCors = false;
            options.EnableSwagger = false;
            options.EnableHealthChecks = false;
            options.EnableOpenTelemetry = false;
        });
        if (timeProvider != null)
        {
            services.AddSingleton(timeProvider);
        }

        return services.BuildServiceProvider();
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        internal void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
