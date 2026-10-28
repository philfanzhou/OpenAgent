namespace OpenAgent.Contracts.Conversation;

public interface ILlmInteractionStore
{
    /// <summary>
    /// 追加一条大模型交互日志。实现必须吞掉自身异常（仅记录警告），
    /// 日志失败绝不影响对话主流程。
    /// </summary>
    Task RecordAsync(LlmInteractionRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按会话分页获取交互日志，按 StartedAt 升序返回。
    /// </summary>
    Task<IReadOnlyList<LlmInteractionRecord>> ListAsync(
        string tenantId,
        string conversationId,
        int skip,
        int take,
        CancellationToken cancellationToken = default);
}
