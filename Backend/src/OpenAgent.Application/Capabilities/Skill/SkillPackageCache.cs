using System.Security.Cryptography;
using System.Text;

namespace OpenAgent.Application.Capabilities.Skill;

/// <summary>
/// 进程级 Skill 包内容缓存：以（租户、对象键、包哈希）为键把物化并校验过的包目录
/// 留在本地，追问轮次直接复用，不再每轮从对象存储逐文件下载与校验。键含包哈希，
/// 内容寻址使缓存天然随发布失效；未登记哈希的记录不缓存（无法判定内容是否被覆盖）。
/// 根目录按进程隔离（含进程 ID），淘汰与引用获取在同一进程内锁下同步，只回收零引用
/// 条目；轮次以租约持有引用，活跃包不会被淘汰删除。
/// </summary>
internal sealed class SkillPackageCache
{
    private const string RootName = "openagent-skill-cache";
    private const string MarkerFileName = ".openagent-skill-cache";
    private const string StagingPrefix = "staging-";
    private const int MaxEntries = 256;
    private static readonly TimeSpan StagingAge = TimeSpan.FromHours(1);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> InitializedRoots = new();

    /// <summary>进程内共享实例；根目录按进程隔离，测试可构造独立根目录的实例。</summary>
    internal static SkillPackageCache Shared { get; } = new();

    private readonly string _root;
    private readonly int _maxEntries;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    internal string Root => _root;

    public SkillPackageCache()
        : this(Path.Combine(Path.GetTempPath(), RootName, "p-" + Environment.ProcessId), MaxEntries)
    {
    }

    public SkillPackageCache(string root, int maxEntries = MaxEntries)
    {
        _root = root;
        _maxEntries = maxEntries;
        // 根目录按进程隔离（含进程 ID）；同进程内多次构造共享初始化，只在首次构造时
        // 清掉上一进程（进程 ID 复用）的残留，避免并发实例删除彼此正在使用的目录。
        InitializedRoots.GetOrAdd(root, static path =>
        {
            TryDelete(path);
            return true;
        });
        Directory.CreateDirectory(root);
    }

    /// <summary>物化结果租约：缓存条目在租约释放前不会被淘汰；一次性目录随租约释放删除。</summary>
    internal sealed class Lease : IDisposable
    {
        private SkillPackageCache? _cache;
        private Entry? _entry;
        private readonly string? _ephemeralPath;
        private int _disposed;

        internal Lease(SkillPackageCache cache, Entry entry)
        {
            _cache = cache;
            _entry = entry;
            Path = entry.Path;
        }

        internal Lease(string ephemeralPath)
        {
            _ephemeralPath = ephemeralPath;
            Path = ephemeralPath;
        }

        internal string Path { get; }

        internal bool Ephemeral => _ephemeralPath is not null;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            if (_ephemeralPath is not null)
            {
                TryDelete(_ephemeralPath);
                return;
            }
            SkillPackageCache? cache = Interlocked.Exchange(ref _cache, null);
            Entry? entry = Interlocked.Exchange(ref _entry, null);
            if (cache is not null && entry is not null)
            {
                cache.Release(entry);
            }
        }
    }

    internal sealed class Entry
    {
        internal required string Path { get; init; }
        internal int References { get; set; }
        internal long LastUsed { get; set; }
    }

    /// <summary>命中缓存时返回持引用的租约；未命中时在独立 staging 目录物化并校验后原子提交。</summary>
    internal async Task<Lease> GetOrMaterializeAsync(
        string tenantId,
        string objectKey,
        string? packageSha256,
        Func<string, CancellationToken, Task> materialize,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(packageSha256))
        {
            // 没有包哈希就无法内容寻址：物化结果可能过期，退回一次性目录，随租约释放删除。
            string ephemeral = Path.Combine(_root, StagingPrefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ephemeral);
            try
            {
                await materialize(ephemeral, cancellationToken).ConfigureAwait(false);
                return new Lease(ephemeral);
            }
            catch
            {
                TryDelete(ephemeral);
                throw;
            }
        }
        string id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            tenantId + "\0" + objectKey + "\0" + packageSha256)));
        Lease? acquired = TryAcquire(id);
        if (acquired is not null)
        {
            return acquired;
        }
        string final = Path.Combine(_root, id);
        string staging = Path.Combine(_root, StagingPrefix + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            // 下载与校验在锁外执行；提交与登记在锁内完成。
            await materialize(staging, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(staging, MarkerFileName),
                DateTimeOffset.UtcNow.ToString("O"), cancellationToken).ConfigureAwait(false);
            try
            {
                Directory.Move(staging, final);
            }
            catch (IOException) when (File.Exists(Path.Combine(final, MarkerFileName)))
            {
                // 另一轮已完成同键提交：以缓存为准，丢弃本次 staging。
                TryDelete(staging);
            }
            catch (UnauthorizedAccessException) when (File.Exists(Path.Combine(final, MarkerFileName)))
            {
                TryDelete(staging);
            }
            Lease lease = Commit(id, final);
            EvictExcess();
            return lease;
        }
        catch
        {
            TryDelete(staging);
            throw;
        }
    }

    private Lease? TryAcquire(string id)
    {
        lock (_gate)
        {
            // 登记表是唯一事实源：条目只在提交后登记，引用获取与淘汰标记在同一临界区同步。
            if (_entries.TryGetValue(id, out Entry? entry))
            {
                entry.References++;
                entry.LastUsed = DateTimeOffset.UtcNow.UtcTicks;
                return new Lease(this, entry);
            }
            return null;
        }
    }

    private Lease Commit(string id, string final)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(id, out Entry? existing))
            {
                existing.References++;
                existing.LastUsed = DateTimeOffset.UtcNow.UtcTicks;
                return new Lease(this, existing);
            }
            Entry entry = new() { Path = final, References = 1, LastUsed = DateTimeOffset.UtcNow.UtcTicks };
            _entries.Add(id, entry);
            return new Lease(this, entry);
        }
    }

    private void Release(Entry entry)
    {
        lock (_gate)
        {
            entry.References--;
        }
    }

    /// <summary>超出条目上限时按最久未使用回收；只回收零引用条目，删除在锁外执行。</summary>
    private void EvictExcess()
    {
        List<string> doomed = [];
        lock (_gate)
        {
            if (_entries.Count <= _maxEntries)
            {
                return;
            }
            foreach (KeyValuePair<string, Entry> pair in _entries
                .OrderByDescending(item => item.Value.LastUsed)
                .Skip(_maxEntries))
            {
                if (pair.Value.References == 0)
                {
                    doomed.Add(pair.Value.Path);
                    _entries.Remove(pair.Key);
                }
            }
        }
        foreach (string path in doomed)
        {
            TryDelete(path);
        }
        SweepStaleStaging();
    }

    private void SweepStaleStaging()
    {
        try
        {
            foreach (string directory in Directory.EnumerateDirectories(_root))
            {
                if (Path.GetFileName(directory).StartsWith(StagingPrefix, StringComparison.Ordinal)
                    && Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow - StagingAge)
                {
                    TryDelete(directory);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
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
