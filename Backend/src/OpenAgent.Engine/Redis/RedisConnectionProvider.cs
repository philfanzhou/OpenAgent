using OpenAgent.Engine.Abstractions;
using StackExchange.Redis;

namespace OpenAgent.Engine.Redis;

/// <summary>
/// Thin wrapper over the DI-provided <see cref="IConnectionMultiplexer"/> (registered by Agent.Core).
/// No self-managed connection, no lazy connect, no 5s throttle reconnect, no event hookups:
/// ConnectionMultiplexer reconnects automatically and AbortOnConnectFail=false queues commands.
/// Connection may be null (island mode) — all read/write operations degrade to empty/false/zero.
/// </summary>
internal sealed class RedisConnectionProvider : IRedisConnectionProvider
{
    private readonly IConnectionMultiplexer? _connection;

    public RedisConnectionProvider(IConnectionMultiplexer? connection)
    {
        _connection = connection;
    }

    /// <summary>
    /// Pure read-only probe: does not trigger any reconnect attempt.
    /// (Semantic change vs. legacy provider: legacy IsAvailable triggered GetConnection() reconnect;
    /// the multiplexer auto-reconnects, so an explicit probe is unnecessary. Registrar
    /// "skip when unavailable" semantics are preserved equivalently.)
    /// </summary>
    public bool IsAvailable => _connection is { IsConnected: true };

    public IDatabase GetDatabase(int database = 0)
    {
        return _connection?.GetDatabase(database)
            ?? throw new InvalidOperationException("Redis connection not available.");
    }

    public IServer? GetServer(int database = 0)
    {
        var connection = _connection;
        if (connection == null)
        {
            return null;
        }

        var endpoint = connection.GetEndPoints().FirstOrDefault();
        if (endpoint == null)
        {
            return null;
        }

        return connection.GetServer(endpoint);
    }

    public Task<RedisValue> StringGetAsync(RedisKey key, CommandFlags flags = CommandFlags.None)
    {
        var db = _connection?.GetDatabase();
        return db != null
            ? db.StringGetAsync(key, flags)
            : Task.FromResult(RedisValue.Null);
    }

    public Task<bool> StringSetAsync(RedisKey key, RedisValue value, TimeSpan? expiry = null, CommandFlags flags = CommandFlags.None)
    {
        var db = _connection?.GetDatabase();
        return db != null
            ? db.StringSetAsync(key, value, expiry, When.Always, flags)
            : Task.FromResult(false);
    }

    public Task<bool> KeyDeleteAsync(RedisKey key, CommandFlags flags = CommandFlags.None)
    {
        var db = _connection?.GetDatabase();
        return db != null
            ? db.KeyDeleteAsync(key, flags)
            : Task.FromResult(false);
    }

    public Task<RedisValue[]> SetMembersAsync(RedisKey key, CommandFlags flags = CommandFlags.None)
    {
        var db = _connection?.GetDatabase();
        return db != null
            ? db.SetMembersAsync(key, flags)
            : Task.FromResult(Array.Empty<RedisValue>());
    }

    public Task<bool> SetAddAsync(RedisKey key, RedisValue value, CommandFlags flags = CommandFlags.None)
    {
        var db = _connection?.GetDatabase();
        return db != null
            ? db.SetAddAsync(key, value, flags)
            : Task.FromResult(false);
    }

    public Task<TimeSpan> PingAsync(CommandFlags flags = CommandFlags.None)
    {
        var db = _connection?.GetDatabase();
        return db != null
            ? db.PingAsync(flags)
            : Task.FromResult(TimeSpan.Zero);
    }

    public RedisValue StringGet(RedisKey key, CommandFlags flags = CommandFlags.None)
    {
        return _connection?.GetDatabase().StringGet(key, flags) ?? RedisValue.Null;
    }

    public void Subscribe(RedisChannel channel, Action<RedisChannel, RedisValue> handler)
    {
        var subscriber = _connection?.GetSubscriber();
        if (subscriber != null)
        {
            subscriber.Subscribe(channel, handler);
        }
    }

    public void Dispose()
    {
        // No self-owned resources. ConnectionMultiplexer lifetime is managed by the DI container.
    }
}
