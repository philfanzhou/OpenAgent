using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Contracts.Security;
using OpenAgent.Runtime.Acp.Client;
using OpenAgent.Runtime.Acp.Processes;
using OpenAgent.Runtime.Acp.Protocol;

namespace OpenAgent.Runtime;

/// <summary>每轮独立的 ACP 进程和工作区；平台历史作为上下文传入，不跨租户复用 Agent 状态。</summary>
internal sealed class OpenCodeRuntime(IOptions<OpenCodeOptions> options, ILogger<OpenCodeRuntime> logger) : IAgentRuntimeDriver, IDisposable
{
    private readonly SemaphoreSlim _turns = new(options.Value.MaxConcurrentTurns);

    public async IAsyncEnumerable<AgentStreamEvent> RunAsync(
        RuntimeInvocation invocation,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await _turns.WaitAsync(cancellationToken).ConfigureAwait(false);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.Value.TurnTimeoutSeconds));
        try
        {
            await using IAsyncEnumerator<AgentStreamEvent> events = RunCoreAsync(invocation, deadline.Token).GetAsyncEnumerator();
            while (true)
            {
                bool available;
                try
                {
                    available = await events.MoveNextAsync().ConfigureAwait(false);
                }
                catch (Exception) when (deadline.IsCancellationRequested)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new AgentException(AgentErrorCode.LlmTimeout, "OpenCode turn exceeded its time limit.");
                }
                catch (AgentException)
                {
                    throw;
                }
                catch (Exception error)
                {
                    // 外部 RPC 错误可能包含模型请求/凭据，不能原样进入 SSE、持久化或日志。
                    logger.LogWarning("OpenCode runtime failed ({ErrorType})", error.GetType().Name);
                    throw new AgentException(AgentErrorCode.PipelineExecutionFailed, "OpenCode runtime failed; check the executable, provider configuration and connectivity.");
                }
                if (!available)
                {
                    break;
                }
                yield return events.Current;
            }
        }
        finally
        {
            _turns.Release();
        }
    }

    public void Dispose() => _turns.Dispose();

    private async IAsyncEnumerable<AgentStreamEvent> RunCoreAsync(
        RuntimeInvocation invocation,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        OpenCodeOptions settings = options.Value;
        ContentBlock[] prompt = BuildPrompt(invocation.Messages, settings.MaxPromptCharacters);
        // 标识仅用于目录分区；每轮随机子目录避免同会话并发/重试继承另一进程的可变状态。
        string owner = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            invocation.Turn.TenantId + "\0" + invocation.Turn.UserId + "\0" + invocation.Turn.ConversationId)));
        string parent = Path.Combine(settings.WorkspaceRoot, owner);
        string workspace = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            await using RuntimeToolBridge bridge = await RuntimeToolBridge.StartAsync(invocation.Tools, cancellationToken).ConfigureAwait(false);
            // 平台自有工具经桥；外部 MCP server 按 Agent 配置直接透传给 harness 原生连接。
            McpServer[] sessionServers = [bridge.Server, .. OpenCodeSessionMcp.Map(invocation.McpServers)];
            await using AcpProcess process = AcpProcess.Start(OpenCodeLaunch.Create(settings, invocation, workspace));
            await using AcpClient client = new(process.Transport, options: new()
            {
                RequestTimeout = TimeSpan.FromSeconds(settings.RequestTimeoutSeconds)
            });
            await client.InitializeAsync(new("OpenAgent", "1"), cancellationToken).ConfigureAwait(false);
            await using AcpSession session = await client.NewSessionAsync(new(workspace, sessionServers), cancellationToken).ConfigureAwait(false);
            await SelectModelAsync(session, invocation.Profile.Model.ModelId, cancellationToken).ConfigureAwait(false);
            await using AcpPromptTurn turn = await session.StartPromptAsync(prompt, cancellationToken).ConfigureAwait(false);
            await using IAsyncEnumerator<SessionNotification> updates = session.ReadUpdatesAsync().GetAsyncEnumerator();
            AcpEventMapper mapper = new();
            bool closed = false;
            Exception? completionFailure = null;
            while (true)
            {
                Task<bool> next = updates.MoveNextAsync().AsTask();
                if (!closed && await Task.WhenAny(next, turn.Completion).ConfigureAwait(false) == turn.Completion)
                {
                    try
                    {
                        await turn.Completion.ConfigureAwait(false);
                    }
                    catch (Exception error)
                    {
                        completionFailure = error;
                    }
                    // 先完成会话队列，再读完尾部；最终响应并不意味着消费者已经读完更新。
                    await session.DisposeAsync().ConfigureAwait(false);
                    closed = true;
                }
                if (!await next.ConfigureAwait(false))
                {
                    break;
                }
                foreach (AgentStreamEvent item in mapper.Map(updates.Current.Update))
                {
                    yield return item;
                }
            }
            if (completionFailure is not null)
            {
                ExceptionDispatchInfo.Capture(completionFailure).Throw();
            }
            PromptResponse result = await turn.Completion.ConfigureAwait(false);
            if (result.StopReason == "cancelled")
            {
                throw new OperationCanceledException("ACP prompt was cancelled.", cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (result.StopReason != "end_turn")
            {
                throw new AgentException(AgentErrorCode.PipelineExecutionFailed, "OpenCode did not complete the prompt.");
            }
            yield return new AgentStreamEvent
            {
                Type = AgentStreamEventType.Usage,
                ModelId = invocation.Profile.Model.ModelId,
                TraceId = invocation.Turn.TraceId
            };
        }
        finally
        {
            // 只删除本对象创建的随机目录；不得删除宿主指定的根目录或其他轮次的工作区。
            if (Directory.Exists(workspace))
            {
                try
                {
                    Directory.Delete(workspace, recursive: true);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning("Could not remove OpenCode workspace {Workspace} ({ErrorType})", workspace, error.GetType().Name);
                }
            }
        }
    }

    private static async Task SelectModelAsync(AcpSession session, string model, CancellationToken ct)
    {
        ConfigOption? option = session.State.ConfigOptions?.FirstOrDefault(item => item.Category == "model" || item.Id == "model");
        string expected = "openagent/" + model;
        if (option is null)
        {
            // 固定版本 OpenCode 从受控启动配置选模；没有 configOptions 时不猜测私有 RPC。
            return;
        }
        if (option.CurrentValue.ValueKind == JsonValueKind.String && option.CurrentValue.GetString() == expected)
        {
            return;
        }
        await session.SetConfigAsync(option.Id, AcpJson.Element(expected), ct).ConfigureAwait(false);
    }

    private static ContentBlock[] BuildPrompt(IReadOnlyList<RuntimeMessage> messages, int maxCharacters)
    {
        if (messages.Count == 0)
        {
            throw new ArgumentException("Runtime input must contain a user message.");
        }
        RuntimeMessage current = messages[^1];
        string history = messages.Count > 1
            ? JsonSerializer.Serialize(messages.Take(messages.Count - 1).Select(message => new { role = message.Role, content = message.Content }))
            : string.Empty;
        if (history.Length + current.Content.Length > maxCharacters)
        {
            throw new AgentException(AgentErrorCode.InvalidRequest, "Conversation exceeds the ACP prompt limit; compact it before retrying.");
        }
        List<ContentBlock> content = [];
        if (history.Length > 0)
        {
            content.Add(new EmbeddedResource(new ResourceContents("openagent://conversation/history", "application/json", history)));
        }
        content.Add(new TextContent(current.Content));
        // 历史图片已由平台按窗口筛选；保留其数据，才能继续讨论上一轮上传的图片。
        foreach (RuntimeMessage message in messages)
        {
            foreach (RuntimeAttachment attachment in message.Attachments ?? [])
            {
                if (attachment.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    content.Add(new ImageContent(Convert.ToBase64String(attachment.Data.Span), attachment.MediaType));
                }
            }
        }
        return content.ToArray();
    }
}
