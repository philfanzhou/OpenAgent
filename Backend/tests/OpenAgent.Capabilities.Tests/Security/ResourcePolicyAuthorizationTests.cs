using System.Diagnostics;
using Microsoft.Extensions.Options;
using OpenAgent.Capabilities.Security;
using OpenAgent.Capabilities.Tests.TestDoubles;
using OpenAgent.Contracts.Security;
using Xunit;

namespace OpenAgent.Capabilities.Tests.Security;

public sealed class ResourcePolicyAuthorizationTests
{
    private static readonly AgentAuthorizationRequest Request = new("agent", AgentResourceType.Mcp, "server", "use");
    private static AgentUserContext User(string tenant = "tenant", bool admin = false, bool scope = true) => new()
    {
        UserId = "alice", TenantId = tenant, Roles = admin ? ["Admin"] : ["Member"],
        Claims = scope ? new Dictionary<string, string> { ["scope"] = "mcp.use" } : new Dictionary<string, string>()
    };
    private static ResourcePolicyAuthorizationService Service(PolicyStore store, bool strict = false) => new(store,
        Options.Create(new AgentAuthorizationOptions { Mode = AgentAuthorizationMode.Claims, RequireResourcePolicy = strict }));

    [Theory]
    [InlineData(false, false, true, true)]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, false, true)]
    [InlineData(true, true, true, false)]
    [InlineData(true, false, true, false)]
    public async Task Authorize_MissingPolicy_UsesExplicitMigrationMode(bool strict, bool admin, bool scope, bool expected)
    {
        Assert.Equal(expected, await Service(new(), strict).IsAuthorizedAsync(Request, User(admin: admin, scope: scope)));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Authorize_ExplicitDeny_OverridesAllowAndAdmin(bool deny, bool expected)
    {
        PolicyStore store = new() { Snapshot = new(true, "v1", new() { Rules =
        [new() { Actions = ["*"] }, new() { Actions = ["use"], UserIds = ["alice"], Deny = deny }] }) };
        Assert.Equal(expected, await Service(store).IsAuthorizedAsync(Request, User(admin: true)));
    }

    [Theory]
    [InlineData("alice", "", "use", true)]
    [InlineData("Alice", "", "use", false)]
    [InlineData("bob", "member", "use", true)]
    [InlineData("bob", "other", "use", false)]
    [InlineData("alice", "", "invoke", false)]
    public async Task Authorize_SubjectAndAction_MatchPolicy(string id, string role, string action, bool expected)
    {
        PolicyStore store = new() { Snapshot = new(true, "v1", new() { Rules =
        [new() { Actions = [action], UserIds = [id], Roles = role.Length == 0 ? [] : [role] }] }) };
        Assert.Equal(expected, await Service(store, true).IsAuthorizedAsync(Request, User()));
        Assert.False(await Service(store, true).IsAuthorizedAsync(Request, User(scope: false)));
    }

    [Theory]
    [InlineData("other", true)]
    [InlineData("", true)]
    [InlineData("tenant", false)]
    public async Task Authorize_MissingTenantOrResource_DeniesAdmin(string tenant, bool exists)
    {
        PolicyStore store = new() { Snapshot = new(exists, "v1", null) };
        Assert.False(await Service(store).IsAuthorizedAsync(Request, User(tenant, admin: true)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Revalidate_PolicyRevokedOrConfigurationRotated_CancelsActiveLease(bool rotate)
    {
        PolicyStore store = new();
        var authorization = Service(store);
        Assert.True(await authorization.IsAuthorizedAsync(Request, User()));
        await using AuthorizationLease lease = new(authorization.RevalidateAsync, default, TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(1));
        Stopwatch elapsed = Stopwatch.StartNew();
        store.Snapshot = rotate ? new(true, "config-2", null) : new(true, "config-1", new() { Revision = 2, Rules = [] });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Delay(TimeSpan.FromSeconds(2), lease.Token));
        Assert.InRange(elapsed.ElapsedMilliseconds, 0, 1500);
        Assert.Throws<AgentException>(lease.ThrowIfRevoked);
        Assert.True(store.Reads >= 2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Revalidate_AuthorityUnavailableOrHung_FailsClosed(bool hang)
    {
        async Task<bool> Read(CancellationToken ct)
        {
            if (hang) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("storage unavailable");
        }
        await using AuthorizationLease lease = new(Read, default, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Delay(TimeSpan.FromSeconds(2), lease.Token));
        Assert.Throws<AgentException>(lease.ThrowIfRevoked);
    }

    [Fact]
    public async Task Authorize_DiscoveryThenRevocation_InvocationReadsAuthorityAgain()
    {
        PolicyStore store = new();
        var authorization = Service(store);
        Assert.True(await authorization.IsAuthorizedAsync(Request, User()));
        store.Snapshot = new(true, "config-1", new() { Revision = 1, Rules = [] });
        Assert.False(await authorization.IsAuthorizedAsync(Request, User()));
        Assert.Equal(2, store.Reads);
    }
}
