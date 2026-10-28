using System.Diagnostics;
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

namespace OpenAgent.Runtime.OpenCode;

/// <summary>每轮独立的 ACP 会话和工作区；进程按会话归属与启动身份在空闲窗口内保活复用，工具桥由宿主共享，平台历史作为上下文传入，不跨租户复用 Agent 状态。</summary>
internal sealed class OpenCodeRuntime(IOptions<OpenCodeOptions> options, OpenCodeProcessKeeper keeper, RuntimeToolBridge bridge, ILogger<OpenCodeRuntime> logger) : IAgentRuntimeDriver, IDisposable
{
    private readonly SemaphoreSlim _turns = new(options.Value.MaxConcurrentTurns);
    private readonly object _launchGate = new();
    private OpenCodeLaunchSpec? _launch;

    /// <summary>可执行文件解析一次后缓存；解析失败不影响引擎启动，只在首个执行轮次暴露错误。</summary>
    private OpenCodeLaunchSpec Launch
    {
        get
        {
            if (_launch is { } cached)
            {
                return cached;
            }
            lock (_launchGate)
            {
                if (_launch is { } resolved)
                {
                    return resolved;
                }
                OpenCodeLaunchSpec spec = OpenCodeProcess.Resolve(options.Value);
                if (spec.ShellFallback)
                {
                    logger.LogWarning(
                        "OpenCode executable fell back to npm shim '{Source}' via cmd.exe; install a native executable for direct launch.",
                        spec.Source);
                }
                else
                {
                    logger.LogInformation("OpenCode executable resolved: {Source}", spec.Source);
                }
                _launch = spec;
                return spec;
            }
        }
    }

