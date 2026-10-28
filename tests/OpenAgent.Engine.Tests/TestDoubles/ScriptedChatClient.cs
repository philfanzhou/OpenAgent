using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace OpenAgent.Engine.Tests;

/// <summary>
/// 脚本化 IChatClient：按请求轮次依次回放预置的流式更新序列（与生产
/// ChatClient 在 FunctionInvokingChatClient 之下的位置一致）。
/// <paramref name="holdUntilCancelled"/> 打开时，最后一轮先流出已给的更新，
/// 然后挂起等待取消——用于模拟被用户停止的长时间生成。
/// </summary>
internal sealed class ScriptedChatClient(
    IReadOnlyList<IReadOnlyList<ChatResponseUpdate>> turns,
    bool holdUntilCancelled = false) : IChatClient
{
    private readonly TaskCompletionSource _cancelled = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    internal List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

    internal Task Cancelled => _cancelled.Task;

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Requests.Add(messages.ToList());
        int index = Requests.Count - 1;
        IReadOnlyList<ChatResponseUpdate> turn = turns[Math.Min(index, turns.Count - 1)];
        foreach (ChatResponseUpdate update in turn)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return update;
            await Task.Yield();
        }
        if (holdUntilCancelled && index >= turns.Count - 1)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _cancelled.TrySetResult();
                throw;
            }
        }
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("ScriptedChatClient only supports streaming responses.");

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey == null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }
}
