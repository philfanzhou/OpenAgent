using System.Runtime.ExceptionServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAgent.Contracts.Requests;
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
        _resources = [history, .. resources];
    }

    internal AIAgent Agent { get; }

    internal Task<ChatMessage> CreateUserMessageAsync(CancellationToken cancellationToken) =>
        _history.CreateUserMessageAsync(cancellationToken);

    internal void AppendPartial(string content)
    {
        _history.AppendPartial(content);
    }

    internal void AppendPartialReasoning(string reasoning)
    {
        _history.AppendPartialReasoning(reasoning);
    }

    internal Task CompleteAsync(
        TokenUsage? usage,
        string modelId,
        CancellationToken cancellationToken) =>
        _history.CompleteAsync(usage, modelId, cancellationToken);

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
