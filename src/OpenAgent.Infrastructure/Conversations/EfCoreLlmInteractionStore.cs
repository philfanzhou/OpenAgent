using Microsoft.EntityFrameworkCore;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Infrastructure.Entities;
using OpenAgent.Infrastructure.Persistence;

namespace OpenAgent.Infrastructure.Conversations;

/// <summary>
/// 大模型交互日志的 PostgreSQL 持久化。日志表只追加：写入失败会抛给
/// 调用方（记录器负责降级为警告），查询按 (StartedAt, CallIndex) 升序分页。
/// </summary>
internal sealed class EfCoreLlmInteractionStore(
    IDbContextFactory<OpenAgentDbContext> contexts) : ILlmInteractionStore
{
    public async Task RecordAsync(LlmInteractionRecord record, CancellationToken cancellationToken = default)
    {
        await using OpenAgentDbContext context = await contexts.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        context.LlmInteractions.Add(ToEntity(record));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<LlmInteractionRecord>> ListAsync(
        string tenantId,
        string conversationId,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        await using OpenAgentDbContext context = await contexts.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        List<LlmInteractionEntity> entities = await context.LlmInteractions.AsNoTracking()
            .Where(item => item.TenantId == tenantId && item.ConversationId == conversationId)
            .OrderBy(item => item.StartedAt)
            .ThenBy(item => item.CallIndex)
            .Skip(Math.Max(0, skip))
            .Take(Math.Max(0, take))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return entities.Select(ToRecord).ToList().AsReadOnly();
    }

    private static LlmInteractionEntity ToEntity(LlmInteractionRecord record) => new()
    {
        InteractionId = record.InteractionId,
        TenantId = record.TenantId,
        UserId = record.UserId,
        ConversationId = record.ConversationId,
        TraceId = record.TraceId,
        AgentId = record.AgentId,
        Source = (int)record.Source,
        Provider = record.Provider,
        ApiFormat = record.ApiFormat,
        ModelId = record.ModelId,
        Streamed = record.Streamed,
        CallIndex = record.CallIndex,
        RequestJson = record.RequestJson,
        ResponseJson = record.ResponseJson,
        PromptTokens = record.TokenUsage?.PromptTokens,
        CompletionTokens = record.TokenUsage?.CompletionTokens,
        TotalTokens = record.TokenUsage?.TotalTokens,
        CachedInputTokens = record.TokenUsage?.CachedInputTokens,
        ReasoningTokens = record.TokenUsage?.ReasoningTokens,
        Status = (int)record.Status,
        ErrorMessage = record.ErrorMessage,
        StartedAt = record.StartedAt,
        DurationMs = record.DurationMs
    };

    private static LlmInteractionRecord ToRecord(LlmInteractionEntity entity) => new()
    {
        InteractionId = entity.InteractionId,
        TenantId = entity.TenantId,
        UserId = entity.UserId,
        ConversationId = entity.ConversationId,
        TraceId = entity.TraceId,
        AgentId = entity.AgentId,
        Source = (LlmInteractionSource)entity.Source,
        Provider = entity.Provider,
        ApiFormat = entity.ApiFormat,
        ModelId = entity.ModelId,
        Streamed = entity.Streamed,
        CallIndex = entity.CallIndex,
        RequestJson = entity.RequestJson,
        ResponseJson = entity.ResponseJson,
        TokenUsage = CreateTokenUsage(entity),
        Status = (LlmInteractionStatus)entity.Status,
        ErrorMessage = entity.ErrorMessage,
        StartedAt = entity.StartedAt,
        DurationMs = entity.DurationMs
    };

    private static Contracts.Requests.TokenUsage? CreateTokenUsage(LlmInteractionEntity entity)
    {
        if (entity.PromptTokens == null
            || entity.CompletionTokens == null
            || entity.TotalTokens == null)
        {
            return null;
        }

        return new Contracts.Requests.TokenUsage
        {
            PromptTokens = entity.PromptTokens.Value,
            CompletionTokens = entity.CompletionTokens.Value,
            TotalTokens = entity.TotalTokens.Value,
            CachedInputTokens = entity.CachedInputTokens,
            ReasoningTokens = entity.ReasoningTokens
        };
    }
}
