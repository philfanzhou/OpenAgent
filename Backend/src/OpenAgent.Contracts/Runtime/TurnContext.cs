using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Files;

namespace OpenAgent.Contracts.Runtime;

/// <summary>
/// 一次 agent 轮次的已解析身份坐标。由执行器在轮次开始时构造一次，
/// 各子系统（LLM 交互日志、文件资产、会话存储）从它派生自己的 scope，
/// 不再各自拼装并重复归一化。新轮次级横切字段应加在这里，投影只改一处。
/// </summary>
public sealed record TurnContext
{
    /// <summary>已归一化的租户标识，绝不为 null。</summary>
    public required string TenantId { get; init; }

    public required string UserId { get; init; }

    public string? ConversationId { get; init; }

    /// <summary>已解析的轮次追溯键（前端 X-Trace-Id），绝不为空。</summary>
    public required string TraceId { get; init; }

    public string? AgentId { get; init; }

    public ConversationType? ConversationType { get; init; }

    /// <summary>投影为文件资产作用域（租户/用户/会话三元组）。</summary>
    public FileAssetScope ToFileAssetScope() => new()
    {
        TenantId = TenantId,
        UserId = UserId,
        ConversationId = ConversationId
    };
}
