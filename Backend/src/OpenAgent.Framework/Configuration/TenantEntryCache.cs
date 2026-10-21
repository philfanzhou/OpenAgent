using System.Collections.Concurrent;

namespace OpenAgent.Framework.Configuration;

/// <summary>
/// 配置读取的进程内 TTL 缓存：租户级配置每轮聊天都会重复读取，30 秒窗口内
/// 直接命中内存并缓存负结果；写入/删除路径必须同步失效，保证同进程内读写一致。
/// </summary>
internal sealed class TenantEntryCache
{
    private static readonly TimeSpan TimeToLive = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<(string TenantId, string Id), Entry> _entries = new();

    public async Task<T?> GetOrAddAsync<T>(
        string tenantId,
        string id,
        Func<CancellationToken, Task<T?>> read,
        CancellationToken cancellationToken) where T : class
    {
        (string TenantId, string Id) key = (tenantId, id);
        if (_entries.TryGetValue(key, out Entry cached) && cached.Expires > DateTimeOffset.UtcNow)
        {
            return cached.Value as T;
        }
        T? value = await read(cancellationToken).ConfigureAwait(false);
        _entries[key] = new Entry(DateTimeOffset.UtcNow.Add(TimeToLive), value);
        return value;
    }

    public void Invalidate(string tenantId, string id) => _entries.TryRemove((tenantId, id), out _);

    private readonly record struct Entry(DateTimeOffset Expires, object? Value);
}
