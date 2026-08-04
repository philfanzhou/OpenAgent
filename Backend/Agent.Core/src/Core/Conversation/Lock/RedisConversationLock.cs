using Microsoft.Extensions.Logging;
using OpenAgent.Core.Conversation.Scripts;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Core.Conversation;
using StackExchange.Redis;

namespace OpenAgent.Core.Conversation.Lock;

/// <summary>
/// Redis-based distributed conversation lock using SET NX EX for acquisition,
/// Lua scripts for atomic release/extend, and a background heartbeat for long operations.
/// </summary>
internal sealed class RedisConversationLock : IConversationLock
{
    private const string LockKeyPrefix = "lock:conversation:";

    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisConversationLock> _logger;

    public RedisConversationLock(IConnectionMultiplexer redis, ILogger<RedisConversationLock> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task<IConversationLockHandle?> TryAcquireAsync(
        string tenantId,
        string conversationId,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        var key = $"{LockKeyPrefix}{tenantId}:{conversationId}";
        var token = Guid.NewGuid().ToString("N");
        var db = _redis.GetDatabase();

        var acquired = await db.StringSetAsync(key, token, ttl, when: When.NotExists);
        if (!acquired)
        {
            return null;
        }

        return new RedisConversationLockHandle(_redis, _logger, key, tenantId, conversationId, token, ttl);
    }

    /// <summary>
    /// Handle for a Redis conversation lock. Runs a heartbeat to extend TTL.
    /// </summary>
    internal sealed class RedisConversationLockHandle : IConversationLockHandle
    {
        private readonly IConnectionMultiplexer _redis;
        private readonly ILogger _logger;
        private readonly string _key;
        private readonly TimeSpan _ttl;
        private readonly CancellationTokenSource _heartbeatCts = new();
        private readonly Task _heartbeatTask;
        private int _disposed;

        public RedisConversationLockHandle(
            IConnectionMultiplexer redis,
            ILogger logger,
            string key,
            string tenantId,
            string conversationId,
            string ownerToken,
            TimeSpan ttl)
        {
            _redis = redis;
            _logger = logger;
            _key = key;
            _ttl = ttl;
            TenantId = tenantId;
            ConversationId = conversationId;
            OwnerToken = ownerToken;

            var interval = TimeSpan.FromMilliseconds(ttl.TotalMilliseconds / 3);
            _heartbeatTask = RunHeartbeatAsync(interval, _heartbeatCts.Token);
        }

        public string TenantId { get; }
        public string ConversationId { get; }
        public string OwnerToken { get; }
        public bool IsHeld => Volatile.Read(ref _disposed) == 0;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _heartbeatCts.Cancel();
            try
            {
                await _heartbeatTask;
            }
            catch
            {
                // Ignore heartbeat task exceptions during shutdown
            }

            _heartbeatCts.Dispose();
            await ReleaseAsync();
        }

        private async Task RunHeartbeatAsync(TimeSpan interval, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(interval, ct);
                    await ExtendAsync();
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
            }
            catch (Exception ex)
            {
                ConversationLog.HeartbeatFailed(_logger, ex, _key, OwnerToken[..8]);
            }
        }

        private async Task ExtendAsync()
        {
            var db = _redis.GetDatabase();
            var result = (long?)await db.ScriptEvaluateAsync(
                ConversationScripts.LockExtend,
                new RedisKey[] { _key },
                new RedisValue[] { OwnerToken, (long)_ttl.TotalMilliseconds });

            if (result == 0)
            {
                ConversationLog.ExtendFailed(_logger, _key, OwnerToken[..8]);
            }
        }

        private async Task ReleaseAsync()
        {
            try
            {
                var db = _redis.GetDatabase();
                await db.ScriptEvaluateAsync(
                    ConversationScripts.LockRelease,
                    new RedisKey[] { _key },
                    new RedisValue[] { OwnerToken });
            }
            catch (Exception ex)
            {
                ConversationLog.ReleaseFailed(_logger, ex, _key, OwnerToken[..8]);
            }
        }
    }
}
