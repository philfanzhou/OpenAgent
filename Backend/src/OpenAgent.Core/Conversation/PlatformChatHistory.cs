using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Files;
using OpenAgent.Core.Runtime.Agent;

namespace OpenAgent.Core.Conversation;

/// <summary>
/// MAF ChatHistoryProvider 适配器：编排一轮对话的锁、历史投影、流式缓冲与持久化，
/// 具体职责由 ConversationTurnLock / HistoryFileInflater / HistoryRepair /
/// StreamingTurnBuffer 承担。
/// </summary>
internal sealed class PlatformChatHistory : ChatHistoryProvider, IAsyncDisposable
{
    private static readonly TimeSpan DefaultLockTtl = TimeSpan.FromSeconds(30);

    private readonly ConversationContext _conversation;
    private readonly string _agentId;
    private readonly string _modelId;
    private readonly string _input;
    private readonly IReadOnlyList<FileAsset> _files;
    private readonly FileAssetExecutionContext _fileExecution;
    private readonly ConversationTurnLock _turnLock;
    private readonly StreamingTurnBuffer _buffer = new();
    private readonly HistoryFileInflater _inflater;
    private readonly ConversationSessionStore _store;
    private readonly ILogger<PlatformChatHistory> _logger;
    private readonly List<ConversationMessage> _pending = [];
    private int _currentVersion;
    private int _nextSequence = 1;
    private bool _loaded;
    private bool _userRecorded;
    private bool _stored;
    private bool _finalized;
    private bool _completionStaged;

    public PlatformChatHistory(
        PlatformChatHistoryContext context,
        FileAssetExecutionContext fileExecution,
        IConversationLock conversationLock,
        ConversationSessionStore store,
        ILogger<PlatformChatHistory> logger,
        IFileAssetService fileService,
        IInlineImageOptimizer imageOptimizer,
        IOptions<FileAssetOptions> fileOptions)
    {
        _conversation = context.Conversation;
        _agentId = context.Conversation.AgentId ?? string.Empty;
        _modelId = context.ModelId;
        _input = context.Input;
        _files = context.Files;
        _fileExecution = fileExecution;
        _turnLock = new ConversationTurnLock(conversationLock);
        _store = store;
        _logger = logger;
        _inflater = new HistoryFileInflater(
            new FileAssetScope
            {
                TenantId = _conversation.TenantId ?? string.Empty,
                UserId = _conversation.UserId ?? string.Empty,
                ConversationId = _conversation.ConversationId
            },
            fileService,
            imageOptimizer,
            context.SupportsMultimodal,
            fileOptions.Value.MaxInlineImageBytes,
            fileOptions.Value.MaxInlineImageCount);
    }

    internal void AppendPartial(string content) => _buffer.AppendPartial(content);

    internal void AppendPartialReasoning(string reasoning) => _buffer.AppendPartialReasoning(reasoning);

    internal void AppendToolCall(string name, string callId, IDictionary<string, object?>? arguments) =>
        _buffer.AppendToolCall(name, callId, arguments);

    internal void AppendToolResult(string? callId, string? result) =>
        _buffer.AppendToolResult(callId, result);

    internal Task<ChatMessage> CreateUserMessageAsync(CancellationToken cancellationToken) =>
        _inflater.CreateUserMessageAsync(_input, _files, cancellationToken);

