using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace OpenAgent.Core.Runtime.Agent;

/// <summary>
/// Wraps a tool so an invocation failure is reported to the model as an error
/// result instead of escaping to <see cref="FunctionInvokingChatClient"/>, which
/// would rethrow after repeated failures and abort the whole agent run.
/// </summary>
internal sealed class IsolatedToolFunction : AIFunction
{
    private static readonly JsonSerializerOptions ErrorOptions = new()
    {
        // 错误文本要回传给模型并展示给用户，保持原字符可读而不是 \uXXXX 转义。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly AIFunction _inner;

    private IsolatedToolFunction(AIFunction inner)
    {
        _inner = inner;
    }

    public override string Name => _inner.Name;
    public override string Description => _inner.Description;
    public override JsonElement JsonSchema => _inner.JsonSchema;

    internal static AITool Wrap(AITool tool) => tool is AIFunction function
        ? new IsolatedToolFunction(function)
        : tool;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _inner.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return JsonSerializer.Serialize(new
            {
                error = $"Tool '{_inner.Name}' failed: {exception.Message}"
            }, ErrorOptions);
        }
    }
}
