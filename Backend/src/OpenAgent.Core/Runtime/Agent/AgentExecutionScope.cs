using System.Runtime.ExceptionServices;
using Microsoft.Agents.AI;
using OpenAgent.Core.Conversation;

namespace OpenAgent.Core.Runtime.Agent;

/// <summary>
/// Owns per-request resources created by <see cref="AgentFactory"/>: conversation
/// history, MCP client runtimes, and Skill providers with their temporary package
/// directories. Disposal is reverse-order and best-effort so one cleanup failure
/// does not leak the remaining resources.
/// </summary>
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
        _resources = [history, .. resources];
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
        Exception? failure = null;
        for (int index = _resources.Length - 1; index >= 0; index--)
        {
            try
            {
                await _resources[index].DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
        }
        if (failure != null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
