using StackExchange.Redis;

namespace OpenAgent.Engine.Abstractions;

internal interface IRedisConnectionProvider : IDisposable
{
    bool IsAvailable { get; }
    IServer? GetServer(int database = 0);
    IDatabase GetDatabase(int database = 0);

    Task<RedisValue> StringGetAsync(RedisKey key, CommandFlags flags = CommandFlags.None);
    Task<bool> StringSetAsync(RedisKey key, RedisValue value, TimeSpan? expiry = null, CommandFlags flags = CommandFlags.None);
    Task<bool> KeyDeleteAsync(RedisKey key, CommandFlags flags = CommandFlags.None);
    Task<RedisValue[]> SetMembersAsync(RedisKey key, CommandFlags flags = CommandFlags.None);
    Task<bool> SetAddAsync(RedisKey key, RedisValue value, CommandFlags flags = CommandFlags.None);
    Task<TimeSpan> PingAsync(CommandFlags flags = CommandFlags.None);

    RedisValue StringGet(RedisKey key, CommandFlags flags = CommandFlags.None);
    void Subscribe(RedisChannel channel, Action<RedisChannel, RedisValue> handler);
}
