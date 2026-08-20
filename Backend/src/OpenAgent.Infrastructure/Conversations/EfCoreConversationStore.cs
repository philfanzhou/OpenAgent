using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Security;
using OpenAgent.Infrastructure.Entities;

namespace OpenAgent.Infrastructure;

internal sealed class EfCoreConversationStore(
    IDbContextFactory<OpenAgentDbContext> contexts,
    ICurrentUserContext currentUser) : IConversationStore
{
    public async Task<IReadOnlyList<ConversationMessage>> GetMessagesAsync(
        string tenantId,
        string conversationId,
        int maxMessages,
        CancellationToken cancellationToken = default)
    {
        if (maxMessages <= 0)
        {
            return Array.Empty<ConversationMessage>();
        }

        await using OpenAgentDbContext context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        bool exists = await context.Conversations.AsNoTracking().AnyAsync(
            item => item.ConversationId == conversationId && item.TenantId == tenantId && !item.IsDeletedByUser,
            cancellationToken).ConfigureAwait(false);
        if (!exists)
        {
            return Array.Empty<ConversationMessage>();
        }

        List<ConversationMessageEntity> entities = await context.ConversationMessages.AsNoTracking()
            .Where(item => item.ConversationId == conversationId)
            .OrderByDescending(item => item.Sequence)
            .Take(maxMessages)
            .OrderBy(item => item.Sequence)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return await ToMessagesAsync(context, entities, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ConversationMessage>> GetMessagesPagedAsync(
        string tenantId,
        string conversationId,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        if (take <= 0)
        {
            return Array.Empty<ConversationMessage>();
        }

        await using OpenAgentDbContext context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        bool exists = await context.Conversations.AsNoTracking().AnyAsync(
            item => item.ConversationId == conversationId && item.TenantId == tenantId && !item.IsDeletedByUser,
            cancellationToken).ConfigureAwait(false);
        if (!exists)
        {
            return Array.Empty<ConversationMessage>();
        }

        List<ConversationMessageEntity> entities = await context.ConversationMessages.AsNoTracking()
            .Where(item => item.ConversationId == conversationId)
            .OrderBy(item => item.Sequence)
            .Skip(Math.Max(skip, 0))
            .Take(take)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return await ToMessagesAsync(context, entities, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ConversationRecord?> GetRecordAsync(
        string tenantId,
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        await using OpenAgentDbContext context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        ConversationEntity? entity = await context.Conversations.AsNoTracking().SingleOrDefaultAsync(
            item => item.ConversationId == conversationId && item.TenantId == tenantId && !item.IsDeletedByUser,
            cancellationToken).ConfigureAwait(false);
        if (entity == null)
        {
            return null;
        }

        List<ConversationMessageEntity> messages = await context.ConversationMessages.AsNoTracking()
            .Where(item => item.ConversationId == conversationId)
            .OrderBy(item => item.Sequence)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return ToRecord(entity, await ToMessagesAsync(context, messages, cancellationToken).ConfigureAwait(false));
    }

    public async Task<bool> CreateAsync(ConversationRecord record, CancellationToken cancellationToken = default)
    {
        await using OpenAgentDbContext context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        context.Conversations.Add(ToEntity(record));
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException)
        {
            return false;
        }
    }

    public async Task<AppendResult> AppendMessagesAsync(
        string tenantId,
        string conversationId,
        int expectedVersion,
        IReadOnlyList<ConversationMessage> messages,
        CancellationToken cancellationToken = default)
    {
        await using OpenAgentDbContext context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        ConversationEntity? conversation = await context.Conversations.SingleOrDefaultAsync(
            item => item.ConversationId == conversationId && item.TenantId == tenantId && !item.IsDeletedByUser,
            cancellationToken).ConfigureAwait(false);
        if (conversation == null)
        {
            return AppendResult.Conflict("conversation-not-found");
        }
        if (conversation.Version != expectedVersion)
        {
            return AppendResult.Conflict("version-conflict");
        }

        HashSet<string> existingMessageIds = messages.Count == 0
            ? []
            : (await context.ConversationMessages.AsNoTracking()
                .Where(item => item.ConversationId == conversationId && messages.Select(message => message.MessageId).Contains(item.MessageId))
                .Select(item => item.MessageId)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
                .ToHashSet(StringComparer.Ordinal);
        List<ConversationMessage> additions = messages.Where(message => existingMessageIds.Add(message.MessageId)).ToList();
        string[] requestedFileIds = additions
            .SelectMany(message => message.FileIds)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        HashSet<string> conversationFileIds = requestedFileIds.Length == 0
            ? []
            : (await context.ConversationFileReferences.AsNoTracking()
                .Where(item => item.ConversationId == conversationId && requestedFileIds.Contains(item.FileId))
                .Select(item => item.FileId)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
                .ToHashSet(StringComparer.Ordinal);
        foreach (ConversationMessage message in additions)
        {
            context.ConversationMessages.Add(ToEntity(conversationId, message));
            foreach (string fileId in message.FileIds.Distinct(StringComparer.Ordinal))
            {
                if (conversationFileIds.Add(fileId))
                {
                    context.ConversationFileReferences.Add(new ConversationFileReferenceEntity
                    {
                        ConversationId = conversationId,
                        FileId = fileId,
                        CreatedAt = message.Timestamp
                    });
                }
                context.MessageFileReferences.Add(new MessageFileReferenceEntity
                {
                    MessageId = message.MessageId,
                    FileId = fileId,
                    CreatedAt = message.Timestamp
                });
            }
        }

        if (additions.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return AppendResult.Ok(conversation.Version, conversation.MessageCount, messages.Count);
        }

        conversation.Version++;
        conversation.MessageCount += additions.Count;
        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        conversation.LastMessageAt = additions.Max(item => item.Timestamp);
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return AppendResult.Ok(conversation.Version, conversation.MessageCount, messages.Count - additions.Count);
        }
        catch (DbUpdateConcurrencyException)
        {
            return AppendResult.Conflict("version-conflict");
        }
        catch (DbUpdateException exception)
        {
            return AppendResult.Conflict($"database-write-failed:{exception.GetType().Name}");
        }
    }

    public async Task<bool> UpdateStatusAsync(
        string tenantId,
        string conversationId,
        ConversationStatus status,
        int expectedVersion,
        CancellationToken cancellationToken = default)
    {
        await using OpenAgentDbContext context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        ConversationEntity? conversation = await context.Conversations.SingleOrDefaultAsync(
            item => item.ConversationId == conversationId && item.TenantId == tenantId && !item.IsDeletedByUser,
            cancellationToken).ConfigureAwait(false);
        if (conversation == null || conversation.Version != expectedVersion)
        {
            return false;
        }

        conversation.Status = (int)status;
        conversation.Version++;
        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }

    public async Task<bool> UpdateModelOverrideAsync(
        string tenantId,
        string conversationId,
        LlmModelSelection? modelOverride,
        int expectedVersion,
        CancellationToken cancellationToken = default)
    {
        await using OpenAgentDbContext context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        ConversationEntity? conversation = await context.Conversations.SingleOrDefaultAsync(
            item => item.ConversationId == conversationId && item.TenantId == tenantId && !item.IsDeletedByUser,
            cancellationToken).ConfigureAwait(false);
        if (conversation == null || conversation.Version != expectedVersion)
        {
            return false;
        }

        conversation.ModelProvider = modelOverride?.Provider;
        conversation.ModelId = modelOverride?.ModelId;
        conversation.Version++;
        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<ConversationRecord>> ListConversationsAsync(
        string tenantId,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        if (take <= 0)
        {
            return Array.Empty<ConversationRecord>();
        }

        await using OpenAgentDbContext context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        List<ConversationEntity> conversations = await context.Conversations.AsNoTracking()
            .Where(item => item.TenantId == tenantId && !item.IsDeletedByUser)
            .Where(item => item.UserId == currentUser.UserId)
            .Where(item => item.Type == (int)ConversationType.User)
            .OrderByDescending(item => item.LastMessageAt)
            .Skip(Math.Max(skip, 0))
            .Take(take)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return conversations.Select(item => ToRecord(item, [])).ToList().AsReadOnly();
    }

    public async Task<IReadOnlyList<ConversationRecord>> SearchConversationsAsync(
        string tenantId,
        string keyword,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(keyword) || take <= 0)
        {
            return Array.Empty<ConversationRecord>();
        }

        await using OpenAgentDbContext context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        List<ConversationEntity> conversations = await context.Conversations.AsNoTracking()
            .Where(conversation => conversation.TenantId == tenantId && !conversation.IsDeletedByUser)
            .Where(conversation => conversation.UserId == currentUser.UserId)
            .Where(conversation => conversation.Type == (int)ConversationType.User)
            .Where(conversation => context.ConversationMessages.Any(message =>
                message.ConversationId == conversation.ConversationId
                && EF.Functions.ILike(message.Content, $"%{keyword}%")))
            .OrderByDescending(item => item.LastMessageAt)
            .Skip(Math.Max(skip, 0))
            .Take(take)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return conversations.Select(item => ToRecord(item, [])).ToList().AsReadOnly();
    }

    public async Task<bool> SoftDeleteAsync(
        string tenantId,
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        await using OpenAgentDbContext context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        ConversationEntity? conversation = await context.Conversations.SingleOrDefaultAsync(
            item => item.ConversationId == conversationId && item.TenantId == tenantId && !item.IsDeletedByUser,
            cancellationToken).ConfigureAwait(false);
        if (conversation == null)
        {
            return false;
        }

        conversation.IsDeletedByUser = true;
        conversation.DeletedAt = DateTimeOffset.UtcNow;
        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static ConversationEntity ToEntity(ConversationRecord record) => new()
    {
        ConversationId = record.ConversationId,
        TenantId = record.TenantId,
        UserId = record.UserId,
        Type = (int)record.Type,
        AgentId = record.AgentId,
        ModelProvider = record.ModelOverride?.Provider,
        ModelId = record.ModelOverride?.ModelId,
        TraceId = record.TraceId,
        Version = record.Version,
        Status = (int)record.Status,
        CreatedAt = record.CreatedAt,
        UpdatedAt = record.UpdatedAt,
        LastMessageAt = record.LastMessageAt,
        MessageCount = record.MessageCount,
        Title = record.Title,
        IsDeletedByUser = record.IsDeletedByUser,
        DeletedAt = record.DeletedAt
    };

    private static ConversationMessageEntity ToEntity(string conversationId, ConversationMessage message) => new()
    {
        MessageId = message.MessageId,
        ConversationId = conversationId,
        Sequence = message.Sequence,
        Role = message.Role,
        Content = message.Content,
        ToolCallId = message.ToolCallId,
        ToolName = message.ToolName,
        IdempotencyKey = message.IdempotencyKey,
        Timestamp = message.Timestamp,
        MetadataJson = message.Metadata == null ? null : JsonSerializer.Serialize(message.Metadata),
        PromptTokens = message.TokenUsage?.PromptTokens,
        CompletionTokens = message.TokenUsage?.CompletionTokens,
        TotalTokens = message.TokenUsage?.TotalTokens,
        CachedInputTokens = message.TokenUsage?.CachedInputTokens,
        ReasoningTokens = message.TokenUsage?.ReasoningTokens,
        ModelId = message.ModelId
    };

    private static ConversationRecord ToRecord(ConversationEntity entity, IReadOnlyList<ConversationMessage> messages) => new()
    {
        ConversationId = entity.ConversationId,
        TenantId = entity.TenantId,
        UserId = entity.UserId,
        Type = (ConversationType)entity.Type,
        AgentId = entity.AgentId,
        ModelOverride = string.IsNullOrWhiteSpace(entity.ModelProvider)
            || string.IsNullOrWhiteSpace(entity.ModelId)
            ? null
            : new LlmModelSelection
            {
                Provider = entity.ModelProvider,
                ModelId = entity.ModelId
            },
        TraceId = entity.TraceId,
        Version = entity.Version,
        Status = (ConversationStatus)entity.Status,
        CreatedAt = entity.CreatedAt,
        UpdatedAt = entity.UpdatedAt,
        LastMessageAt = entity.LastMessageAt,
        MessageCount = entity.MessageCount,
        Title = entity.Title,
        IsDeletedByUser = entity.IsDeletedByUser,
        DeletedAt = entity.DeletedAt,
        Messages = messages.ToList()
    };

    private static async Task<IReadOnlyList<ConversationMessage>> ToMessagesAsync(
        OpenAgentDbContext context,
        IReadOnlyList<ConversationMessageEntity> entities,
        CancellationToken cancellationToken)
    {
        if (entities.Count == 0)
        {
            return Array.Empty<ConversationMessage>();
        }

        string[] messageIds = entities.Select(item => item.MessageId).ToArray();
        Dictionary<string, IReadOnlyList<string>> fileIds = (await context.MessageFileReferences.AsNoTracking()
            .Where(item => messageIds.Contains(item.MessageId))
            .ToListAsync(cancellationToken).ConfigureAwait(false))
            .GroupBy(item => item.MessageId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)group.Select(item => item.FileId).ToList(),
                StringComparer.Ordinal);
        return entities.Select(entity => new ConversationMessage
        {
            MessageId = entity.MessageId,
            Sequence = entity.Sequence,
            Role = entity.Role,
            Content = entity.Content,
            ToolCallId = entity.ToolCallId,
            ToolName = entity.ToolName,
            IdempotencyKey = entity.IdempotencyKey,
            Timestamp = entity.Timestamp,
            Metadata = DeserializeMetadata(entity.MetadataJson),
            FileIds = fileIds.GetValueOrDefault(entity.MessageId, Array.Empty<string>()),
            TokenUsage = CreateTokenUsage(entity),
            ModelId = entity.ModelId
        }).ToList().AsReadOnly();
    }

    private static OpenAgent.Contracts.Requests.TokenUsage? CreateTokenUsage(
        ConversationMessageEntity entity)
    {
        if (entity.PromptTokens == null
            || entity.CompletionTokens == null
            || entity.TotalTokens == null)
        {
            return null;
        }

        return new OpenAgent.Contracts.Requests.TokenUsage
        {
            PromptTokens = entity.PromptTokens.Value,
            CompletionTokens = entity.CompletionTokens.Value,
            TotalTokens = entity.TotalTokens.Value,
            CachedInputTokens = entity.CachedInputTokens,
            ReasoningTokens = entity.ReasoningTokens
        };
    }

    private static IReadOnlyDictionary<string, string>? DeserializeMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
