using OpenAgent.Contracts.Conversation;

namespace OpenAgent.Core.Conversation.Store;

/// <summary>
/// 进程内交互日志存储：测试与未装配持久化存储的开发环境的默认实现。
/// 生产环境由 Infrastructure 的 EF Core 实现替换。
/// </summary>
internal sealed class InMemoryLlmInteractionStore : ILlmInteractionStore
{
    private readonly object _lock = new();
    private readonly List<LlmInteractionRecord> _records = [];

    public Task RecordAsync(LlmInteractionRecord record, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _records.Add(record);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<LlmInteractionRecord>> ListAsync(
        string tenantId,
        string conversationId,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            IReadOnlyList<LlmInteractionRecord> result = _records
                .Where(record => string.Equals(record.TenantId, tenantId, StringComparison.Ordinal)
                    && string.Equals(record.ConversationId, conversationId, StringComparison.Ordinal))
                .OrderBy(record => record.StartedAt)
                .ThenBy(record => record.CallIndex)
                .Skip(Math.Max(0, skip))
                .Take(Math.Max(0, take))
                .ToList();
            return Task.FromResult(result);
        }
    }
}