    public async IAsyncEnumerable<AgentStreamEvent> RunAsync(
        RuntimeInvocation invocation,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (invocation.Profile.Runtime is { Provider: not "OpenCode" })
        {
            throw new AgentException(AgentErrorCode.InvalidRequest, "The selected runtime provider is not enabled.");
        }
        await _turns.WaitAsync(cancellationToken).ConfigureAwait(false);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Min(options.Value.TurnTimeoutSeconds,
            invocation.Profile.Runtime?.TurnTimeoutSeconds ?? options.Value.TurnTimeoutSeconds)));
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
                    throw new AgentException(AgentErrorCode.LlmTimeout, "本轮执行超过时间限制：请稍后重试；若持续超时请联系管理员调整超时配置。");
                }
                catch (AgentException)
                {
                    throw;
                }
                catch (Exception error)
                {
                    // 实测 OpenCode 的 RPC 错误 Message 会逐字回带 provider 响应体（可能含
                    // 密钥材料/账户标识），原始异常不得进入 SSE、持久化、ProblemDetails 或日志。
                    // 分类器只输出错误码 + 固定中文模板；TraceId/错误码/RPC 码（纯数字）入日志，
                    // 管理员按 TraceId 可定位到具体类别——旧路径只记异常类型名，无从排查。
                    AgentException classified = OpenCodeErrorClassifier.Classify(error);
                    logger.LogWarning(
                        "OpenCode turn failed. TraceId={TraceId}, ErrorCode={ErrorCode}, Diagnostic={Diagnostic}",
                        invocation.Turn.TraceId,
                        classified.ErrorCode,
                        classified.Details);
                    throw classified;
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
        OpenCodeWorkspace.CreatePrivateDirectory(settings.WorkspaceRoot);
        OpenCodeWorkspace.CreatePrivateDirectory(parent);
        // 复用键 = 会话归属 + 启动身份：追问轮次命中保活进程，冷启动只在空闲窗口外支付。
        string leaseKey = owner + "/" + OpenCodeProcess.Identity(Launch, invocation, settings.AllowedNativeTools);
        // 每轮随机工作区作为 ACP 会话 cwd；进程主目录与轮次工作区分离，轮次结束即可回收。
        string workspace = Path.Combine(parent, "t-" + Guid.NewGuid().ToString("N"));
        OpenCodeWorkspace.CreatePrivateDirectory(workspace);
        OpenCodeAgentProcess? agent = null;
        RuntimeToolLease? tools = null;
        bool completed = false;
        // 分段计时检查点（相对轮次开始的累计毫秒，-1 表示未到达）：判别延迟归属。
        Stopwatch clock = Stopwatch.StartNew();
        long processAt = 0, sessionAt = -1, promptAt = -1, firstUpdateAt = -1, firstTokenAt = -1, completionAt = -1;
        long Elapsed(long from, long to) => from >= 0 && to >= 0 ? to - from : 0;
        try
        {
            OpenCodeSkills.Materialize(workspace, invocation.Skills, cancellationToken);
            agent = await keeper.TryAcquireAsync(leaseKey).ConfigureAwait(false)
                ?? await SpawnAsync(settings, invocation, leaseKey, parent, cancellationToken).ConfigureAwait(false);
            processAt = clock.ElapsedMilliseconds;
            // 宿主共享桥只按本轮 bearer 注册/吊销工具集，不再每轮启动站点；端点长期存活，
            // 保活复用也不会给 harness 留下死端点。
            tools = await bridge.RegisterAsync(invocation.Tools, cancellationToken).ConfigureAwait(false);
            // 平台自有工具经桥；外部 MCP server 按 Agent 配置直接透传给 harness 原生连接。
            McpServer[] sessionServers = [tools.Server, .. AcpSessionMcp.Map(invocation.McpServers)];
            InitializeResponse initialized = agent.Initialized;
            yield return new AgentStreamEvent
            {
                Type = AgentStreamEventType.RuntimeStarted,
                Runtime = new("OpenCode", invocation.Profile.Runtime?.Id, invocation.Profile.Runtime?.Name,
                    initialized.AgentInfo?.Name, initialized.AgentInfo?.Version)
            };
            agent.Sessions++;
            await using AcpSession session = await agent.Client.NewSessionAsync(new(workspace, sessionServers), cancellationToken).ConfigureAwait(false);
            sessionAt = clock.ElapsedMilliseconds;
            await SelectModelAsync(session, invocation.Profile.Model.ModelId, cancellationToken).ConfigureAwait(false);
            await using AcpPromptTurn turn = await session.StartPromptAsync(prompt, cancellationToken).ConfigureAwait(false);
            promptAt = clock.ElapsedMilliseconds;
            await using IAsyncEnumerator<SessionNotification> updates = session.ReadUpdatesAsync().GetAsyncEnumerator();
            // OpenCode 把 MCP 工具显示为 "服务器名_工具名"；平台事件沿用无前缀工具名。
            AcpEventMapper mapper = new("openagent_");
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
                    if (completionAt < 0)
                    {
                        completionAt = clock.ElapsedMilliseconds;
                    }
                    // 先完成会话队列，再读完尾部；最终响应并不意味着消费者已经读完更新。
                    await session.DisposeAsync().ConfigureAwait(false);
                    closed = true;
                }
                if (!await next.ConfigureAwait(false))
                {
                    break;
                }
                if (firstUpdateAt < 0)
                {
                    firstUpdateAt = clock.ElapsedMilliseconds;
                }
                (long Used, long Size)? before = mapper.LastUsage;
                foreach (AgentStreamEvent item in mapper.Map(updates.Current.Update))
                {
                    if (firstTokenAt < 0 && item.Type is AgentStreamEventType.Content or AgentStreamEventType.Reasoning)
                    {
                        firstTokenAt = clock.ElapsedMilliseconds;
                    }
                    yield return item;
                }
                if (options.Value.LogUsageUpdates && mapper.LastUsage is { } updated && updated != before)
                {
                    OpenCodeLog.UsageUpdate(logger, updated.Used, updated.Size);
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
            completed = true;
            yield return new AgentStreamEvent
            {
                Type = AgentStreamEventType.Usage,
                ModelId = invocation.Profile.Model.ModelId,
                TraceId = invocation.Turn.TraceId,
                // usage_update 只有上下文占用（Used）与窗口上限（Size），没有逐项计费拆分；
                // 填 prompt/total 供面板展示"本轮上下文"，completion 未知保持 0，不伪造。
                Usage = mapper.LastUsage is { } last
                    ? new TokenUsage
                    {
                        PromptTokens = Clamp(last.Used),
                        TotalTokens = Clamp(last.Used),
                    }
                    : null
            };
        }
        finally
        {
            // 吊销本轮工具注册：共享桥继续监听，但旧 bearer 立即失效。
            if (tools is not null)
            {
                await tools.DisposeAsync().ConfigureAwait(false);
            }
            TryDeleteWorkspace(workspace);
            // 失败、取消或超限的进程立即销毁；只有完整成功的轮次才归还保活池。
            if (agent is not null)
            {
                await keeper.ReleaseAsync(agent, completed).ConfigureAwait(false);
            }
            // 每轮一行分段计时：判别冷启动 / ACP 会话 / 模型首 token / 流式 / 收尾的延迟归属。
            long closeAt = clock.ElapsedMilliseconds;
            OpenCodeLog.TurnTiming(logger,
                (int)processAt,
                (int)Elapsed(processAt, sessionAt),
                (int)Elapsed(sessionAt, promptAt),
                (int)Elapsed(promptAt, firstUpdateAt),
                (int)Elapsed(promptAt, firstTokenAt),
                (int)Elapsed(firstTokenAt, completionAt),
                (int)Elapsed(completionAt, closeAt),
                (int)closeAt);
        }
    }

    private async Task<OpenCodeAgentProcess> SpawnAsync(
        OpenCodeOptions settings,
        RuntimeInvocation invocation,
        string leaseKey,
        string parent,
        CancellationToken cancellationToken)
    {
        string home = Path.Combine(parent, "p-" + Guid.NewGuid().ToString("N"));
        OpenCodeWorkspace.CreatePrivateDirectory(home);
        AcpProcess? process = null;
        AcpClient? client = null;
        try
        {
            process = AcpProcess.Start(OpenCodeProcess.Create(Launch, invocation, settings.AllowedNativeTools, home));
            client = new AcpClient(process.Transport, options: new()
            {
                RequestTimeout = TimeSpan.FromSeconds(settings.RequestTimeoutSeconds)
            });
            InitializeResponse initialized = await client.InitializeAsync(new("OpenAgent", "1"), cancellationToken).ConfigureAwait(false);
            return new OpenCodeAgentProcess(process, client, initialized, leaseKey, home);
        }
        catch
        {
            if (client is not null)
            {
                try { await client.DisposeAsync().ConfigureAwait(false); }
                catch (Exception) { }
            }
            if (process is not null)
            {
                try { await process.DisposeAsync().ConfigureAwait(false); }
                catch (Exception) { }
            }
            TryDeleteWorkspace(home);
            throw;
        }
    }

    private void TryDeleteWorkspace(string directory)
    {
        // 只删除本对象创建的随机目录；不得删除宿主指定的根目录、进程主目录或其他轮次的工作区。
        if (!Directory.Exists(directory))
        {
            return;
        }
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not remove OpenCode workspace {Workspace} ({ErrorType})", directory, error.GetType().Name);
            // 保活进程可能短暂保留会话 cwd 的句柄；延迟重试避免遗留目录，不阻塞轮次收尾。
            _ = RetryDeleteWorkspaceAsync(directory);
        }
    }

    private async Task RetryDeleteWorkspaceAsync(string directory)
    {
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt)).ConfigureAwait(false);
                if (!Directory.Exists(directory))
                {
                    return;
                }
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
        logger.LogWarning("Could not remove OpenCode workspace {Workspace} after retries", directory);
    }

    private static async Task SelectModelAsync(AcpSession session, string model, CancellationToken ct)
    {
        ConfigOption? option = session.State.ConfigOptions?.FirstOrDefault(item => item.Category == "model" || item.Id == "model");
        string expected = "openagent/" + model;
        if (option is null)
        {
            // OpenCode 从受控启动配置选模；没有 configOptions 时不猜测私有 RPC。
            return;
        }
        if (option.CurrentValue.ValueKind == JsonValueKind.String && option.CurrentValue.GetString() == expected)
        {
            return;
        }
        await session.SetConfigAsync(option.Id, AcpJson.Element(expected), ct).ConfigureAwait(false);
    }

    private static int Clamp(long value) => value > int.MaxValue ? int.MaxValue : value < 0 ? 0 : (int)value;

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
            throw new AgentException(AgentErrorCode.InvalidRequest, "会话内容超出提示词长度上限，请开启新会话或精简历史消息后重试。");
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

