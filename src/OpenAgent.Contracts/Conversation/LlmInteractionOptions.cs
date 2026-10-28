namespace OpenAgent.Contracts.Conversation;

/// <summary>
/// 大模型交互日志配置。日志记录失败只降级不阻断；
/// MaxContentLength 限制单个载荷字段的存储长度，超长截断并标注。
/// </summary>
public sealed class LlmInteractionOptions
{
    public const string SectionName = "LlmInteraction";

    public bool Enabled { get; set; } = true;

    /// <summary>单字段（正文/参数/结果）最大保留字符数，0 表示不限制。</summary>
    public int MaxContentLength { get; set; } = 100_000;
}
