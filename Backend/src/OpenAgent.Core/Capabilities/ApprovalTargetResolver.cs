using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Core.Capabilities;

internal sealed class ApprovalTargetResolver(
    IReadOnlyDictionary<string, ApprovalTarget> targets,
    IReadOnlySet<string> highRiskSkillNames)
{
    internal ApprovalTarget Resolve(ToolApprovalRequestContent request)
    {
        FunctionCallContent? call = request.ToolCall as FunctionCallContent;
        if (call != null && targets.TryGetValue(call.Name, out ApprovalTarget? target))
        {
            return target;
        }

        if (call != null && call.Name is (
            AgentSkillsProvider.LoadSkillToolName
            or AgentSkillsProvider.ReadSkillResourceToolName
            or AgentSkillsProvider.RunSkillScriptToolName))
        {
            string? skillName = call.Arguments?.TryGetValue("skillName", out object? value) == true
                ? value?.ToString()
                : null;
            string action = call.Name switch
            {
                AgentSkillsProvider.LoadSkillToolName => "load",
                AgentSkillsProvider.ReadSkillResourceToolName => "read",
                _ => "execute"
            };
            return new ApprovalTarget(
                AgentResourceType.Skill,
                !string.IsNullOrWhiteSpace(skillName) && highRiskSkillNames.Contains(skillName)
                    ? skillName
                    : call.Name,
                action);
        }

        return new ApprovalTarget(
            AgentResourceType.Function,
            call?.Name ?? request.ToolCall.CallId,
            "invoke");
    }
}
