namespace OpenAgent.Contracts.Conversation;

public sealed class ConversationStoreOptions
{
    public const string SectionName = "ConversationStore";

    /// <summary>
    /// 执行侧历史消息窗口大小（最近 N 条）。
    /// </summary>
    /// <summary>
    /// 临时的模型上下文长度回退值。模型元数据尚未提供上下文窗口时使用，默认 1000 token 便于测试。
    /// </summary>
    // TODO: Replace this fallback with the context window reported by the selected model.
    public int DefaultModelContextTokens { get; set; } = 1_000;

    /// <summary>
    /// 会话标题截取的最大字符数。首轮用户消息截取前 N 个字符作为初始标题。默认 50。
    /// </summary>
    public int TitleTruncateLength { get; set; } = 50;

    /// <summary>
    /// 是否启用 LLM 异步生成会话摘要标题。默认 true。
    /// </summary>
    public bool EnableTitleSummarization { get; set; } = true;
}
