using System.Text.Json;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;
using OpenAgent.Engine.Config;
using Xunit;

namespace OpenAgent.Engine.Tests.Config;

public class AgentAclEntryProjectionTests
{
    [Fact]
    public void EmptyAllowLists_ProduceNoEntry() =>
        Assert.Null(AgentConfigManagementService.BuildAclEntry("agent-1", new AgentConfig()));

    [Fact]
    public void AllowLists_ProduceEntryTheRouterCanReadWithDefaultJsonOptions()
    {
        AgentAclEntry? entry = AgentConfigManagementService.BuildAclEntry(
            "agent-1",
            new AgentConfig { AllowedGroups = ["g1"], AllowedRoles = ["r1"] });

        AgentAclEntry? roundTripped = JsonSerializer.Deserialize<AgentAclEntry>(JsonSerializer.Serialize(entry));

        Assert.Equal("agent-1", roundTripped!.AgentId);
        Assert.Equal(["g1"], roundTripped.AllowedGroups);
        Assert.Equal(["r1"], roundTripped.AllowedRoles);
        Assert.Empty(roundTripped.AllowedUserIds);
    }
}
