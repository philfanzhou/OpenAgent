using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts.Files;
using OpenAgent.Core.Capabilities.Mcp;
using OpenAgent.Core.Files;

namespace OpenAgent.Core.Runtime.Agent;

/// <summary>Applies one turn's policy at the SDK function invocation boundary.</summary>
internal sealed class ToolInvocationPolicy(
    AgentExecutionOptions options,
    IReadOnlyList<AITool> mcpTools,
    IFileAssetService files,
    FileAssetExecutionContext fileContext,
    ILogger<IsolatedToolFunction> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _exclusiveGate = new(1, 1);
    private readonly HashSet<AITool> _mcpTools = new(mcpTools, ReferenceEqualityComparer.Instance);
    private readonly TimeSpan _timeout = options.ToolCallTimeoutSeconds > 0
        ? TimeSpan.FromSeconds(options.ToolCallTimeoutSeconds)
        : Timeout.InfiniteTimeSpan;

    internal ValueTask<object?> InvokeAsync(
        FunctionInvocationContext context,
        CancellationToken cancellationToken)
    {
        AIFunction function = context.Function;
        AITool adapted = _mcpTools.Contains(function)
            ? McpResourcePipeline.Wrap(function, files, fileContext, logger)
            : function;
        AIFunction invocation = (AIFunction)IsolatedToolFunction.Wrap(
            adapted,
            _timeout,
            ToolResultBudgets.Resolve(options, function.Name),
            ToolConcurrencyRules.Resolve(function),
            _exclusiveGate,
            logger);
        return invocation.InvokeAsync(context.Arguments, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        _exclusiveGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
