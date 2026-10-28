using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace OpenAgent.Core.Tooling.Abstractions;

/// <summary>SDK-facing contribution; ownership stays with the contributing module.</summary>
internal sealed class AgentFeature : IAsyncDisposable
{
    internal IReadOnlyList<AITool> Tools { get; init; } = [];
    internal IReadOnlyList<AITool> InlineTools { get; init; } = [];
    internal IReadOnlyList<AIContextProvider> ContextProviders { get; init; } = [];
    internal int DeferredToolThreshold { get; init; }
    internal Func<AITool, AITool>? PrepareTool { get; init; }
    internal IAsyncDisposable? Resource { get; init; }

    public ValueTask DisposeAsync() => Resource?.DisposeAsync() ?? ValueTask.CompletedTask;
}
