using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Files;
using OpenAgent.Core.Runtime;
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
    private readonly string _modelId;
    private readonly string _input;
    private readonly IReadOnlyList<FileAsset> _files;
    private readonly FileAssetExecutionContext _fileExecution;
    private readonly IConversationLock _conversationLock;
    private readonly ConversationSessionStore _store;
    private readonly ILogger<PlatformChatHistory> _logger;
    private readonly HistoryAttachments _attachments;
    private readonly List<ConversationMessage> _pending = [];
    private readonly StringBuilder _partialAssistant = new();
    private readonly StringBuilder _partialReasoning = new();
    private readonly List<ChatMessage> _streamedToolMessages = [];
    private IConversationLockHandle? _lockHandle;
    private int _currentVersion;
    private int _nextSequence = 1;
    private bool _loaded;
    private bool _userRecorded;
    private bool _released;
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
        _conversationLock = conversationLock;
        _store = store;
        _logger = logger;
        _attachments = new HistoryAttachments(context, fileService, imageOptimizer, fileOptions.Value);
    }

    internal void AppendPartial(string content)
    {
        if (!string.IsNullOrEmpty(content))
        {
            _partialAssistant.Append(content);
        }
    }

    internal void AppendPartialReasoning(string reasoning)
    {
        if (!string.IsNullOrEmpty(reasoning))
        {
            _partialReasoning.Append(reasoning);
        }
    }

    /// <summary>
    /// 记录本轮已流出的工具调用。失败/取消时随 partial assistant 一并持久化，
    /// 保证刷新后前端重建的时间线与实时看到的工具过程一致。
    /// </summary>
    internal void AppendToolCall(string name, string callId, IDictionary<string, object?>? arguments)
    {
        // 部分 LLM 返回的工具调用 arguments 为 null 而非空对象：规格化为空字典，
        // 保证持久化与刷新后重建的形态一致。
        arguments ??= new Dictionary<string, object?>();
        // 同一调用可能随流式更新重播以补全参数：按 callId 覆盖而非追加，
        // 避免失败/取消路径把同一调用重复持久化成多行。
        if (!string.IsNullOrWhiteSpace(callId))
        {
            _streamedToolMessages.RemoveAll(message =>
                message.Contents.OfType<FunctionCallContent>().Any(call =>
                    string.Equals(call.CallId, callId, StringComparison.Ordinal)));
        }
        _streamedToolMessages.Add(new ChatMessage(
            ChatRole.Assistant,
            [new FunctionCallContent(callId, name, arguments)]));
    }

    internal void AppendToolResult(string? callId, string? result)
    {
        if (string.IsNullOrWhiteSpace(callId))
        {
            return;
        }
        _streamedToolMessages.Add(new ChatMessage(
            ChatRole.Tool,
            [new FunctionResultContent(callId, result)]));
    }

    /// <summary>
    /// 当轮用户消息的文件描述符必须与内联图片在同一次附加中生成：
    /// 若先写入"内容未包含、请用工具读取"再补内联图片，模型会服从前一句指令
    /// 而拒绝识别已经注入的图片。
    /// </summary>
    internal Task<ChatMessage> CreateUserMessageAsync(CancellationToken cancellationToken) =>
        _attachments.CreateUserMessageAsync(cancellationToken);

    /// <summary>把中止/失败时已产生的部分正文与思考内容组装成一条 assistant 消息（含 reasoning 元数据）；失败时附失败原因。</summary>
    private ConversationMessage BuildPartialMessage(ConversationStatus status, MessageErrorMetadata? error = null)
    {
        string reasoning = _partialReasoning.ToString();
        return ConversationSessionStore.Message(
            _nextSequence++,
            "assistant",
            _partialAssistant.ToString(),
            metadata: new ConversationMessageMetadata
            {
                ExecutionStatus = status.ToString(),
                Reasoning = reasoning.Length > 0 ? reasoning : null,
                Error = error
            },
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
            List<ChatMessage> history = await BuildHistoryAsync(loaded.History, cancellationToken).ConfigureAwait(false);
            return ToolHistoryRepair.Repair(history);
        }
        catch
        {
            await ReleaseLockAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal Task<List<ChatMessage>> BuildHistoryAsync(
        IReadOnlyList<ConversationMessage> stored, CancellationToken cancellationToken) =>
        _attachments.BuildHistoryAsync(stored, cancellationToken);

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
            // 用户停止/断开产生的取消是正常路径：Debug 记录即可，Error 会让日志被取消事件淹没。
            if (context.InvokeException is OperationCanceledException)
            {
                _logger.LogDebug(
                    context.InvokeException,
                    "Agent '{AgentId}' execution cancelled for conversation '{ConversationId}'",
                    _agentId,
                    _conversation.ConversationId);
            }
            else if (context.InvokeException is AgentException)
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
            // 失败原因随消息持久化：SSE error 事件只存活一次流式连接，前端刷新后
            // 只剩"响应未完成"占位；取消是用户主动行为，不携带错误。
            _pending.Add(BuildPartialMessage(
                status,
                status == ConversationStatus.Failed
                    ? ExecutionFailureDescriptor.Describe(context.InvokeException, _conversation.TraceId)
                    : null));
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
            await ReleaseLockAsync().ConfigureAwait(false);
        }
    }

    /// <summary>把已流出的工具事件转换为存储行，仅在失败/取消路径补充持久化。</summary>
    private void StageStreamedToolMessages()
    {
        if (_streamedToolMessages.Count == 0)
        {
            return;
        }
        foreach (ConversationMessage message in AgentMessageAdapter.ToStored(
            _streamedToolMessages, ref _nextSequence))
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
