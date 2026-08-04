using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Core.Impl;
using OpenAgent.Core.Conversation.Lock;
using StackExchange.Redis;
using Xunit;
using Xunit.Sdk;

namespace OpenAgent.Core.Tests.Conversation;

/// <summary>
/// Integration tests for RedisConversationLock.
/// Requires a local Redis at localhost:6379; tests are skipped automatically when Redis is unavailable.
/// </summary>
[Trait("Category", "Integration")]
public class RedisConversationLockTests : IAsyncLifetime
{
    private IConnectionMultiplexer? _redis;
    private RedisConversationLock? _sut;

    public async Task InitializeAsync()
    {
        try
        {
            _redis = await ConnectionMultiplexer.ConnectAsync(
                "localhost:6379,abortConnect=false,connectTimeout=500");
            if (!_redis.IsConnected) throw new InvalidOperationException("Redis not connected");
        }
        catch (Exception ex)
        {
            Skip.If(true, $"Redis unavailable at localhost:6379: {ex.Message}");
        }
        _sut = new RedisConversationLock(_redis!, NullLogger<RedisConversationLock>.Instance);
    }

    public async Task DisposeAsync()
    {
        if (_redis == null) return;
        try
        {
            // Clean up any leftover lock keys
            var db = _redis.GetDatabase();
            var server = _redis.GetEndPoints().FirstOrDefault();
            if (server != null)
            {
                var keys = new List<RedisKey>();
                foreach (var ep in _redis.GetEndPoints())
                {
                    var s = _redis.GetServer(ep);
                    foreach (var key in s.Keys(pattern: "lock:conversation:*"))
                    {
                        keys.Add(key);
                    }
                }
                if (keys.Count > 0)
                {
                    await db.KeyDeleteAsync(keys.ToArray());
                }
            }

            await _redis.DisposeAsync();
        }
        catch
        {
            // ignored
        }
    }

    [SkippableFact]
    public async Task TryAcquire_FreeKey_ReturnsHandle()
    {
        var handle = await _sut!.TryAcquireAsync("t1", "c1", TimeSpan.FromSeconds(30));
        Assert.NotNull(handle);
        await handle!.DisposeAsync();
    }

    [SkippableFact]
    public async Task TryAcquire_HeldKey_ReturnsNull()
    {
        var h1 = await _sut!.TryAcquireAsync("t1", "c1", TimeSpan.FromSeconds(30));
        var h2 = await _sut.TryAcquireAsync("t1", "c1", TimeSpan.FromSeconds(30));
        Assert.NotNull(h1);
        Assert.Null(h2);
        await h1!.DisposeAsync();
    }

    [SkippableFact]
    public async Task TryAcquire_AfterRelease_ReturnsNewHandle()
    {
        var h1 = await _sut!.TryAcquireAsync("t1", "c1", TimeSpan.FromSeconds(1));
        Assert.NotNull(h1);
        await h1!.DisposeAsync();
        await Task.Delay(TimeSpan.FromSeconds(2));
        var h2 = await _sut.TryAcquireAsync("t1", "c1", TimeSpan.FromSeconds(30));
        Assert.NotNull(h2);
        await h2!.DisposeAsync();
    }

    [SkippableFact]
    public async Task TryAcquire_DifferentTenantKeys_AreIndependent()
    {
        var h1 = await _sut!.TryAcquireAsync("t1", "c1", TimeSpan.FromSeconds(30));
        var h2 = await _sut.TryAcquireAsync("t2", "c1", TimeSpan.FromSeconds(30));
        Assert.NotNull(h1);
        Assert.NotNull(h2);
        await h1!.DisposeAsync();
        await h2!.DisposeAsync();
    }

    [SkippableFact]
    public async Task TryAcquire_DifferentConversationIds_AreIndependent()
    {
        var h1 = await _sut!.TryAcquireAsync("t1", "c1", TimeSpan.FromSeconds(30));
        var h2 = await _sut.TryAcquireAsync("t1", "c2", TimeSpan.FromSeconds(30));
        Assert.NotNull(h1);
        Assert.NotNull(h2);
        await h1!.DisposeAsync();
        await h2!.DisposeAsync();
    }

    [SkippableFact]
    public async Task Dispose_ReleasesLock()
    {
        var h1 = await _sut!.TryAcquireAsync("t1", "c1", TimeSpan.FromSeconds(30));
        await h1!.DisposeAsync();
        var h2 = await _sut.TryAcquireAsync("t1", "c1", TimeSpan.FromSeconds(30));
        Assert.NotNull(h2);
        await h2.DisposeAsync();
    }

    [SkippableFact]
    public async Task Release_WrongOwner_DoesNotDelete()
    {
        var h1 = await _sut!.TryAcquireAsync("t1", "c1", TimeSpan.FromSeconds(30));
        var db = _redis!.GetDatabase();

        // Execute the release-lock Lua script with a wrong owner token
        const string releaseScript = """
            local current = redis.call('GET', KEYS[1])
            if current == ARGV[1] then
                return redis.call('DEL', KEYS[1])
            else
                return 0
            end
            """;
        var result = await db.ScriptEvaluateAsync(
            releaseScript,
            new RedisKey[] { "lock:conversation:t1:c1" },
            new RedisValue[] { "wrong-token" });
        Assert.Equal(0, (long)result);

        // h1 can still be released normally
        await h1!.DisposeAsync();

        // After correct release, a new lock can be acquired
        var h2 = await _sut.TryAcquireAsync("t1", "c1", TimeSpan.FromSeconds(30));
        Assert.NotNull(h2);
        await h2!.DisposeAsync();
    }

    [SkippableFact]
    public async Task Release_CorrectOwner_DeletesKey()
    {
        var h1 = await _sut!.TryAcquireAsync("t1", "c1", TimeSpan.FromSeconds(30));
        await h1!.DisposeAsync();

        var db = _redis!.GetDatabase();
        var exists = await db.KeyExistsAsync("lock:conversation:t1:c1");
        Assert.False(exists);
    }

    [SkippableFact]
    public async Task Heartbeat_ExtendsTtl_BeforeExpiry()
    {
        // TTL=3s, heartbeat interval = 1s. Wait 4s (past original TTL) and verify the key still exists.
        var handle = await _sut!.TryAcquireAsync("t1", "c1", TimeSpan.FromSeconds(3));
        Assert.NotNull(handle);

        await Task.Delay(TimeSpan.FromSeconds(4));

        var db = _redis!.GetDatabase();
        var exists = await db.KeyExistsAsync("lock:conversation:t1:c1");
        Assert.True(exists, "Expected lock key to still exist after original TTL thanks to heartbeat");

        var ttl = await db.KeyTimeToLiveAsync("lock:conversation:t1:c1");
        Assert.NotNull(ttl);
        Assert.True(ttl!.Value.TotalSeconds > 0, $"Expected TTL > 0 but got {ttl.Value.TotalSeconds}s");

        await handle!.DisposeAsync();
    }

    [SkippableFact]
    public async Task Dispose_Idempotent_DoesNotThrow()
    {
        var handle = await _sut!.TryAcquireAsync("t1", "c1", TimeSpan.FromSeconds(30));
        Assert.NotNull(handle);
        await handle!.DisposeAsync();
        await handle.DisposeAsync();
    }
}
