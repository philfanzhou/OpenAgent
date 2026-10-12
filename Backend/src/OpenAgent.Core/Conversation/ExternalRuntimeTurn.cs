using OpenAgent.Contracts.Security;
using System.Text;
using System.Text.Json;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Core.Conversation.Store;

namespace OpenAgent.Core.Conversation;

internal sealed class ExternalRuntimeTurn : IAsyncDisposable
{
    private readonly ConversationSessionStore _store;
    private readonly IConversationLockHandle _lock;
    private readonly ConversationContext _context;
    private readonly RuntimeInvocation _invocation;
    private readonly CancellationToken _cancellation;
    private readonly List<ConversationMessage> _messages = [];
    private readonly StringBuilder _content = new();
    private readonly StringBuilder _reasoning = new();
    private readonly Dictionary<string, int> _calls = [];
    private int _version;
    private int _sequence;
    private bool _userSaved;
    private bool _completed;
    private bool _cancelled;
    internal string? SessionId { get; private set; }

    private ExternalRuntimeTurn(ConversationSessionStore store, IConversationLockHandle handle,
        RuntimeInvocation invocation, ConversationSession session, CancellationToken cancellationToken)
    {
        _store = store;
        _lock = handle;
        _invocation = invocation;
        _cancellation = cancellationToken;
        _context = new(invocation.Turn.ConversationId, invocation.Turn.TenantId, invocation.Turn.UserId,
            invocation.Profile.AgentId, invocation.Turn.TraceId, invocation.Request.ConversationType);
        _version = session.CurrentVersion;
        _sequence = session.NextSequence;
        SessionId = session.History.LastOrDefault(item => item.Metadata?.Extensions?.ContainsKey("runtime.session") == true)
            ?.Metadata?.Extensions?.GetValueOrDefault("runtime.session");
        if (session.History.Count > 0 && SessionId == null)
            throw new AgentException(AgentErrorCode.Conflict, "The saved runtime session is unavailable. Start a new conversation.");
    }

    internal static async Task<ExternalRuntimeTurn> OpenAsync(ConversationSessionStore store, IConversationLock locks,
        RuntimeInvocation invocation, TimeSpan ttl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(invocation.Turn.ConversationId))
            throw new AgentException(AgentErrorCode.InvalidRequest, "An ACP conversation requires a ConversationId.");
        IConversationLockHandle handle = await locks.TryAcquireAsync(invocation.Turn.TenantId,
            invocation.Turn.ConversationId, ttl, cancellationToken).ConfigureAwait(false)
            ?? throw new AgentException(AgentErrorCode.Conflict, "Conversation is being processed by another request");
        try
        {
            var context = new ConversationContext(invocation.Turn.ConversationId, invocation.Turn.TenantId,
                invocation.Turn.UserId, invocation.Profile.AgentId, invocation.Turn.TraceId, invocation.Request.ConversationType);
            ConversationSession session = await store.OpenAsync(context, invocation.Profile.AgentId, invocation.Request.Query, cancellationToken).ConfigureAwait(false);
            return new ExternalRuntimeTurn(store, handle, invocation, session, cancellationToken);
        }
        catch { await handle.DisposeAsync().ConfigureAwait(false); throw; }
    }

    internal async Task BindAsync(string sessionId, CancellationToken cancellationToken)
    {
        SessionId = sessionId;
        await SaveUserAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SaveUserAsync(CancellationToken cancellationToken)
    {
        if (_userSaved)
            return;
        var binding = new Dictionary<string, string>
        {
            ["runtime.adapter"] = _invocation.Profile.Config.Runtime.Adapter,
            ["runtime.harness"] = _invocation.Profile.Config.Runtime.Harness!,
        };
        if (SessionId != null)
            binding["runtime.session"] = SessionId;
        ConversationMessage user = ConversationSessionStore.Message(_sequence++, "user", _invocation.Request.Query,
            metadata: new ConversationMessageMetadata { Extensions = binding });
        await _store.SaveAsync(_context, _version, [user], ConversationStatus.Running, cancellationToken).ConfigureAwait(false);
        _version = (await _store.Store.GetRecordAsync(_context.TenantId!, _context.ConversationId!, cancellationToken).ConfigureAwait(false))!.Version;
        _userSaved = true;
    }

    internal void Cancel() => _cancelled = true;

    internal void Append(AgentStreamEvent item)
    {
        switch (item.Type)
        {
            case AgentStreamEventType.Content:
                _content.Append(item.Content);
                break;
            case AgentStreamEventType.Reasoning:
                _reasoning.Append(item.Content);
                break;
            case AgentStreamEventType.ToolCall:
                var message = ConversationSessionStore.Message(_sequence, "assistant", "", item.ToolCallId, item.ToolName,
                    new ConversationMessageMetadata { ToolArguments = JsonSerializer.Serialize(item.ToolArguments) });
                if (item.ToolCallId != null && _calls.TryGetValue(item.ToolCallId, out int index))
                    _messages[index] = message with { Sequence = _messages[index].Sequence };
                else
                {
                    _sequence++;
                    if (item.ToolCallId != null)
                        _calls[item.ToolCallId] = _messages.Count;
                    _messages.Add(message);
                }
                break;
            case AgentStreamEventType.ToolResult:
                _messages.Add(ConversationSessionStore.Message(_sequence++, "tool", item.Content ?? "", item.ToolCallId, item.ToolName));
                break;
        }
    }

    internal async Task CompleteAsync(TokenUsage? usage, string modelId, CancellationToken cancellationToken)
    {
        await SaveAsync(ConversationStatus.Completed, usage, modelId, cancellationToken).ConfigureAwait(false);
        _completed = true;
    }

    private async Task SaveAsync(ConversationStatus status, TokenUsage? usage, string modelId, CancellationToken cancellationToken)
    {
        await SaveUserAsync(cancellationToken).ConfigureAwait(false);
        var metadata = new ConversationMessageMetadata
        {
            Reasoning = _reasoning.Length > 0 ? _reasoning.ToString() : null,
            ExecutionStatus = status.ToString(),
            Error = status == ConversationStatus.Failed
                ? new MessageErrorMetadata("Runtime execution failed", "Check the harness configuration and retry after checking task state.", _invocation.Turn.TraceId)
                : null
        };
        _messages.Add(ConversationSessionStore.Message(_sequence++, "assistant", _content.ToString(),
            metadata: metadata, tokenUsage: usage, modelId: modelId));
        await _store.SaveAsync(_context, _version, _messages, status, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_completed)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await SaveAsync(_cancelled || _cancellation.IsCancellationRequested ? ConversationStatus.Cancelled : ConversationStatus.Failed,
                    null, _invocation.Profile.Model.ModelId, timeout.Token).ConfigureAwait(false);
            }
        }
        finally { await _lock.DisposeAsync().ConfigureAwait(false); }
    }
}
