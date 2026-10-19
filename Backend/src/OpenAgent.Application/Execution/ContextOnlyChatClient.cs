using Microsoft.Extensions.AI;

namespace OpenAgent.Application.Execution;

/// <summary>仅为现有历史/Skill context provider 提供上下文载体，任何推理调用都显式禁止。</summary>
internal sealed class ContextOnlyChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("Model inference must run through the ACP runtime driver.");
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("Model inference must run through the ACP runtime driver.");
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose()
    {
    }
}
