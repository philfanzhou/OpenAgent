namespace OpenAgent.Contracts.Conversation;

public sealed class ConversationStoreOptions
{
    public const string SectionName = "ConversationStore";

    /// <summary>
    /// 执行侧历史消息窗口大小（最近 N 条）。
    /// </summary>
    public int MaxHistoryMessages { get; set; } = 20;

    /// <summary>
    /// 会话标题截取的最大字符数。首轮用户消息截取前 N 个字符作为初始标题。默认 50。
    /// </summary>
    public int TitleTruncateLength { get; set; } = 50;

    /// <summary>
    /// 是否启用 LLM 异步生成会话摘要标题。默认 true。
    /// </summary>
    public bool EnableTitleSummarization { get; set; } = true;
}
