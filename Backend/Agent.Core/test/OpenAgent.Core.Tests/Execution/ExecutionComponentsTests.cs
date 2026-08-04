using OpenAgent.Contracts.Engine;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Core.Execution.Resolvers;
using OpenAgent.Core.Execution;
using OpenAgent.Core.Execution.Tools;
using Xunit;

namespace OpenAgent.Core.Tests.Execution;

public class ExecutionComponentsTests
{
    [Fact]
    public void ExecutionTypes_AreTopLevelTypes()
    {
        Assert.Null(typeof(OpenAgent.Core.Execution.ExecutionContext).DeclaringType);
        Assert.Null(typeof(ExecutionEvent).DeclaringType);
        Assert.Null(typeof(AssistantContentChunk).DeclaringType);
        Assert.Null(typeof(TerminalError).DeclaringType);
    }

    [Fact]
    public void UserContextBuilder_MapsIdentityCollectionsAndClaims()
    {
        var context = new Dictionary<string, object>
        {
            ["UserId"] = "user-1",
            ["TenantId"] = "tenant-1",
            ["Groups"] = new List<object> { "group-a", "group-b" },
            ["Roles"] = new List<string> { "admin" },
            ["Claims"] = new Dictionary<string, string> { ["region"] = "east" },
            ["Audience"] = new List<string> { "agent" }
        };

        var result = new UserContextBuilder().Build(context);

        Assert.Equal("user-1", result.UserId);
        Assert.Equal("tenant-1", result.TenantId);
        Assert.Equal(["group-a", "group-b"], result.Groups);
        Assert.Equal(["admin"], result.Roles);
        Assert.Equal("east", result.Claims["region"]);
        Assert.Equal(["agent"], result.Audience);
        Assert.True(result.IsAuthenticated);
    }

}