    /// <summary>把中止/失败时已产生的部分正文与思考内容组装成一条 assistant 消息（含 reasoning 元数据）。</summary>
    private ConversationMessage BuildPartialMessage(ConversationStatus status)
    {
        string reasoning = _buffer.PartialReasoning;
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ExecutionStatus"] = status.ToString()
        };
        if (reasoning.Length > 0)
        {
            metadata["Reasoning"] = reasoning;
        }
        return ConversationSessionStore.Message(
            _nextSequence++,
            "assistant",
            _buffer.PartialAssistant,
            metadata: metadata,
            modelId: _modelId);
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
            await _turnLock.AcquireAsync(
                conversation.TenantId!,
                conversation.ConversationId!,
                DefaultLockTtl,
                cancellationToken).ConfigureAwait(false);
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
            List<ChatMessage> history = await _inflater.BuildHistoryAsync(
                loaded.History, cancellationToken).ConfigureAwait(false);
            return HistoryRepair.RepairToolHistory(history);
        }
        catch
        {
            await _turnLock.ReleaseAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Converts stored messages to model input and rebuilds referenced attachments.
    /// </summary>
    internal Task<List<ChatMessage>> BuildHistoryAsync(
        IReadOnlyList<ConversationMessage> stored,
        CancellationToken cancellationToken) =>
        _inflater.BuildHistoryAsync(stored, cancellationToken);

    protected override ValueTask StoreChatHistoryAsync(
        InvokedContext context,
        CancellationToken cancellationToken)
    {
        _finalized = true;
        StageResponses(context.ResponseMessages);
        _completionStaged = true;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Converts the run's response messages into stored rows. Also used on the
    /// interrupted path so a cancelled or failed turn keeps its tool calls and
    /// partial text instead of collapsing into a bare "in progress" marker.
    /// </summary>
    private void StageResponses(IEnumerable<ChatMessage>? responseMessages)
    {
        RecordUser();
        HashSet<string> recordedCallIds = new(StringComparer.Ordinal);
        foreach (FunctionCallContent call in (responseMessages ?? [])
            .SelectMany(message => message.Contents.OfType<FunctionCallContent>()))
        {
            if (call.Exception != null || string.IsNullOrWhiteSpace(call.Name))
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(call.CallId) && !recordedCallIds.Add(call.CallId))
            {
                continue;
            }
            EngineMeter.RecordCapabilityCall(call.Name);
        }
        List<ConversationMessage> responses = AgentMessageAdapter.ToStored(
            responseMessages ?? [],
            ref _nextSequence).ToList();
        AssociatePublishedFiles(responses);
        foreach (ConversationMessage message in responses)
        {
            _pending.Add(message);
        }
    }

    internal async Task CompleteAsync(
        TokenUsage? usage,
        string modelId,
        CancellationToken cancellationToken)
    {
        if (_stored)
        {
            return;
        }
        if (!_completionStaged)
        {
            throw new InvalidOperationException("Conversation completion was not staged.");
        }

        int assistantIndex = _pending.FindLastIndex(message =>
            string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase));
        if (assistantIndex >= 0)
        {
            _pending[assistantIndex] = _pending[assistantIndex] with
            {
                TokenUsage = usage,
                ModelId = modelId
            };
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

            // 记录 agent/工具执行失败，避免被框架吞掉（此前 AgentException 不会被任何日志记录）。
            if (context.InvokeException is AgentException)
            {
                _logger.LogWarning(
                    context.InvokeException,
                    "Agent '{AgentId}' execution failed for conversation '{ConversationId}'",
                    _agentId,
                    _conversation.ConversationId);
            }
            else
            {
                _logger.LogError(
                    context.InvokeException,
                    "Agent '{AgentId}' execution failed for conversation '{ConversationId}'",
                    _agentId,
                    _conversation.ConversationId);
            }

            RecordUser();
            _finalized = true;
            ConversationStatus status = context.InvokeException is OperationCanceledException
                ? ConversationStatus.Cancelled
                : ConversationStatus.Failed;
            StageStreamedToolMessages();
            _pending.Add(BuildPartialMessage(status));
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
            await _turnLock.ReleaseAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_loaded && !_stored)
            {
                ConversationStatus status = ConversationStatus.Completed;
                if (!_finalized)
                {
                    _finalized = true;
                    RecordUser();
                    status = ConversationStatus.Cancelled;
                    StageStreamedToolMessages();
                    _pending.Add(BuildPartialMessage(status));
                }
                await _store.SaveAsync(
                    _conversation,
                    _currentVersion,
                    _pending,
                    status,
                    CancellationToken.None).ConfigureAwait(false);
                _stored = true;
            }
        }
        finally
        {
            await _turnLock.ReleaseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>把已流出的工具事件转换为存储行，仅在失败/取消路径补充持久化。</summary>
    private void StageStreamedToolMessages()
    {
        IReadOnlyList<ChatMessage> streamed = _buffer.StreamedToolMessages;
        if (streamed.Count == 0)
        {
            return;
        }
        foreach (ConversationMessage message in AgentMessageAdapter.ToStored(
            streamed, ref _nextSequence))
        {
            _pending.Add(message);
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
            metadata: AgentMessageAdapter.BuildFileMetadata(_files),
            fileIds: _files.Select(item => item.FileId).ToArray()));
    }

    private void AssociatePublishedFiles(List<ConversationMessage> responses)
    {
        IReadOnlyList<FileAsset> published = _fileExecution.Published;
        if (published.Count == 0)
        {
            return;
        }

        int assistantIndex = responses.FindLastIndex(message =>
            string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase));
        if (assistantIndex >= 0)
        {
            responses[assistantIndex] = AgentMessageAdapter.AssociateFiles(
                responses[assistantIndex],
                published);
            return;
        }

        responses.Add(ConversationSessionStore.Message(
            _nextSequence++,
            "assistant",
            "Published file assets.",
            metadata: AgentMessageAdapter.BuildFileMetadata(published),
            fileIds: published.Select(file => file.FileId).ToArray()));
    }
}
