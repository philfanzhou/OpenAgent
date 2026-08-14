using Microsoft.Agents.AI;
using OpenAgent.Core.Conversation;

namespace OpenAgent.Core.Runtime.Agent;

internal sealed class AgentExecutionScope : IAsyncDisposable
{
    private readonly PlatformChatHistory _history;
    private readonly IAsyncDisposable[] _resources;

    internal AgentExecutionScope(
        AIAgent agent,
        PlatformChatHistory history,
        params IAsyncDisposable[] resources)
    {
        Agent = agent;
        _history = history;
        _resources = resources;
    }

    internal AIAgent Agent { get; }

    internal void AppendPartial(string content)
    {
        _history.AppendPartial(content);
    }

    internal void AppendPartialReasoning(string reasoning)
    {
        _history.AppendPartialReasoning(reasoning);
    }

    public async ValueTask DisposeAsync()
    {
        for (int index = _resources.Length - 1; index >= 0; index--)
        {
            await _resources[index].DisposeAsync().ConfigureAwait(false);
        }
        await _history.DisposeAsync().ConfigureAwait(false);
    }
}
