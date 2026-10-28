namespace OpenAgent.Contracts.Conversation;

public enum LlmInteractionSource
{
    /// <summary>Agent 对话轮次内的一次模型调用（含工具循环的多次迭代）。</summary>
    AgentTurn = 0,

    /// <summary>上下文压缩摘要调用（自动或手动触发）。</summary>
    Compaction = 1
}

public enum LlmInteractionStatus
{
    Succeeded = 0,
    Failed = 1,
    Cancelled = 2
}

/// <summary>
/// 一次大模型交互的完整请求/响应日志。TraceId 即轮次键：同一轮对话内
/// （含工具循环与自动压缩）的所有交互共享前端请求携带的 X-Trace-Id。
/// 载荷已脱敏：不含 API Key，二进制内容仅记录占位符。
/// </summary>
public sealed class LlmInteractionRecord
{
    public required string InteractionId { get; init; }
    public required string TenantId { get; init; }
    public required string UserId { get; init; }
    public string? ConversationId { get; init; }
    public required string TraceId { get; init; }
    public string? AgentId { get; init; }
    public LlmInteractionSource Source { get; init; }
    public string? Provider { get; init; }
    public string? ApiFormat { get; init; }
    public required string ModelId { get; init; }
    public bool Streamed { get; init; }
    /// <summary>同一轮内的调用序号，从 0 开始。</summary>
    public int CallIndex { get; init; }

    /// <summary>脱敏后的请求载荷 JSON（messages + options）。</summary>
    public string? RequestJson { get; init; }

    /// <summary>脱敏后的响应载荷 JSON（contents + usage）；失败时可能为空。</summary>
    public string? ResponseJson { get; init; }

    public Requests.TokenUsage? TokenUsage { get; init; }
    public LlmInteractionStatus Status { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public int DurationMs { get; init; }
}
