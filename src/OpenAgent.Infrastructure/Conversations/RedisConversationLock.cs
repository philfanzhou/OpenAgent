using Microsoft.Extensions.Logging;
using OpenAgent.Contracts.Conversation;
using StackExchange.Redis;

namespace OpenAgent.Infrastructure;

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
        bool acquired = await connection.GetDatabase().StringSetAsync(key, token, ttl, When.NotExists)
            .ConfigureAwait(false);
        return acquired
            ? new Handle(connection, logger, key, token, tenantId, conversationId, ttl)
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

        public Handle(
            IConnectionMultiplexer connection, ILogger logger, string key, string ownerToken,
            string tenantId, string conversationId, TimeSpan ttl)
        {
            _connection = connection;
            _logger = logger;
            _key = key;
            OwnerToken = ownerToken;
            TenantId = tenantId;
            ConversationId = conversationId;
            _ttl = ttl;
            _heartbeat = HeartbeatAsync(_heartbeatCancellation.Token);
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
            try
            {
                while (true)
                {
                    await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                    RedisResult result = await _connection.GetDatabase().ScriptEvaluateAsync(
                        ExtendScript, [_key], [OwnerToken, (long)_ttl.TotalMilliseconds]).ConfigureAwait(false);
                    if ((long)result == 0)
                    {
                        _logger.LogWarning("Conversation lock was lost for {ConversationId}", ConversationId);
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Conversation lock heartbeat failed for {ConversationId}", ConversationId);
            }
        }
    }
}
