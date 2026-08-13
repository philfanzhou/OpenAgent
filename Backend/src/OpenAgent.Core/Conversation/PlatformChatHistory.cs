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
    private readonly string _input;
    private readonly IReadOnlyList<FileAsset> _files;
    private readonly FileAssetExecutionContext _fileExecution;
    private readonly IConversationLock _conversationLock;
    private readonly ConversationSessionStore _store;
    private readonly ILogger<PlatformChatHistory> _logger;
    private readonly IFileAssetService _fileService;
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

    internal PlatformChatHistory(
        ConversationContext conversation,
        string agentId,
        string input,
        IReadOnlyList<FileAsset> files,
        FileAssetExecutionContext fileExecution,
        IConversationLock conversationLock,
        ConversationSessionStore store,
        ILogger<PlatformChatHistory> logger,
        IFileAssetService fileService)
    {
        _conversation = conversation;
        _agentId = agentId;
        _input = input;
        _files = files;
        _fileExecution = fileExecution;
        _conversationLock = conversationLock;
        _store = store;
        _logger = logger;
        _fileService = fileService;
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
    private ConversationMessage BuildPartialMessage()
    {
        string reasoning = _partialReasoning.ToString();
        IReadOnlyDictionary<string, string>? metadata = reasoning.Length > 0
            ? new Dictionary<string, string>(StringComparer.Ordinal) { ["Reasoning"] = reasoning }
            : null;
        return ConversationSessionStore.Message(
            _nextSequence++,
            "assistant",
            _partialAssistant.ToString(),
            metadata: metadata);
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
    /// 把存储的会话消息转成模型输入，并为历史用户消息重建文件附件
    /// （否则续接会话时模型看不到上一次上传的图片等文件）。
    /// </summary>
    private async Task<List<ChatMessage>> BuildHistoryAsync(
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
            if (string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase)
                && message.FileIds.Count > 0)
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
        foreach (string fileId in fileIds.Distinct(StringComparer.Ordinal))
        {
            try
            {
                FileAssetContent content = await _fileService.ReadAsync(
                    fileId, scope, cancellationToken).ConfigureAwait(false);
                AgentMessageAdapter.AttachFile(chatMessage, content);
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

    protected override async ValueTask StoreChatHistoryAsync(
        InvokedContext context,
        CancellationToken cancellationToken)
    {
        _finalized = true;
        RecordUser();
        List<ConversationMessage> responses = AgentMessageAdapter.ToStored(
            context.ResponseMessages ?? [],
            ref _nextSequence).ToList();
        AssociateCreatedFiles(responses);
        foreach (ConversationMessage message in responses)
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
            if (_partialAssistant.Length > 0 || _partialReasoning.Length > 0)
            {
                _pending.Add(BuildPartialMessage());
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
                if (_partialAssistant.Length > 0 || _partialReasoning.Length > 0)
                {
                    _pending.Add(BuildPartialMessage());
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
            metadata: AgentMessageAdapter.BuildFileMetadata(_files),
            fileIds: _files.Select(item => item.FileId).ToArray()));
    }

    private void AssociateCreatedFiles(List<ConversationMessage> responses)
    {
        IReadOnlyList<FileAsset> created = _fileExecution.Created;
        if (created.Count == 0)
        {
            return;
        }

        int assistantIndex = responses.FindLastIndex(message =>
            string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase));
        if (assistantIndex >= 0)
        {
            responses[assistantIndex] = AgentMessageAdapter.AssociateFiles(
                responses[assistantIndex],
                created);
            return;
        }

        responses.Add(ConversationSessionStore.Message(
            _nextSequence++,
            "assistant",
            "Created file assets.",
            metadata: AgentMessageAdapter.BuildFileMetadata(created),
            fileIds: created.Select(file => file.FileId).ToArray()));
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
