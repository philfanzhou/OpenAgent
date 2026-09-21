namespace OpenAgent.Contracts.Conversation;

/// <summary>
/// 会话消息的结构化元数据（conversation_messages.metadata_json 的强类型形态）。
/// 已知字段显式建模，未知键进入 <see cref="Extensions"/> 逃生舱原样透传。
/// </summary>
public sealed class ConversationMessageMetadata
{
    /// <summary>附件清单（原 "Files" 字典键内的 JSON 数组提升为强类型）。</summary>
    public List<MessageFileMetadata>? Files { get; set; }

    /// <summary>思考链文本（原 "Reasoning"）。</summary>
    public string? Reasoning { get; set; }

    /// <summary>中止/失败状态（原 "ExecutionStatus"，值为 ConversationStatus 的字符串名）。</summary>
    public string? ExecutionStatus { get; set; }

    /// <summary>工具调用参数的原始 JSON（保持字符串，消费端各自解析）。</summary>
    public string? ToolArguments { get; set; }

    /// <summary>未识别键的逃生舱：旧数据/第三方写入的透传。</summary>
    public Dictionary<string, string>? Extensions { get; set; }
}

/// <summary>消息附件的持久化描述（文件内容不入消息，按 FileId 惰性读取）。</summary>
public sealed record MessageFileMetadata(
    string FileId,
    string FileName,
    string MediaType,
    long Length,
    string? ObjectKey);