internal static partial class OpenCodeLog
{
    // 由 OpenCode:LogUsageUpdates 显式开启；只记录类型化的 usage_update 值，不落对话报文。
    [LoggerMessage(EventId = 4500, Level = LogLevel.Information, Message = "OpenCode usage update: used={Used} size={Size}")]
    internal static partial void UsageUpdate(ILogger logger, long used, long size);

    /// <summary>
    /// 每轮一行分段计时（不含对话内容），用于判别延迟归属：
    /// process=进程获取/启动+initialize（近 0 表示命中保活）、sessionNew=ACP session/new、
    /// prompt=模型配置+发送、firstUpdate=prompt→首个 session/update、firstToken=prompt→
    /// 首个可见 token、stream=首 token→轮次完成、close=会话关闭+清理+归还。
    /// </summary>
    [LoggerMessage(EventId = 4501, Level = LogLevel.Information,
        Message = "OpenCode turn timing: process={ProcessMs}ms sessionNew={SessionNewMs}ms prompt={PromptMs}ms firstUpdate={FirstUpdateMs}ms firstToken={FirstTokenMs}ms stream={StreamMs}ms close={CloseMs}ms total={TotalMs}ms")]
    internal static partial void TurnTiming(ILogger logger, int processMs, int sessionNewMs, int promptMs, int firstUpdateMs, int firstTokenMs, int streamMs, int closeMs, int totalMs);
}
