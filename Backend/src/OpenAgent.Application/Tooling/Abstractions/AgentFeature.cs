using Microsoft.Extensions.AI;
using OpenAgent.Contracts.Runtime;

namespace OpenAgent.Application.Tooling.Abstractions;

/// <summary>轮次能力贡献；所有权归产出模块，随 <see cref="AgentFeature"/> 一起释放。</summary>
internal sealed class AgentFeature : IAsyncDisposable
{
    internal IReadOnlyList<AITool> Tools { get; init; } = [];
    internal IReadOnlyList<AITool> InlineTools { get; init; } = [];
    internal IReadOnlyList<IAgentContextProvider> ContextProviders { get; init; } = [];
    internal IReadOnlyList<RuntimeSkill> Skills { get; init; } = [];
    internal IReadOnlyList<RuntimeMcpServer> McpServers { get; init; } = [];
    internal Func<AITool, AITool>? PrepareTool { get; init; }
    internal IAsyncDisposable? Resource { get; init; }

    public ValueTask DisposeAsync() => Resource?.DisposeAsync() ?? ValueTask.CompletedTask;
}
