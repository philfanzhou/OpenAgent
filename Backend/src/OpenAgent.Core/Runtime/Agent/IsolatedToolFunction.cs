using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace OpenAgent.Core.Runtime.Agent;

/// <summary>
/// Wraps a tool so an invocation failure is reported to the model as an error
/// result instead of escaping to <see cref="FunctionInvokingChatClient"/>, which
/// would rethrow after repeated failures and abort the whole agent run.
/// 超时同样不中断运行：单次调用超过上限（或传输层先行取消）时，调用被取消，
/// 以带 timedOut 标记的结果告知模型，由它决定重试还是绕开。
/// </summary>
internal sealed class IsolatedToolFunction : AIFunction
{
    private static readonly JsonSerializerOptions ErrorOptions = new()
    {
        // 错误文本要回传给模型并展示给用户，保持原字符可读而不是 \uXXXX 转义。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly AIFunction _inner;
    private readonly TimeSpan _callTimeout;

    private IsolatedToolFunction(AIFunction inner, TimeSpan callTimeout)
    {
        _inner = inner;
        _callTimeout = callTimeout;
    }

    public override string Name => _inner.Name;
    public override string Description => _inner.Description;
    public override JsonElement JsonSchema => _inner.JsonSchema;

    /// <param name="toolCallTimeout">
    /// 单次调用上限；小于等于 <see cref="TimeSpan.Zero"/> 表示不限时，
    /// 仅保留对传输层取消的超时归类。
    /// </param>
    internal static AITool Wrap(AITool tool, TimeSpan toolCallTimeout = default) =>
        tool is AIFunction function
            ? new IsolatedToolFunction(function, toolCallTimeout)
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
            return await _inner.InvokeAsync(
                arguments,
                callTimeout?.Token ?? cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            // 外层运行未取消却被取消：要么是本包装器的单次调用上限触发，
            // 要么是传输层超时（如 MCP HttpClient.Timeout）。统一归类为超时，
            // 模型才能分辨"等太久了"，而不是看到模糊的 "A task was canceled"。
            bool ownDeadline = callTimeout?.IsCancellationRequested == true;
            return SerializeError(
                ownDeadline
                    ? $"Tool '{_inner.Name}' timed out after {(int)_callTimeout.TotalSeconds}s and was cancelled"
                    : $"Tool '{_inner.Name}' timed out: {exception.Message}",
                timedOut: true);
        }
        catch (Exception exception)
        {
            return SerializeError($"Tool '{_inner.Name}' failed: {exception.Message}");
        }
        finally
        {
            callTimeout?.Dispose();
        }
    }

    private string SerializeError(string error, bool timedOut = false) =>
        JsonSerializer.Serialize(new { error, timedOut }, ErrorOptions);
}
