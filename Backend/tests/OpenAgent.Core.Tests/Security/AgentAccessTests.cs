using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;
using Xunit;

namespace OpenAgent.Core.Tests.Security;

public sealed class AgentAccessTests
{
    private static readonly AgentUserContext User = new()
    {
        UserId = "u1",
        Groups = ["g1"],
        Roles = ["r1"]
    };

    [Fact]
    public void EmptyAllowLists_AllowEveryone() =>
        Assert.True(AgentAccess.IsAllowed(new AgentConfig(), User));

    [Theory]
    [InlineData("u1", null, null, true)]
    [InlineData(null, "g1", null, true)]
    [InlineData(null, null, "r1", true)]
    [InlineData("u2", "g2", "r2", false)]
    [InlineData("u2", null, null, false)]
    public void AllowLists_AnySingleMatchGrantsAccess(string? user, string? group, string? role, bool expected)
    {
        AgentConfig config = new();
        if (user != null) config.AllowedUserIds.Add(user);
        if (group != null) config.AllowedGroups.Add(group);
        if (role != null) config.AllowedRoles.Add(role);

        Assert.Equal(expected, AgentAccess.IsAllowed(config, User));
    }
}
