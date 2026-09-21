using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Core.Conversation;

/// <summary>
/// 一轮对话的会话锁：加载历史时获取，在完成/失败/释放三路径下恰好释放一次；
/// 锁贯穿整个模型调用，保证同一会话不被并发处理。
/// </summary>
internal sealed class ConversationTurnLock(IConversationLock conversationLock)
{
    private IConversationLockHandle? _handle;
    private bool _released;

    internal async Task AcquireAsync(
        string tenantId,
        string conversationId,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        _handle = await conversationLock.TryAcquireAsync(
            tenantId,
            conversationId,
            ttl,
            cancellationToken).ConfigureAwait(false);
        if (_handle == null)
        {
            throw new AgentException(
                AgentErrorCode.Conflict,
                "Conversation is being processed by another request");
        }
    }

    internal ValueTask ReleaseAsync()
    {
        if (_released)
        {
            return ValueTask.CompletedTask;
        }

        _released = true;
        if (_handle != null)
        {
            return _handle.DisposeAsync();
        }
        return ValueTask.CompletedTask;
    }
}
