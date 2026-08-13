using Microsoft.Agents.AI;
using OpenAgent.Core.Conversation;

namespace OpenAgent.Core.Runtime.Agent;

internal sealed class AgentExecutionScope : IAsyncDisposable
{
    private readonly PlatformChatHistory _history;

    internal AgentExecutionScope(AIAgent agent, PlatformChatHistory history)
    {
        Agent = agent;
        _history = history;
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

    public ValueTask DisposeAsync() => _history.DisposeAsync();
}
