using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Core.Conversation;

namespace OpenAgent.Core.Runtime.Agent;

/// <summary>
/// TurnContext 的 Core 侧投影：LLM 交互日志捕获与会话上下文。
/// 目标类型为 Core internal，无法作为 Contracts 成员，故以扩展方法收敛派生点。
/// </summary>
internal static class TurnContextProjections
{
    internal static LlmInteractionCapture ToCapture(this TurnContext turn, LlmInteractionSource source) => new()
    {
        TenantId = turn.TenantId,
        UserId = turn.UserId,
        ConversationId = turn.ConversationId,
        TraceId = turn.TraceId,
        AgentId = turn.AgentId,
        Source = source
    };

    /// <summary>
    /// 会话上下文要求非空 Type；主链路总是携带请求的 ConversationType，
    /// 缺省回落 User，与 AgentRequest 的默认值一致。
    /// </summary>
    internal static ConversationContext ToConversationContext(this TurnContext turn) => new(
        turn.ConversationId,
        turn.TenantId,
        turn.UserId,
        turn.AgentId,
        turn.TraceId,
        turn.ConversationType ?? ConversationType.User);
}
