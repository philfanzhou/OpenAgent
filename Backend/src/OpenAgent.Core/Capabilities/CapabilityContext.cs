using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Core.Capabilities;

/// <summary>
/// 能力发现所需的只读窄视图：只暴露能力相关小节，替代把整只 AgentConfig
/// 递给每个 <see cref="ICapabilitySource"/>。给某节加字段不再波及能力源；
/// 需要新小节时在此显式加节（review 可见），横切身份信息优先走
/// <see cref="IAgentUserContext"/> 或 TurnContext，而不是扩充本类型。
/// </summary>
internal sealed record CapabilityContext(
    string AgentId,
    string TenantId,
    McpConfig Mcp,
    RagConfig Rag,
    SkillsConfig Skills,
    CodeExecutionConfig CodeExecution)
{
    /// <summary>
    /// 从运行时投影构造。TenantId 与 TurnContext 同语义：取自用户上下文并
    /// 归一化为非空字符串（缺失即空串，由消费方决定空租户是否短路）。
    /// </summary>
    public static CapabilityContext From(AgentRuntimeProfile profile, IAgentUserContext user) => new(
        profile.AgentId,
        user.TenantId ?? string.Empty,
        profile.Config.Mcp,
        profile.Config.Rag,
        profile.Config.Skills,
        profile.Config.CodeExecution);
}
