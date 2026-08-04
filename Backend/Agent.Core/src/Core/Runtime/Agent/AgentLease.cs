using Microsoft.Agents.AI;
using OpenAgent.Core.Conversation;

namespace OpenAgent.Core.Runtime.Agent;

internal sealed class AgentLease : IAsyncDisposable
{
    private readonly IAsyncDisposable[] _ownedResources;

    internal AgentLease(AIAgent agent, params IAsyncDisposable[] ownedResources)
    {
        Agent = agent;
        _ownedResources = ownedResources;
    }

    internal AIAgent Agent { get; }

    internal void AppendPartial(string content)
    {
        foreach (PlatformChatHistory history in _ownedResources.OfType<PlatformChatHistory>())
        {
            history.AppendPartial(content);
        }
    }

    public async ValueTask DisposeAsync()
    {
        for (int index = _ownedResources.Length - 1; index >= 0; index--)
        {
            await _ownedResources[index].DisposeAsync().ConfigureAwait(false);
        }
    }
}
