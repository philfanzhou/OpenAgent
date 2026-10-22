using OpenAgent.Application.Capabilities.Skill;
using Xunit;

namespace OpenAgent.Application.Tests.Capabilities;

/// <summary>每测试独立缓存根目录，断言只针对本测试创建的路径，不依赖共享目录总数。</summary>
public sealed class SkillPackageCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "skill-cache-tests-" + Guid.NewGuid().ToString("N"));

    private string NewObjectKey() => "files/tenants/shared/skill-packages/" + Guid.NewGuid().ToString("N");

    private static Task WritePackageAsync(string directory, CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(Path.Combine(directory, "SKILL.md"), "# cached skill", cancellationToken);

    [Fact]
    public async Task SameTenantKeyAndHash_SecondGetSkipsMaterialization()
    {
        SkillPackageCache cache = new(_root);
        int materializations = 0;
        Task<SkillPackageCache.Lease> GetAsync() => cache.GetOrMaterializeAsync(
            "tenant", "key-1", "sha-1",
            async (directory, ct) => { materializations++; await WritePackageAsync(directory, ct); },
            CancellationToken.None);
        using SkillPackageCache.Lease first = await GetAsync();
        using SkillPackageCache.Lease second = await GetAsync();
        Assert.False(first.Ephemeral);
        Assert.Equal(first.Path, second.Path);
        Assert.Equal(1, materializations);
        Assert.True(File.Exists(Path.Combine(second.Path, "SKILL.md")));
    }

    [Fact]
    public async Task DifferentHashOrTenant_MaterializesAgain()
    {
        SkillPackageCache cache = new(_root);
        int materializations = 0;
        string objectKey = NewObjectKey();
        Task<SkillPackageCache.Lease> GetAsync(string tenant, string sha) => cache.GetOrMaterializeAsync(
            tenant, objectKey, sha,
            async (directory, ct) => { materializations++; await WritePackageAsync(directory, ct); },
            CancellationToken.None);
        using SkillPackageCache.Lease first = await GetAsync("tenant-a", "sha-1");
        using SkillPackageCache.Lease otherHash = await GetAsync("tenant-a", "sha-2");
        using SkillPackageCache.Lease otherTenant = await GetAsync("tenant-b", "sha-1");
        Assert.Equal(3, materializations);
        Assert.Equal(3, new[] { first.Path, otherHash.Path, otherTenant.Path }.Distinct().Count());
    }

    [Fact]
    public async Task MissingPackageHash_IsEphemeralAndDeletedWithLease()
    {
        SkillPackageCache cache = new(_root);
        int materializations = 0;
        Task<SkillPackageCache.Lease> GetAsync() => cache.GetOrMaterializeAsync(
            "tenant", "key-e", null,
            async (directory, ct) => { materializations++; await WritePackageAsync(directory, ct); },
            CancellationToken.None);
        using SkillPackageCache.Lease first = await GetAsync();
        using SkillPackageCache.Lease second = await GetAsync();
        Assert.True(first.Ephemeral);
        Assert.True(second.Ephemeral);
        Assert.NotEqual(first.Path, second.Path);
        Assert.Equal(2, materializations);
        string firstPath = first.Path;
        first.Dispose();
        Assert.False(Directory.Exists(firstPath));
        Assert.True(Directory.Exists(second.Path));
    }

    [Fact]
    public async Task FailedMaterialization_IsNotCachedAndCanBeRetried()
    {
        SkillPackageCache cache = new(_root);
        string objectKey = NewObjectKey();
        int materializations = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cache.GetOrMaterializeAsync("tenant", objectKey, "sha-1",
                (_, _) => { materializations++; throw new InvalidOperationException("download failed"); },
                CancellationToken.None));
        using SkillPackageCache.Lease recovered = await cache.GetOrMaterializeAsync(
            "tenant", objectKey, "sha-1",
            async (directory, ct) => { materializations++; await WritePackageAsync(directory, ct); },
            CancellationToken.None);
        Assert.Equal(2, materializations);
        Assert.False(recovered.Ephemeral);
        Assert.True(File.Exists(Path.Combine(recovered.Path, "SKILL.md")));
        // 失败 staging 未留存：本测试根目录中只有重试成功的 final 条目。
        Assert.Single(Directory.GetDirectories(_root));
    }

    [Fact]
    public async Task ConcurrentSameKey_AllLeasesGetCommittedDirectory()
    {
        SkillPackageCache cache = new(_root);
        string objectKey = NewObjectKey();
        Task<SkillPackageCache.Lease>[] calls =
        [
            cache.GetOrMaterializeAsync("tenant", objectKey, "sha-1", WritePackageAsync, CancellationToken.None),
            cache.GetOrMaterializeAsync("tenant", objectKey, "sha-1", WritePackageAsync, CancellationToken.None),
            cache.GetOrMaterializeAsync("tenant", objectKey, "sha-1", WritePackageAsync, CancellationToken.None)
        ];
        SkillPackageCache.Lease[] leases = await Task.WhenAll(calls);
        Assert.All(leases, lease => Assert.False(lease.Ephemeral));
        Assert.Single(leases.Select(lease => lease.Path).Distinct());
        Assert.True(File.Exists(Path.Combine(leases[0].Path, "SKILL.md")));
        foreach (SkillPackageCache.Lease lease in leases)
        {
            lease.Dispose();
        }
    }

    [Fact]
    public async Task ActiveLease_SurvivesEvictionUntilReleased()
    {
        // 上限 4：持有包 A 的租约，继续物化超过上限的其他包（每轮租约即释放）。
        SkillPackageCache cache = new(_root, maxEntries: 4);
        using SkillPackageCache.Lease active = await cache.GetOrMaterializeAsync(
            "tenant", NewObjectKey(), "sha-active", WritePackageAsync, CancellationToken.None);
        for (int i = 0; i < 8; i++)
        {
            using SkillPackageCache.Lease filler = await cache.GetOrMaterializeAsync(
                "tenant", NewObjectKey(), "sha-" + i, WritePackageAsync, CancellationToken.None);
        }
        // 引用中的条目不得被淘汰：目录仍可读，且再次获取命中缓存。
        Assert.True(File.Exists(Path.Combine(active.Path, "SKILL.md")),
            "active lease must not be evicted while referenced");
        using SkillPackageCache.Lease reacquired = await cache.GetOrMaterializeAsync(
            "tenant", NewObjectKey(), "sha-other", WritePackageAsync, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(active.Path, "SKILL.md")));
        // 释放后条目变为可淘汰；继续物化后零引用条目被回收，目录数量有界。
        active.Dispose();
        for (int i = 10; i < 14; i++)
        {
            using SkillPackageCache.Lease filler = await cache.GetOrMaterializeAsync(
                "tenant", NewObjectKey(), "sha-late-" + i, WritePackageAsync, CancellationToken.None);
        }
        Assert.True(Directory.GetDirectories(_root).Length <= 8,
            "eviction must bound committed entries; active release makes the entry collectable");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }
}
