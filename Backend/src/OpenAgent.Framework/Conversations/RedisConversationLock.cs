using Microsoft.Extensions.Logging;
using OpenAgent.Contracts.Conversation;
using StackExchange.Redis;

namespace OpenAgent.Framework;

/// <summary>
/// Redis distributed lock for serializing one conversation across Engine nodes.
/// Ownership is verified by Lua on heartbeat extension and release.
/// </summary>
internal sealed class RedisConversationLock(
    IConnectionMultiplexer connection,
    ILogger<RedisConversationLock> logger) : IConversationLock
{
    private const string ExtendScript = """
        local current = redis.call('GET', KEYS[1])
        if current == ARGV[1] then return redis.call('PEXPIRE', KEYS[1], ARGV[2]) end
        return 0
        """;
    private const string ReleaseScript = """
        local current = redis.call('GET', KEYS[1])
        if current == ARGV[1] then return redis.call('DEL', KEYS[1]) end
        return 0
        """;

    public async Task<IConversationLockHandle?> TryAcquireAsync(
        string tenantId, string conversationId, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        string key = $"openagent:conversation-lock:{tenantId}:{conversationId}";
        string token = Guid.NewGuid().ToString("N");
        long expiresAt = Environment.TickCount64 + (long)ttl.TotalMilliseconds;
        bool acquired = await connection.GetDatabase().StringSetAsync(key, token, ttl, When.NotExists)
            .ConfigureAwait(false);
        return acquired
            ? new Handle(connection, logger, key, token, tenantId, conversationId, ttl, expiresAt)
            : null;
    }

    private sealed class Handle : IConversationLockHandle
    {
        private readonly IConnectionMultiplexer _connection;
        private readonly ILogger _logger;
        private readonly string _key;
        private readonly TimeSpan _ttl;
        private readonly CancellationTokenSource _heartbeatCancellation = new();
        private readonly Task _heartbeat;
        private int _disposed;

        private int _lost;
        private long _expiresAt;
        public Handle(
            IConnectionMultiplexer connection, ILogger logger, string key, string ownerToken,
            string tenantId, string conversationId, TimeSpan ttl, long expiresAt)
        {
            _connection = connection;
            _logger = logger;
            _key = key;
            OwnerToken = ownerToken;
            TenantId = tenantId;
            ConversationId = conversationId;
            _ttl = ttl;
            _expiresAt = expiresAt;
            _heartbeat = HeartbeatAsync(_heartbeatCancellation.Token);
        }

        public string TenantId { get; }
        public string ConversationId { get; }
        public string OwnerToken { get; }
        public bool IsHeld
        {
            get
            {
                if (Environment.TickCount64 >= Volatile.Read(ref _expiresAt))
                {
                    Interlocked.Exchange(ref _lost, 1);
                }
                return Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _lost) == 0;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            _heartbeatCancellation.Cancel();
            try { await _heartbeat.ConfigureAwait(false); } catch (OperationCanceledException) { }
            _heartbeatCancellation.Dispose();
            try
            {
                await _connection.GetDatabase().ScriptEvaluateAsync(
                    ReleaseScript, [_key], [OwnerToken]).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Conversation lock release failed for {ConversationId}", ConversationId);
            }
        }

        private async Task HeartbeatAsync(CancellationToken cancellationToken)
        {
            TimeSpan interval = TimeSpan.FromMilliseconds(Math.Max(1000, _ttl.TotalMilliseconds / 3));
            // 瞬时抖动（网络闪断、单条命令超时）不应立刻放弃锁：连续失败到累计约一个 TTL
            // 才判定失效；期间任何一次成功续期都会重置计数。达到阈值时锁的剩余 TTL 已趋近
            // 于零，与真实持锁状态一致，不会扩大与其他节点的冲突窗口。
            const int MaxConsecutiveFailures = 3;
            int failures = 0;
            try
            {
                while (true)
                {
                    await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                    if (!IsHeld)
                    {
                        return;
                    }
                    try
                    {
                        long expiresAt = Environment.TickCount64 + (long)_ttl.TotalMilliseconds;
                        RedisResult result = await _connection.GetDatabase().ScriptEvaluateAsync(
                            ExtendScript, [_key], [OwnerToken, (long)_ttl.TotalMilliseconds]).ConfigureAwait(false);
                        if ((long)result == 0)
                        {
                            Interlocked.Exchange(ref _lost, 1);
                            _logger.LogWarning("Conversation lock was lost for {ConversationId}", ConversationId);
                            return;
                        }
                        failures = 0;
                        Volatile.Write(ref _expiresAt, expiresAt);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        failures++;
                        if (failures >= MaxConsecutiveFailures)
                        {
                            Interlocked.Exchange(ref _lost, 1);
                            _logger.LogWarning(exception, "Conversation lock heartbeat failed {Failures} times for {ConversationId}", failures, ConversationId);
                            return;
                        }
                        _logger.LogWarning(exception, "Conversation lock heartbeat failed once ({Failures}) for {ConversationId}; retrying", failures, ConversationId);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }
    }
}
