using OpenAgent.Engine.Abstractions;
using StackExchange.Redis;

namespace OpenAgent.Engine.Tests;

internal sealed class FakeRedisConnectionProvider : IRedisConnectionProvider
{
    private readonly Dictionary<string, string> _strings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _sets = new(StringComparer.OrdinalIgnoreCase);

    public bool IsAvailable { get; set; } = true;

    internal TimeSpan? LastStringExpiry { get; private set; }

    public IServer? GetServer(int database = 0) => null;

    public IDatabase GetDatabase(int database = 0) => throw new NotSupportedException();

    public Task<RedisValue> StringGetAsync(RedisKey key, CommandFlags flags = CommandFlags.None) => Task.FromResult(StringGet(key, flags));

    public Task<bool> StringSetAsync(RedisKey key, RedisValue value, TimeSpan? expiry = null, CommandFlags flags = CommandFlags.None)
    {
        _strings[key!] = value.ToString();
        LastStringExpiry = expiry;
        return Task.FromResult(true);
    }

    public Task<bool> KeyDeleteAsync(RedisKey key, CommandFlags flags = CommandFlags.None) => Task.FromResult(_strings.Remove(key!));

    public Task<RedisValue[]> SetMembersAsync(RedisKey key, CommandFlags flags = CommandFlags.None) =>
        Task.FromResult(
            _sets.TryGetValue(key!, out HashSet<string>? members)
                ? members.Select(member => (RedisValue)member).ToArray()
                : []);

    public Task<bool> SetAddAsync(RedisKey key, RedisValue value, CommandFlags flags = CommandFlags.None)
    {
        if (!_sets.TryGetValue(key!, out HashSet<string>? members))
        {
            members = new HashSet<string>(StringComparer.Ordinal);
            _sets[key!] = members;
        }

        return Task.FromResult(members.Add(value.ToString()));
    }

    public Task<bool> SetRemoveAsync(RedisKey key, RedisValue value, CommandFlags flags = CommandFlags.None) =>
        Task.FromResult(
            _sets.TryGetValue(key!, out HashSet<string>? members) &&
            members.Remove(value.ToString()));

    public Task<TimeSpan> PingAsync(CommandFlags flags = CommandFlags.None) => Task.FromResult(TimeSpan.FromMilliseconds(1));

    public RedisValue StringGet(RedisKey key, CommandFlags flags = CommandFlags.None) =>
        _strings.TryGetValue(key!, out var value) ? value : RedisValue.Null;

    public void Subscribe(RedisChannel channel, Action<RedisChannel, RedisValue> handler)
    {
    }

    public void Dispose()
    {
    }

    public void SetString(string key, string value)
    {
        _strings[key] = value;
    }
}
