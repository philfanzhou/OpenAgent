using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts.Capabilities;
using OpenAgent.Core.Capabilities;

namespace OpenAgent.Core.Runtime.Agent;

/// <summary>
/// Wraps a tool so an invocation failure is reported to the model as an error
/// result instead of escaping to <see cref="FunctionInvokingChatClient"/>, which
/// would rethrow after repeated failures and abort the whole agent run.
/// 超时同样不中断运行：单次调用超过上限（或传输层先行取消）时，调用被取消，
/// 以带 timedOut 标记的结果告知模型，由它决定重试还是绕开。
/// 该包装器是所有工具回传模型的唯一出口，因此也承担三件事：
/// 1) Exclusive 工具经每轮共享的信号量串行化（并发调用只放行 ReadOnly 白名单）；
/// 2) 结果预算（头尾保留截断）与统一错误信封渲染——CapabilityDefinition 返回的
///    <see cref="ToolResult"/> 在这里落成最终字符串，MCP/MAF 工具的裸字符串同样过预算管道。
/// </summary>
internal sealed class IsolatedToolFunction : AIFunction
{
    // 头尾保留比例：开头多为元信息、结尾多为结论/退出状态，中间折叠损失最小。
    private const double HeadRatio = 0.6;

    private readonly AIFunction _inner;
    private readonly TimeSpan _callTimeout;
    private readonly ToolResultBudget _budget;
    private readonly ToolConcurrency _concurrency;
    private readonly SemaphoreSlim? _exclusiveGate;
    private readonly ILogger? _logger;

    private IsolatedToolFunction(
        AIFunction inner,
        TimeSpan callTimeout,
        ToolResultBudget budget,
        ToolConcurrency concurrency,
        SemaphoreSlim? exclusiveGate,
        ILogger? logger)
    {
        _inner = inner;
        _callTimeout = callTimeout;
        _budget = budget;
        _concurrency = concurrency;
        _exclusiveGate = exclusiveGate;
        _logger = logger;
    }

    public override string Name => _inner.Name;
    public override string Description => _inner.Description;
    public override JsonElement JsonSchema => _inner.JsonSchema;

    /// <param name="toolCallTimeout">
    /// 单次调用上限；小于等于 <see cref="TimeSpan.Zero"/> 表示不限时，
    /// 仅保留对传输层取消的超时归类。
    /// </param>
    /// <param name="budget">结果字符预算；0 表示不限。</param>
    /// <param name="concurrency">并发类别；Exclusive 工具经 gate 串行化。</param>
    /// <param name="exclusiveGate">
    /// 同一轮执行共享的独占信号量；并发等待计入单次调用超时预算，
    /// 避免慢调用后面排起无限长的队。
    /// </param>
    /// <param name="logger">记录被脱敏的原始异常（errorId 关联），可空便于单测。</param>
    internal static AITool Wrap(
        AITool tool,
        TimeSpan toolCallTimeout = default,
        ToolResultBudget budget = default,
        ToolConcurrency concurrency = ToolConcurrency.Exclusive,
        SemaphoreSlim? exclusiveGate = null,
        ILogger? logger = null) =>
        tool is AIFunction function
            ? new IsolatedToolFunction(function, toolCallTimeout, budget, concurrency, exclusiveGate, logger)
            : tool;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        CancellationTokenSource? callTimeout = null;
        if (_callTimeout > TimeSpan.Zero)
        {
            callTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            callTimeout.CancelAfter(_callTimeout);
        }
        CancellationToken effectiveToken = callTimeout?.Token ?? cancellationToken;

        // acquired 标志区分超时发生在"排队等独占锁"还是"自己执行中"：
        // catch 时无法从信号量状态可靠判断（两种情况 CurrentCount 都是 0）。
        // 等锁必须放在 try 内：排队期间被截止时间取消同样要转成超时信封，
        // 不能让 OperationCanceledException 逸出到 FunctionInvokingChatClient。
        bool acquired = false;
        try
        {
            if (_concurrency == ToolConcurrency.Exclusive && _exclusiveGate != null)
            {
                await _exclusiveGate.WaitAsync(effectiveToken).ConfigureAwait(false);
                acquired = true;
            }

            object? result = await _inner.InvokeAsync(
                arguments,
                effectiveToken).ConfigureAwait(false);
            return Render(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // 外层运行未取消却被取消：单次调用上限触发、排队等待超限，
            // 或传输层超时（如 MCP HttpClient.Timeout）。统一归类为超时，
            // 模型才能分辨"等太久了"，而不是看到模糊的 "A task was canceled"。
            bool ownDeadline = callTimeout?.IsCancellationRequested == true;
            bool queued = _concurrency == ToolConcurrency.Exclusive && !acquired;
            string error = ownDeadline
                ? queued
                    ? $"Tool '{_inner.Name}' timed out after {(int)_callTimeout.TotalSeconds}s while waiting for another exclusive tool call to finish"
                    : $"Tool '{_inner.Name}' timed out after {(int)_callTimeout.TotalSeconds}s and was cancelled"
                : $"Tool '{_inner.Name}' timed out: the transport layer cancelled the call";
            return ToolResult.Error(
                error,
                code: "tool_timeout",
                hint: "Retry only if this tool is expected to be slow; otherwise find a faster path.",
                timedOut: true).Content;
        }
        catch (Exception exception)
        {
            // 原始异常只进日志（errorId 关联），不透传给模型：exception.Message
            // 可能携带内部地址/堆栈细节，且 IncludeDetailedErrors=false 的意图
            // 不应被本层绕过。
            string errorId = Guid.NewGuid().ToString("N")[..8];
            _logger?.LogError(
                exception,
                "Tool {ToolName} invocation failed (errorId {ErrorId})",
                _inner.Name,
                errorId);
            return ToolResult.Error(
                $"Tool '{_inner.Name}' failed with an unexpected error.",
                code: "tool_error",
                hint: "Check the arguments; if they look correct, avoid this tool for the current turn.",
                errorId: errorId).Content;
        }
        finally
        {
            if (acquired)
            {
                _exclusiveGate!.Release();
            }
            callTimeout?.Dispose();
        }
    }

    private object? Render(object? result) => result switch
    {
        // 错误信封已经短小且结构化，不再截断；成功内容过预算管道。
        ToolResult { IsError: true } toolResult => toolResult.Content,
        ToolResult toolResult => Truncate(toolResult.Content),
        string text => Truncate(text),
        _ => result
    };

    private string Truncate(string content)
    {
        int maxChars = _budget.MaxChars;
        if (maxChars <= 0 || content.Length <= maxChars)
        {
            return content;
        }
        // 预算的 60% 给头部、40% 给尾部，折叠标记从尾部份额里扣除，
        // 保证截断后长度绝不超预算。
        int head = (int)(maxChars * HeadRatio);
        string marker = BuildMarker(omitted: content.Length - head, maxChars);
        int tailLength = maxChars - head - marker.Length;
        if (tailLength <= 0)
        {
            // 预算小到放不下头+标记：退化为硬截断，宁可丢尾部也不超预算。
            return content[..maxChars];
        }
        // 尾部确定后省略数只会变小，标记只会变短，总长仍不超过预算。
        marker = BuildMarker(content.Length - head - tailLength, maxChars);
        return content[..head] + marker + content[^tailLength..];

        string BuildMarker(int omitted, int budget) =>
            $"\n[... {omitted} characters omitted: result exceeded the {budget}-character budget; "
            + $"{_budget.TruncationHint}. ...]\n";
    }
}
