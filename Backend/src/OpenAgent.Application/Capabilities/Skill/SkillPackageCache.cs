using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace OpenAgent.Application.Capabilities.Skill;

/// <summary>
/// 进程级 Skill 包内容缓存：以（租户、对象键、包哈希）为键把物化并校验过的包目录
/// 留在本地，追问轮次直接复用，不再每轮从对象存储逐文件下载与校验。键含包哈希，
/// 内容寻址使缓存天然随发布失效；未登记哈希的记录不缓存（无法判定内容是否被覆盖）。
/// </summary>
internal static class SkillPackageCache
{
    private const string RootName = "openagent-skill-cache";
    private const string MarkerFileName = ".openagent-skill-cache";
    private const string StagingPrefix = "staging-";
    private const int MaxEntries = 256;
    private static readonly TimeSpan StagingAge = TimeSpan.FromHours(1);

    /// <summary>物化结果：缓存目录跨轮复用；一次性目录须由调用方随轮释放。</summary>
    internal readonly record struct SkillPackageMaterialization(string Path, bool Ephemeral);

    internal static string Root => Path.Combine(Path.GetTempPath(), RootName);

    /// <summary>命中缓存时直接返回包目录；未命中时在独立 staging 目录物化并校验后原子提交。</summary>
    internal static async Task<SkillPackageMaterialization> GetOrMaterializeAsync(
        string tenantId,
        string objectKey,
        string? packageSha256,
        Func<string, CancellationToken, Task> materialize,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(packageSha256))
        {
            // 没有包哈希就无法内容寻址：物化结果可能过期，退回一次性目录。
            string ephemeral = Path.Combine(Root, StagingPrefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ephemeral);
            try
            {
                await materialize(ephemeral, cancellationToken).ConfigureAwait(false);
                return new(ephemeral, Ephemeral: true);
            }
            catch
            {
                TryDelete(ephemeral);
                throw;
            }
        }
        string key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            tenantId + "\0" + objectKey + "\0" + packageSha256)));
        string root = Root;
        string final = Path.Combine(root, key);
        Directory.CreateDirectory(root);
        if (IsCached(final))
        {
            Touch(final);
            return new(final, Ephemeral: false);
        }
        string staging = Path.Combine(root, StagingPrefix + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            await materialize(staging, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(staging, MarkerFileName),
                DateTimeOffset.UtcNow.ToString("O"), cancellationToken).ConfigureAwait(false);
            try
            {
                Directory.Move(staging, final);
            }
            catch (IOException) when (IsCached(final))
            {
                // 另一轮已完成同键提交：以缓存为准，丢弃本次 staging。
                TryDelete(staging);
            }
            catch (UnauthorizedAccessException) when (IsCached(final))
            {
                TryDelete(staging);
            }
            EvictExcess(root);
            return new(final, Ephemeral: false);
        }
        catch
        {
            TryDelete(staging);
            throw;
        }
    }

    private static bool IsCached(string directory) =>
        Directory.Exists(directory) && File.Exists(Path.Combine(directory, MarkerFileName));

    private static void Touch(string directory)
    {
        try
        {
            File.SetLastWriteTimeUtc(Path.Combine(directory, MarkerFileName), DateTime.UtcNow);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void EvictExcess(string root)
    {
        try
        {
            List<(string Path, DateTime Used)> entries = [];
            foreach (string directory in Directory.EnumerateDirectories(root))
            {
                string name = Path.GetFileName(directory);
                if (name.StartsWith(StagingPrefix, StringComparison.Ordinal))
                {
                    // 陈旧 staging 只可能来自中断的物化；宽限期后回收。
                    if (Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow - StagingAge)
                    {
                        TryDelete(directory);
                    }
                    continue;
                }
                string marker = Path.Combine(directory, MarkerFileName);
                if (File.Exists(marker))
                {
                    entries.Add((directory, File.GetLastWriteTimeUtc(marker)));
                }
            }
            foreach ((string path, _) in entries.OrderBy(entry => entry.Used).Take(Math.Max(0, entries.Count - MaxEntries)))
            {
                TryDelete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
