using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAgent.Contracts.Content;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Runtime.Agent;

namespace OpenAgent.Core.Conversation;

/// <summary>
/// Adapts the platform conversation store to the SDK history lifecycle.
/// The distributed lock is retained for the complete model invocation.
/// </summary>
internal sealed class PlatformChatHistory : ChatHistoryProvider, IAsyncDisposable
{
    private static readonly TimeSpan DefaultLockTtl = TimeSpan.FromSeconds(30);

    private readonly ConversationContext _conversation;
    private readonly string _agentId;
    private readonly string _input;
    private readonly IReadOnlyList<AgentAttachment> _attachments;
    private readonly IConversationLock _conversationLock;
    private readonly ConversationSessionStore _store;
    private readonly List<ConversationMessage> _pending = [];
    private readonly StringBuilder _partialAssistant = new();
    private IConversationLockHandle? _lockHandle;
    private int _currentVersion;
    private int _nextSequence = 1;
    private bool _loaded;
    private bool _userRecorded;
    private bool _released;
    private bool _stored;
    private bool _finalized;

    internal PlatformChatHistory(
        ConversationContext conversation,
        string agentId,
        string input,
        IReadOnlyList<AgentAttachment> attachments,
        IConversationLock conversationLock,
        ConversationSessionStore store)
    {
        _conversation = conversation;
        _agentId = agentId;
        _input = input;
        _attachments = attachments;
        _conversationLock = conversationLock;
        _store = store;
    }

    internal void AppendPartial(string content)
    {
        if (!string.IsNullOrEmpty(content))
        {
            _partialAssistant.Append(content);
        }
    }

    protected override async ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
        InvokingContext context,
        CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return [];
        }

        ConversationContext conversation = _conversation;
        if (conversation.IsValid)
        {
            _lockHandle = await _conversationLock.TryAcquireAsync(
                conversation.TenantId!,
                conversation.ConversationId!,
                DefaultLockTtl,
                cancellationToken).ConfigureAwait(false);
            if (_lockHandle == null)
            {
                throw new AgentException(
                    AgentErrorCode.Conflict,
                    "Conversation is being processed by another request");
            }
        }

        try
        {
            _loaded = true;
            if (!conversation.IsValid)
            {
                return [];
            }

            ConversationSession loaded = await _store.OpenAsync(
                conversation,
                _agentId,
                _input,
                cancellationToken).ConfigureAwait(false);
            _currentVersion = loaded.CurrentVersion;
            _nextSequence = loaded.NextSequence;
            return loaded.History
                .Select(AgentMessageAdapter.FromStored)
                .Where(message => message != null)
                .Cast<ChatMessage>()
                .ToList();
        }
        catch
        {
            await ReleaseLockAsync().ConfigureAwait(false);
            throw;
        }
    }

    protected override async ValueTask StoreChatHistoryAsync(
        InvokedContext context,
        CancellationToken cancellationToken)
    {
        _finalized = true;
        RecordUser();
        foreach (ConversationMessage message in AgentMessageAdapter.ToStored(
            context.ResponseMessages ?? [],
            ref _nextSequence))
        {
            _pending.Add(message);
        }

        await _store.SaveAsync(
            _conversation,
            _currentVersion,
            _pending,
            ConversationStatus.Completed,
            cancellationToken).ConfigureAwait(false);
        _stored = true;
    }

    protected override async ValueTask InvokedCoreAsync(
        InvokedContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            if (context.InvokeException == null)
            {
                await base.InvokedCoreAsync(context, cancellationToken).ConfigureAwait(false);
                return;
            }

            RecordUser();
            _finalized = true;
            if (_partialAssistant.Length > 0)
            {
                _pending.Add(ConversationSessionStore.Message(
                    _nextSequence++,
                    "assistant",
                    _partialAssistant.ToString()));
            }

            ConversationStatus status = context.InvokeException is OperationCanceledException
                ? ConversationStatus.Cancelled
                : ConversationStatus.Failed;
            await _store.SaveAsync(
                _conversation,
                _currentVersion,
                _pending,
                status,
                CancellationToken.None).ConfigureAwait(false);
            _stored = true;
        }
        finally
        {
            await ReleaseLockAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_loaded && !_stored && !_finalized)
            {
                _finalized = true;
                RecordUser();
                if (_partialAssistant.Length > 0)
                {
                    _pending.Add(ConversationSessionStore.Message(
                        _nextSequence++,
                        "assistant",
                        _partialAssistant.ToString()));
                }
                await _store.SaveAsync(
                    _conversation,
                    _currentVersion,
                    _pending,
                    ConversationStatus.Cancelled,
                    CancellationToken.None).ConfigureAwait(false);
                _stored = true;
            }
        }
        finally
        {
            await ReleaseLockAsync().ConfigureAwait(false);
        }
    }

    private void RecordUser()
    {
        if (_userRecorded)
        {
            return;
        }

        _userRecorded = true;
        _pending.Add(ConversationSessionStore.Message(
            _nextSequence++,
            "user",
            _input,
            metadata: AgentMessageAdapter.BuildAttachmentMetadata(_attachments)));
    }

    private async ValueTask ReleaseLockAsync()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        if (_lockHandle != null)
        {
            await _lockHandle.DisposeAsync().ConfigureAwait(false);
        }
    }
}
