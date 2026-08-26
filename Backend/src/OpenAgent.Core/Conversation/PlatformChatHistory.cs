using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Files;
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
    private readonly IFileAssetService _fileService;
    private readonly bool _supportsVision;
    private readonly long _maxInlineImageBytes;
    private readonly int _maxInlineImageCount;
    private readonly List<ConversationMessage> _pending = [];
    private readonly StringBuilder _partialAssistant = new();
    private readonly StringBuilder _partialReasoning = new();
    private IConversationLockHandle? _lockHandle;
    private int _currentVersion;
    private int _nextSequence = 1;
    private bool _loaded;
    private bool _userRecorded;
    private bool _released;
    private bool _stored;
    private bool _finalized;
    private bool _completionStaged;

    internal PlatformChatHistory(
        ConversationContext conversation,
        string agentId,
        string modelId,
        string input,
        IReadOnlyList<FileAsset> files,
        FileAssetExecutionContext fileExecution,
        IConversationLock conversationLock,
        ConversationSessionStore store,
        ILogger<PlatformChatHistory> logger,
        IFileAssetService fileService,
        bool supportsVision = false,
        long maxInlineImageBytes = 4 * 1024 * 1024,
        int maxInlineImageCount = 4)
    {
        _conversation = conversation;
        _agentId = agentId;
        _modelId = modelId;
        _input = input;
        _files = files;
        _fileExecution = fileExecution;
        _conversationLock = conversationLock;
        _store = store;
        _logger = logger;
        _fileService = fileService;
        _supportsVision = supportsVision;
        _maxInlineImageBytes = maxInlineImageBytes;
        _maxInlineImageCount = maxInlineImageCount;
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

    /// <summary>把中止/失败时已产生的部分正文与思考内容组装成一条 assistant 消息（含 reasoning 元数据）。</summary>
    private ConversationMessage BuildPartialMessage(ConversationStatus status)
    {
        string reasoning = _partialReasoning.ToString();
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
            _partialAssistant.ToString(),
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
            return RepairToolHistory(history);
        }
        catch
        {
            await ReleaseLockAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// 把存储的会话消息转成模型输入，并为每条带文件引用的历史消息重建文件附件
    /// （用户上传和 assistant 发布的文件都属于模型上下文）。
    /// </summary>
    internal async Task<List<ChatMessage>> BuildHistoryAsync(
        IReadOnlyList<ConversationMessage> stored,
        CancellationToken cancellationToken)
    {
        var history = new List<ChatMessage>(stored.Count);
        foreach (ConversationMessage message in stored)
        {
            ChatMessage? chatMessage = AgentMessageAdapter.FromStored(message);
            if (chatMessage == null)
            {
                continue;
            }
            if (message.FileIds.Count > 0)
            {
                await AttachFilesAsync(chatMessage, message.FileIds, cancellationToken).ConfigureAwait(false);
            }
            history.Add(chatMessage);
        }
        return history;
    }

    private async Task AttachFilesAsync(
        ChatMessage chatMessage,
        IReadOnlyList<string> fileIds,
        CancellationToken cancellationToken)
    {
        FileAssetScope scope = new()
        {
            TenantId = _conversation.TenantId ?? string.Empty,
            UserId = _conversation.UserId ?? string.Empty,
            ConversationId = _conversation.ConversationId
        };
        int inlineImageCount = 0;
        foreach (string fileId in fileIds.Distinct(StringComparer.Ordinal))
        {
            try
            {
                FileAsset? asset = await _fileService.GetReferencedAsync(
                    fileId, scope, cancellationToken).ConfigureAwait(false);
                if (asset != null)
                {
                    FileAssetContent? inlineImage = null;
                    if (_supportsVision
                        && inlineImageCount < _maxInlineImageCount
                        && IsImage(asset.MediaType)
                        && asset.Length <= _maxInlineImageBytes)
                    {
                        try
                        {
                            inlineImage = await _fileService.ReadAsync(
                                fileId,
                                scope,
                                cancellationToken,
                                _maxInlineImageBytes).ConfigureAwait(false);
                            inlineImageCount++;
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch
                        {
                            // Keep the metadata manifest when the optional inline read fails.
                        }
                    }

                    AgentMessageAdapter.AttachFile(chatMessage, asset, inlineImage);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 历史中的文件已删除或无权限时忽略，不阻断续接。
            }
        }
    }

    private static bool IsImage(string mediaType) =>
        mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 修复会话历史中的工具调用契约：丢弃重复声明或没有对应 tool 响应的 assistant tool_call，
    /// 避免发给模型时出现 "tool_calls must be followed by tool messages" 的 400。
    /// </summary>
    private static List<ChatMessage> RepairToolHistory(IReadOnlyList<ChatMessage> messages)
    {
        HashSet<string> responded = [];
        foreach (ChatMessage message in messages)
        {
            foreach (FunctionResultContent result in message.Contents.OfType<FunctionResultContent>())
            {
                if (!string.IsNullOrWhiteSpace(result.CallId))
                {
                    responded.Add(result.CallId);
                }
            }
        }

        HashSet<string> announced = new(StringComparer.Ordinal);
        var repaired = new List<ChatMessage>(messages.Count);
        foreach (ChatMessage message in messages)
        {
            List<FunctionCallContent> calls = message.Contents.OfType<FunctionCallContent>().ToList();
            if (message.Role == ChatRole.Assistant && calls.Count > 0)
            {
                List<FunctionCallContent> retained = calls
                    .Where(call => !string.IsNullOrWhiteSpace(call.CallId)
                        && announced.Add(call.CallId)
                        && responded.Contains(call.CallId))
                    .ToList();
                if (retained.Count == 0)
                {
                    continue;
                }

                if (retained.Count < calls.Count)
                {
                    ChatMessage rebuilt = new(message.Role, Array.Empty<AIContent>());
                    foreach (AIContent content in message.Contents)
                    {
                        if (content is not FunctionCallContent)
                        {
                            rebuilt.Contents.Add(content);
                        }
                    }

                    foreach (FunctionCallContent call in retained)
                    {
                        rebuilt.Contents.Add(call);
                    }

                    repaired.Add(rebuilt);
                    continue;
                }
            }

            repaired.Add(message);
        }

        return repaired;
    }

    protected override ValueTask StoreChatHistoryAsync(
        InvokedContext context,
        CancellationToken cancellationToken)
    {
        _finalized = true;
        RecordUser();
        HashSet<string> recordedCallIds = new(StringComparer.Ordinal);
        foreach (FunctionCallContent call in (context.ResponseMessages ?? [])
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
            context.ResponseMessages ?? [],
            ref _nextSequence).ToList();
        AssociatePublishedFiles(responses);
        foreach (ConversationMessage message in responses)
        {
            _pending.Add(message);
        }
        _completionStaged = true;
        return ValueTask.CompletedTask;
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
            _pending[assistantIndex] = WithCompletion(
                _pending[assistantIndex],
                usage,
                modelId);
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

    private static ConversationMessage WithCompletion(
        ConversationMessage message,
        TokenUsage? usage,
        string modelId) => new()
        {
            MessageId = message.MessageId,
            Sequence = message.Sequence,
            Role = message.Role,
            Content = message.Content,
            ToolCallId = message.ToolCallId,
            ToolName = message.ToolName,
            IdempotencyKey = message.IdempotencyKey,
            Timestamp = message.Timestamp,
            Metadata = message.Metadata,
            FileIds = message.FileIds,
            TokenUsage = usage,
            ModelId = modelId
        };

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
