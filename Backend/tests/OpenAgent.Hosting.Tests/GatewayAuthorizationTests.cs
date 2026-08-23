using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenAgent.Authorization;
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
    public void GrantIssuer_WithoutConfiguredKey_FailsClosed()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Mode"] = "Basic"
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
        IDelegatedAuthorizationIssuer issuer = provider
            .GetRequiredService<IDelegatedAuthorizationIssuer>();
        AuthorizationSubject subject = new("user-1", "tenant-1", [], [],
            new Dictionary<string, string>());

        Assert.Throws<InvalidOperationException>(() => issuer.Issue(
            DelegatedAuthorization.Create(subject, ["agent.read"])));
    }

    [Fact]
    public void Authorization_CombinesDefaultsRolesAndResourceClaims()
    {
        using ServiceProvider provider = CreateProvider();
        IPermissionAuthorizationService authorization = provider
            .GetRequiredService<IPermissionAuthorizationService>();
        AuthorizationSubject subject = new(
            "user-1",
            "tenant-1",
            ["Operator"],
            [],
            new Dictionary<string, string>
            {
                ["scope"] = "agent.execute:finance"
            });

        Assert.True(authorization.Authorize(new(subject, "agent.read")).IsAllowed);
        Assert.True(authorization.Authorize(new(subject, "conversation.read")).IsAllowed);
        Assert.True(authorization.Authorize(new(subject, "agent.execute", "finance")).IsAllowed);
        Assert.False(authorization.Authorize(new(subject, "agent.execute", "support")).IsAllowed);
        Assert.False(authorization.Authorize(new(subject, "agent.config.write")).IsAllowed);
    }

    [Fact]
    public async Task AgentExecutePolicy_ScopedGrantPassesCoarseEndpointCheck()
    {
        using ServiceProvider provider = CreateProvider();
        Microsoft.AspNetCore.Authorization.IAuthorizationService authorization = provider
            .GetRequiredService<Microsoft.AspNetCore.Authorization.IAuthorizationService>();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "intent-router"),
            new Claim(PermissionClaimTypes.Permission, "agent.execute:intent-router")
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
        IDelegatedAuthorizationIssuer authorization = provider
            .GetRequiredService<IDelegatedAuthorizationIssuer>();
        GatewayGrantCodec codec = provider.GetRequiredService<GatewayGrantCodec>();
        AuthorizationSubject subject = new(
            "user-1",
            "tenant-1",
            [],
            [],
            new Dictionary<string, string>());

        string grant = authorization.Issue(DelegatedAuthorization.Create(subject, ["agent.read"]));

        Assert.True(codec.TryDecode(grant, "openagent-engine", out GatewayGrantPayload? payload));
        Assert.Equal("user-1", payload?.Subject);
        Assert.False(codec.TryDecode(grant, "external:partner", out _));
        Assert.False(codec.TryDecode(grant + "tampered", "openagent-engine", out _));

        string restrictedGrant = authorization.Issue(DelegatedAuthorization.Restrict(
            subject,
            ["agent.execute:intent-router", "model.invoke"],
            ["agent.execute:intent-router", "model.invoke"]));
        Assert.True(codec.TryDecode(
            restrictedGrant,
            "openagent-engine",
            out GatewayGrantPayload? restrictedPayload));
        Assert.Equal(
            ["agent.execute:intent-router", "model.invoke"],
            restrictedPayload?.Permissions);
        Assert.DoesNotContain("agent.read", restrictedPayload?.Permissions ?? []);

        string externalGrant = authorization.Issue(DelegatedAuthorization.Restrict(
            subject,
            ["agent.execute:support-v2"],
            ["agent.execute:support-v2"],
            "external-partner"));
        Assert.True(codec.TryDecode(
            externalGrant,
            "external-partner",
            out GatewayGrantPayload? externalPayload));
        Assert.Equal(["agent.execute:support-v2"], externalPayload?.Permissions);
        Assert.False(codec.TryDecode(externalGrant, "openagent-engine", out _));

        using ServiceProvider attackerProvider = CreateProvider(
            signingKey: PartnerSigningKey,
            includePartnerKey: false);
        IDelegatedAuthorizationIssuer attacker = attackerProvider
            .GetRequiredService<IDelegatedAuthorizationIssuer>();
        string forgedEngineGrant = attacker.Issue(DelegatedAuthorization.Create(subject, ["*"]));
        Assert.False(codec.TryDecode(forgedEngineGrant, "openagent-engine", out _));

        time.Advance(TimeSpan.FromMinutes(2));
        Assert.False(codec.TryDecode(grant, "openagent-engine", out _));
    }

    [Fact]
    public void Delegation_RejectsPermissionsOutsideTheAuthorizedSet()
    {
        AuthorizationSubject subject = new(
            "user-1",
            "tenant-1",
            [],
            [],
            new Dictionary<string, string>());

        Assert.Throws<UnauthorizedAccessException>(() => DelegatedAuthorization.Restrict(
            subject,
            ["agent.execute:finance"],
            ["agent.execute:support"]));
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
