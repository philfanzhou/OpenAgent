using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAgent.Core.Capabilities.Skill;
using Xunit;

namespace OpenAgent.Core.Tests.Capabilities;

public sealed class AgentSkillsApprovalTests
{
    [Theory]
    [InlineData(AgentSkillsProvider.LoadSkillToolName)]
    [InlineData(AgentSkillsProvider.ReadSkillResourceToolName)]
    [InlineData(AgentSkillsProvider.RunSkillScriptToolName)]
    public void ShouldAutoApprove_OnlyLowRiskSkills(string toolName)
    {
        var highRisk = new HashSet<string>(StringComparer.Ordinal) { "production" };

        Assert.False(AgentSkillsRuntime.ShouldAutoApprove(
            Call(toolName, "production"),
            highRisk));
        Assert.True(AgentSkillsRuntime.ShouldAutoApprove(
            Call(toolName, "documentation"),
            highRisk));
    }

    [Fact]
    public void ShouldAutoApprove_NonSkillFunction_RemainsSubjectToItsOwnPolicy()
    {
        Assert.False(AgentSkillsRuntime.ShouldAutoApprove(
            Call("ordinary_function", "documentation"),
            new HashSet<string>()));
    }

    private static FunctionCallContent Call(string toolName, string skillName) => new(
        "call-1",
        toolName,
        new Dictionary<string, object?> { ["skillName"] = skillName });
}
