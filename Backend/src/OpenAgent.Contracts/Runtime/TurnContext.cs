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

    /// <summary>
    /// 投影为审计平面关联属性，由 Activity 携带到 OTLP 导出。
    /// </summary>
    /// <remarks>
    /// 审计网关靠这些属性把 trace 关联到具体的会话与用户；缺失时该条 trace
    /// 只能按时间堆在一起，无法回答"这是谁的对话"。空值一律不输出——
    /// 输出空字符串会覆盖调用方已设置的值。
    /// </remarks>
    public IReadOnlyDictionary<string, string> ToAuditAttributes()
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AuditAttributeNames.ConversationId] = ConversationId ?? string.Empty,
            [AuditAttributeNames.UserId] = UserId,
            [AuditAttributeNames.TenantId] = TenantId,
            [AuditAttributeNames.TraceId] = TraceId
        };

        if (!string.IsNullOrWhiteSpace(AgentId))
        {
            attributes[AuditAttributeNames.AgentId] = AgentId;
        }

        return attributes
            .Where(pair => !string.IsNullOrEmpty(pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }
}

/// <summary>
/// 审计平面使用的 Activity 标签名。审计网关（masking gateway）按这些键
/// 归集 trace，改动会破坏审计侧的会话关联，因此集中定义。
/// </summary>
public static class AuditAttributeNames
{
    public const string ConversationId = "openagent.conversation_id";

    public const string UserId = "openagent.user_id";

    public const string AgentId = "openagent.agent_id";

    public const string TenantId = "openagent.tenant_id";

    public const string TraceId = "openagent.trace_id";
}
