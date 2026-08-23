using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Core.Capabilities;

internal sealed class ApprovalTargetResolver(
    IReadOnlyDictionary<string, ApprovalTarget> targets,
    IReadOnlySet<string> highRiskSkillNames)
{
    internal ApprovalTarget ResolveRequired(ToolApprovalRequestContent request)
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
            if (!string.IsNullOrWhiteSpace(skillName)
                && highRiskSkillNames.Contains(skillName))
            {
                return new ApprovalTarget(
                    AgentResourceType.Skill,
                    skillName,
                    action);
            }
        }

        throw new InvalidOperationException(
            "Approval request does not match a configured high-risk target.");
    }
}
