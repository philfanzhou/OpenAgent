using OpenAgent.Application.Capabilities.Skill;
using Xunit;

namespace OpenAgent.Application.Tests.Capabilities;

public sealed class SkillPackageCacheTests
{
    private static string NewObjectKey() => "files/tenants/shared/skill-packages/" + Guid.NewGuid().ToString("N");

    private static Task WritePackageAsync(string directory, CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(Path.Combine(directory, "SKILL.md"), "# cached skill", cancellationToken);

    [Fact]
    public async Task SameTenantKeyAndHash_SecondGetSkipsMaterialization()
    {
        string objectKey = NewObjectKey();
        int materializations = 0;
        Task<SkillPackageCache.SkillPackageMaterialization> GetAsync() =>
            SkillPackageCache.GetOrMaterializeAsync("tenant", objectKey, "sha-1",
                async (directory, ct) =>
                {
                    materializations++;
                    await WritePackageAsync(directory, ct);
                }, CancellationToken.None);
        SkillPackageCache.SkillPackageMaterialization first = await GetAsync();
        SkillPackageCache.SkillPackageMaterialization second = await GetAsync();
        Assert.False(first.Ephemeral);
        Assert.Equal(first.Path, second.Path);
        Assert.Equal(1, materializations);
        Assert.True(File.Exists(Path.Combine(second.Path, "SKILL.md")));
    }

    [Fact]
    public async Task DifferentHashOrTenant_MaterializesAgain()
    {
        string objectKey = NewObjectKey();
        int materializations = 0;
        Task<string> GetAsync(string tenant, string sha) =>
            SkillPackageCache.GetOrMaterializeAsync(tenant, objectKey, sha,
                async (directory, ct) =>
                {
                    materializations++;
                    await WritePackageAsync(directory, ct);
                }, CancellationToken.None).ContinueWith(task => task.Result.Path);
        string first = await GetAsync("tenant-a", "sha-1");
        string otherHash = await GetAsync("tenant-a", "sha-2");
        string otherTenant = await GetAsync("tenant-b", "sha-1");
        Assert.Equal(3, materializations);
        Assert.NotEqual(first, otherHash);
        Assert.NotEqual(first, otherTenant);
        Assert.NotEqual(otherHash, otherTenant);
    }

    [Fact]
    public async Task MissingPackageHash_IsEphemeralAndAlwaysMaterializes()
    {
        string objectKey = NewObjectKey();
        int materializations = 0;
        Task<SkillPackageCache.SkillPackageMaterialization> GetAsync() =>
            SkillPackageCache.GetOrMaterializeAsync("tenant", objectKey, null,
                async (directory, ct) =>
                {
                    materializations++;
                    await WritePackageAsync(directory, ct);
                }, CancellationToken.None);
        SkillPackageCache.SkillPackageMaterialization first = await GetAsync();
        SkillPackageCache.SkillPackageMaterialization second = await GetAsync();
        Assert.True(first.Ephemeral);
        Assert.True(second.Ephemeral);
        Assert.NotEqual(first.Path, second.Path);
        Assert.Equal(2, materializations);
    }

    [Fact]
    public async Task FailedMaterialization_IsNotCachedAndStagingRemoved()
    {
        string objectKey = NewObjectKey();
        string root = SkillPackageCache.Root;
        int before = Directory.Exists(root) ? Directory.GetDirectories(root).Length : 0;
        int materializations = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SkillPackageCache.GetOrMaterializeAsync("tenant", objectKey, "sha-1",
                (_, _) =>
                {
                    materializations++;
                    throw new InvalidOperationException("download failed");
                }, CancellationToken.None));
        SkillPackageCache.SkillPackageMaterialization recovered = await SkillPackageCache.GetOrMaterializeAsync(
            "tenant", objectKey, "sha-1",
            async (directory, ct) => { materializations++; await WritePackageAsync(directory, ct); },
            CancellationToken.None);
        Assert.Equal(2, materializations);
        Assert.False(recovered.Ephemeral);
        Assert.True(File.Exists(Path.Combine(recovered.Path, "SKILL.md")));
        Assert.Equal(before + 1, Directory.GetDirectories(root).Length);
    }

    [Fact]
    public async Task ConcurrentSameKey_BothCallsGetCommittedDirectory()
    {
        string objectKey = NewObjectKey();
        Task<SkillPackageCache.SkillPackageMaterialization>[] calls = [GetAsync(), GetAsync(), GetAsync()];
        SkillPackageCache.SkillPackageMaterialization[] results = await Task.WhenAll(calls);
        Assert.All(results, result => Assert.False(result.Ephemeral));
        Assert.Single(results.Select(result => result.Path).Distinct());
        Assert.True(File.Exists(Path.Combine(results[0].Path, "SKILL.md")));

        Task<SkillPackageCache.SkillPackageMaterialization> GetAsync()
        {
            return SkillPackageCache.GetOrMaterializeAsync("tenant", objectKey, "sha-1", WritePackageAsync, CancellationToken.None);
        }
    }
}
