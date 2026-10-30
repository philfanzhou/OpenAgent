using System.Collections.Concurrent;

namespace OpenAgent.Framework.Configuration;

/// <summary>
/// 配置读取的进程内 TTL 缓存：租户级配置每轮聊天都会重复读取，30 秒窗口内
/// 直接命中内存并缓存负结果。每键一个状态对象：失效提升代次并清空已发布值，
/// 回填的代次检查与发布在同一临界区完成，失效前启动的在途读取不会把旧值
/// （或旧负结果）重新写回缓存。
/// </summary>
internal sealed class TenantEntryCache(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private static readonly TimeSpan TimeToLive = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<(string TenantId, string Id), KeyState> _keys = new();

    public async Task<T?> GetOrAddAsync<T>(
        string tenantId,
        string id,
        Func<CancellationToken, Task<T?>> read,
        CancellationToken cancellationToken) where T : class
    {
        KeyState state = _keys.GetOrAdd((tenantId, id), _ => new KeyState());
        long generation;
        DateTimeOffset readStarted = _time.GetUtcNow();
        lock (state)
        {
            if (state.HasValue && state.Expires > _time.GetUtcNow())
            {
                return state.Published as T;
            }
            generation = state.Generation;
        }
        T? value = await read(cancellationToken).ConfigureAwait(false);
        lock (state)
        {
            // 代次不变才允许发布：期间发生过失效（保存/删除）就丢弃本次读取，
            // 让后续请求自己去读新状态；本次调用方仍拿到它读到的值。同代次下
            // 用新鲜读取覆盖过期值是安全的（除非失效，代次必然相同）。
            if (state.Generation == generation)
            {
                state.Published = value;
                state.HasValue = true;
                state.Expires = readStarted.Add(TimeToLive);
            }
        }
        return value;
    }

    public void Invalidate(string tenantId, string id)
    {
        KeyState state = _keys.GetOrAdd((tenantId, id), _ => new KeyState());
        lock (state)
        {
            state.Generation++;
            state.Published = null;
            state.HasValue = false;
            state.Expires = DateTimeOffset.MinValue;
        }
    }

    private sealed class KeyState
    {
        public long Generation { get; set; }
        public object? Published { get; set; }
        public bool HasValue { get; set; }
        public DateTimeOffset Expires { get; set; }
    }
}
