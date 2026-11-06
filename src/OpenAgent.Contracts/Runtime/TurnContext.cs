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
    /// 审计关联属性：外部审计/观测平面（OpenAgent.Audit 的 Masking Gateway）
    /// 依赖这些键把 trace 归集到会话、用户与 Agent。
    /// 任何新增的会话级标识都应先加进本投影，再由各信号统一输出。
    /// </summary>
    public IReadOnlyDictionary<string, string?> ToAuditAttributes()
    {
        var attributes = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [AuditAttributeNames.ConversationId] = ConversationId,
            [AuditAttributeNames.UserId] = UserId,
            [AuditAttributeNames.AgentId] = AgentId,
            [AuditAttributeNames.TenantId] = string.IsNullOrWhiteSpace(TenantId) ? null : TenantId,
            [AuditAttributeNames.TraceId] = TraceId
        };

        return attributes;
    }
}

/// <summary>
/// OpenAgent 对外输出的审计关联属性键。取值与 OpenAgent.Audit 网关的
/// 默认映射一致（docs/openagent-integration.md §2.0）：网关据此写入
/// Langfuse 的 session id / user id / agent tag，密文行也以该会话维度做保留期删除。
/// </summary>
public static class AuditAttributeNames
{
    public const string ConversationId = "openagent.conversation_id";
    public const string UserId = "openagent.user_id";
    public const string AgentId = "openagent.agent_id";
    public const string TenantId = "openagent.tenant_id";
    public const string TraceId = "openagent.trace_id";
}
