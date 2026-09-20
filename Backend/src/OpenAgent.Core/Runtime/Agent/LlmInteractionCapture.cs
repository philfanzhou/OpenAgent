using OpenAgent.Contracts.Conversation;

namespace OpenAgent.Core.Runtime.Agent;

/// <summary>
/// 一次运行中所有 LLM 调用共享的追溯上下文：TraceId 即轮次键
/// （前端 X-Trace-Id），用于把交互日志与消息、SSE 流对齐。
/// </summary>
internal sealed record LlmInteractionCapture
{
    public required string TenantId { get; init; }
    public required string UserId { get; init; }
    public string? ConversationId { get; init; }
    public required string TraceId { get; init; }
    public string? AgentId { get; init; }
    public LlmInteractionSource Source { get; init; }
}
