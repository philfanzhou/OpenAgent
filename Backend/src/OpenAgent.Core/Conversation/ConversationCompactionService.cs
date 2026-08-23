using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Runtime.Agent;

namespace OpenAgent.Core.Conversation;

internal sealed class ConversationCompactionService(
    IConversationStore store,
    IConversationLock conversationLock,
    IAgentRuntimeResolver runtime,
    IAgentChatClientFactory chatClients,
    ConversationHistoryFactory histories,
    ILoggerFactory loggerFactory) : IConversationCompactionService
{
    private static readonly TimeSpan ManualLockTtl = TimeSpan.FromMinutes(5);

    public async Task<ContextSummary> CompactAsync(
        string tenantId,
        string conversationId,
        IAgentUserContext user,
        CancellationToken cancellationToken = default)
    {
        EnsureTenant(tenantId, user);
        ConversationRecord record = await LoadOwnedConversationAsync(
            tenantId,
            conversationId,
            user,
            cancellationToken).ConfigureAwait(false);

        await using IConversationLockHandle handle = await conversationLock.TryAcquireAsync(
            tenantId,
            conversationId,
            ManualLockTtl,
            cancellationToken).ConfigureAwait(false)
            ?? throw new AgentException(
                AgentErrorCode.Conflict,
                "Conversation is being processed by another request");

        record = await LoadOwnedConversationAsync(
            tenantId,
            conversationId,
            user,
            cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(record.AgentId))
        {
            throw new InvalidOperationException("Conversation agent is unavailable.");
        }

        AgentRuntimeProfile profile = await runtime.ResolveAsync(
            record.AgentId,
            user,
            cancellationToken).ConfigureAwait(false);
        IChatClient summarizationClient = chatClients.CreateSummarizationClient(
            profile.Model,
            profile.Config.ContextPolicy);
        SummarizationCompactionStrategy strategy = histories.CreateStrategy(
            profile.Config.ContextPolicy,
            summarizationClient,
            force: true,
            out CompactionTrigger trigger);
        var audited = new AuditedCompactionStrategy(
            strategy,
            trigger,
            "Manual",
            tenantId,
            conversationId,
            store,
            loggerFactory.CreateLogger<AuditedCompactionStrategy>(),
            recordUnchanged: true);
        List<ChatMessage> messages = ConversationSessionStore.ResolveModelHistory(record)
            .Select(AgentMessageAdapter.FromStored)
            .Where(message => message != null)
            .Cast<ChatMessage>()
            .ToList();
        await CompactionProvider.CompactAsync(
            audited,
            messages,
            loggerFactory.CreateLogger<ConversationCompactionService>(),
            cancellationToken).ConfigureAwait(false);

        // MAF intentionally bypasses the strategy for an incomplete history such
        // as one isolated user group. Persist a stable debug record instead of
        // turning that framework boundary into an endpoint 500.
        if (audited.LastAudit == null)
        {
            await audited.RecordNotRunAsync(messages).ConfigureAwait(false);
        }

        ContextSummary? audit = audited.LastAudit;
        if (audit == null || !audited.LastAuditRecorded)
        {
            throw new InvalidOperationException("Conversation compaction audit could not be persisted.");
        }
        return audit;
    }

    private async Task<ConversationRecord> LoadOwnedConversationAsync(
        string tenantId,
        string conversationId,
        IAgentUserContext user,
        CancellationToken cancellationToken)
    {
        ConversationRecord? record = await store.GetRecordAsync(
            tenantId,
            conversationId,
            cancellationToken).ConfigureAwait(false);
        if (record == null)
        {
            throw new InvalidOperationException("Conversation was not found.");
        }
        if (!string.Equals(record.TenantId, tenantId, StringComparison.Ordinal)
            || !string.Equals(record.UserId, user.UserId, StringComparison.Ordinal))
        {
            throw new AgentException(
                AgentErrorCode.PermissionDenied,
                "Conversation does not belong to the current user");
        }
        return record;
    }

    private static void EnsureTenant(string tenantId, IAgentUserContext user)
    {
        if (string.IsNullOrWhiteSpace(user.TenantId)
            || !string.Equals(user.TenantId, tenantId, StringComparison.Ordinal))
        {
            throw new AgentException(
                AgentErrorCode.TenantMismatch,
                "Conversation tenant does not match the current tenant");
        }
    }
}
