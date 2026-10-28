using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Runtime;

namespace OpenAgent.Core.Observability;

internal static class TurnCaptureExtensions
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
}
