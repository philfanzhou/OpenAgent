using OpenAgent.Framework.Configuration;
using Xunit;

namespace OpenAgent.Framework.Tests;

/// <summary>缓存失效与在途读取的竞争：失效前启动的读取不得把旧值/旧负结果回填进缓存。</summary>
public sealed class TenantEntryCacheTests
{
    private sealed class ManualTime : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task SlowRead_TtlStartsBeforeRead_DoesNotExtendStaleCacheWindow()
    {
        ManualTime clock = new();
        TenantEntryCache cache = new(clock);
        await cache.GetOrAddAsync("tenant", "key", ct =>
        {
            clock.Now = clock.Now.AddSeconds(31);
            return Task.FromResult<string?>("stale");
        }, default);
        Assert.Equal("fresh", await cache.GetOrAddAsync("tenant", "key", ct => Task.FromResult<string?>("fresh"), default));
    }

    private sealed class Gates
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    [Fact]
    public async Task InFlightReadBeforeSave_DoesNotRepublishOldValue()
    {
        TenantEntryCache cache = new();
        string store = "old";
        Gates gates = new();
        Task<string?> stale = cache.GetOrAddAsync("t", "k", async ct =>
        {
            string? captured = store;
            gates.Started.SetResult();
            await gates.Release.Task;
            return captured;
        }, CancellationToken.None);
        await gates.Started.Task;
        store = "new";
        cache.Invalidate("t", "k");
        gates.Release.SetResult();
        // 该调用拿到它启动时读到的值。
        Assert.Equal("old", await stale);
        // 后续请求必须读到新状态；本次读取成功发布，下一次命中缓存。
        int reads = 0;
        Task<string?> Fresh() => cache.GetOrAddAsync("t", "k", ct =>
        {
            reads++;
            return Task.FromResult<string?>(store);
        }, CancellationToken.None);
        Assert.Equal("new", await Fresh());
        Assert.Equal(1, reads);
        Assert.Equal("new", await Fresh());
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task InFlightReadBeforeDelete_DoesNotRepublishOldValue()
    {
        TenantEntryCache cache = new();
        string? store = "v1";
        Gates gates = new();
        Task<string?> stale = cache.GetOrAddAsync("t", "k", async ct =>
        {
            string? captured = store;
            gates.Started.SetResult();
            await gates.Release.Task;
            return captured;
        }, CancellationToken.None);
        await gates.Started.Task;
        store = null;
        cache.Invalidate("t", "k");
        gates.Release.SetResult();
        Assert.Equal("v1", await stale);
        int reads = 0;
        Assert.Null(await cache.GetOrAddAsync("t", "k", ct =>
        {
            reads++;
            return Task.FromResult(store);
        }, CancellationToken.None));
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task InFlightNegativeReadBeforeCreate_DoesNotCacheStaleNull()
    {
        TenantEntryCache cache = new();
        string? store = null;
        Gates gates = new();
        Task<string?> stale = cache.GetOrAddAsync("t", "k", async ct =>
        {
            string? captured = store;
            gates.Started.SetResult();
            await gates.Release.Task;
            return captured;
        }, CancellationToken.None);
        await gates.Started.Task;
        store = "created";
        cache.Invalidate("t", "k");
        gates.Release.SetResult();
        Assert.Null(await stale);
        int reads = 0;
        Assert.Equal("created", await cache.GetOrAddAsync("t", "k", ct =>
        {
            reads++;
            return Task.FromResult<string?>(store);
        }, CancellationToken.None));
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task SaveAndDelete_InvalidatePublishedValuesImmediately()
    {
        TenantEntryCache cache = new();
        string? store = "v1";
        Task<string?> Get() => cache.GetOrAddAsync("t", "k", ct => Task.FromResult<string?>(store), CancellationToken.None);
        Assert.Equal("v1", await Get());
        store = "v2";
        cache.Invalidate("t", "k");
        Assert.Equal("v2", await Get());
        store = null;
        cache.Invalidate("t", "k");
        Assert.Null(await Get());
        // 负结果在 TTL 内被缓存：store 保持 null 时不再触发读取。
        store = "should-not-be-read";
        Assert.Null(await Get());
    }
}
