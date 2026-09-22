using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts.Capabilities;

namespace OpenAgent.Core.Runtime.Agent;

/// <summary>
/// Wraps a tool so an invocation failure is reported to the model as an error
/// result instead of escaping to <see cref="FunctionInvokingChatClient"/>, which
/// would rethrow after repeated failures and abort the whole agent run.
/// 超时同样不中断运行：单次调用超过上限（或传输层先行取消）时，调用被取消，
/// 以带 timedOut 标记的结果告知模型，由它决定重试还是绕开。
/// 该包装器是所有工具回传模型的唯一出口，因此也承担结果预算（头尾保留截断）
/// 与统一错误信封渲染：CapabilityDefinition 返回的 <see cref="ToolResult"/>
/// 在这里落成最终字符串，MCP/MAF 工具的裸字符串同样过预算管道。
/// </summary>
internal sealed class IsolatedToolFunction : AIFunction
{
    // 头尾保留比例：开头多为元信息、结尾多为结论/退出状态，中间折叠损失最小。
    private const double HeadRatio = 0.6;

    private readonly AIFunction _inner;
    private readonly TimeSpan _callTimeout;
    private readonly ToolResultBudget _budget;
    private readonly ILogger? _logger;

    private IsolatedToolFunction(
        AIFunction inner,
        TimeSpan callTimeout,
        ToolResultBudget budget,
        ILogger? logger)
    {
        _inner = inner;
        _callTimeout = callTimeout;
        _budget = budget;
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
    /// <param name="logger">记录被脱敏的原始异常（errorId 关联），可空便于单测。</param>
    internal static AITool Wrap(
        AITool tool,
        TimeSpan toolCallTimeout = default,
        ToolResultBudget budget = default,
        ILogger? logger = null) =>
        tool is AIFunction function
            ? new IsolatedToolFunction(function, toolCallTimeout, budget, logger)
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

        try
        {
            object? result = await _inner.InvokeAsync(
                arguments,
                callTimeout?.Token ?? cancellationToken).ConfigureAwait(false);
            return Render(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // 外层运行未取消却被取消：要么是本包装器的单次调用上限触发，
            // 要么是传输层超时（如 MCP HttpClient.Timeout）。统一归类为超时，
            // 模型才能分辨"等太久了"，而不是看到模糊的 "A task was canceled"。
            bool ownDeadline = callTimeout?.IsCancellationRequested == true;
            string error = ownDeadline
                ? $"Tool '{_inner.Name}' timed out after {(int)_callTimeout.TotalSeconds}s and was cancelled"
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
