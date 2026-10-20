using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAgent.Application.Conversation.History;
using OpenAgent.Application.Tooling.Abstractions;
using OpenAgent.Application.Tooling.Invocation;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Runtime;

namespace OpenAgent.Application.Execution;

internal sealed class RuntimePreparationScope(
    PlatformChatHistory history,
    ILogger logger) : IAsyncDisposable
{
    internal List<IDisposable> Clients { get; } = [];
    internal List<AgentFeature> Features { get; } = [];
    internal ToolInvocationScope Invocation { get; } = new();
    internal AgentTurnContext Context { get; set; } = new();
    internal RuntimeInvocation InvocationRequest { get; set; } = null!;
    private readonly StringBuilder _text = new();
    private readonly StringBuilder _reasoning = new();
    private readonly List<ChatMessage> _tools = [];
    private TokenUsage? _usage;

    internal void Observe(AgentStreamEvent item)
    {
        switch (item.Type)
        {
            case AgentStreamEventType.Content:
                _text.Append(item.Content);
                history.AppendPartial(item.Content ?? string.Empty);
                break;
            case AgentStreamEventType.Reasoning:
                _reasoning.Append(item.Content);
                history.AppendPartialReasoning(item.Content ?? string.Empty);
                break;
            case AgentStreamEventType.ToolCall when item.ToolCallId is { } id && item.ToolName is { } name:
                IDictionary<string, object?> arguments = item.ToolArguments as IDictionary<string, object?> ?? new Dictionary<string, object?>();
                history.AppendToolCall(name, id, arguments);
                _tools.RemoveAll(message => message.Contents.OfType<FunctionCallContent>().Any(call => call.CallId == id));
                _tools.Add(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(id, name, arguments)]));
                break;
            case AgentStreamEventType.ToolResult when item.ToolCallId is not null:
                history.AppendToolResult(item.ToolCallId, item.Content);
                _tools.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(item.ToolCallId, item.Content)]));
                break;
            case AgentStreamEventType.Usage:
                _usage = item.Usage;
                break;
        }
    }

    internal async Task CompleteAsync(CancellationToken ct)
    {
        List<AIContent> content = [];
        if (_reasoning.Length > 0)
        {
            content.Add(new TextReasoningContent(_reasoning.ToString()));
        }
        content.Add(new TextContent(_text.ToString()));
        List<ChatMessage> responses = [.. _tools, new(ChatRole.Assistant, content)];
        foreach (IAgentContextProvider provider in Features.SelectMany(feature => feature.ContextProviders))
        {
            await provider.InvokedAsync(Context, responses, ct).ConfigureAwait(false);
        }
        await history.PersistAsync(responses, ct).ConfigureAwait(false);
        await history.CompleteAsync(_usage, InvocationRequest.Profile.Model.ModelId, ct).ConfigureAwait(false);
    }

    internal async Task FailAsync(Exception error)
    {
        // 请求 token 已取消时仍需落库部分正文和最终状态；沿用平台的有界存储清理路径。
        try
        {
            await history.PersistFailureAsync(error, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception persistenceError)
        {
            logger.LogError("Could not persist interrupted ACP turn ({ErrorType})", persistenceError.GetType().Name);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeResourceAsync(history).ConfigureAwait(false);
        foreach (AgentFeature feature in Enumerable.Reverse(Features))
        {
            await DisposeResourceAsync(feature).ConfigureAwait(false);
        }
        await DisposeResourceAsync(Invocation).ConfigureAwait(false);
        foreach (IDisposable client in Clients)
        {
            try
            {
                client.Dispose();
            }
            catch (Exception error)
            {
                logger.LogWarning("Summary client cleanup failed ({ErrorType})", error.GetType().Name);
            }
        }
    }

    private async ValueTask DisposeResourceAsync(IAsyncDisposable resource)
    {
        try
        {
            await resource.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // 单个清理失败不能跳过其他资源，也不能覆盖原始执行异常。
            logger.LogWarning("ACP turn resource cleanup failed ({ErrorType})", error.GetType().Name);
        }
    }
}
