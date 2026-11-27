using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAgent.Runtime.OpenCode;
using Xunit;

namespace OpenAgent.Runtime.Tests;

/// <summary>会话状态回收：只删除不再被任何进程使用、且已过期或已失效的归属目录。</summary>
public sealed class OpenCodeSessionStateSweeperTests : IDisposable
{
    private const string Manifest = "committed-session.json";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "opencode-sweep-" + Guid.NewGuid().ToString("N"));
    private readonly string _durable;
    private readonly string _temporary;
    // 回收器读到的时间比真实时间快这么多，磁盘上的目录随之变旧。
    private TimeSpan _advanced;
    public OpenCodeSessionStateSweeperTests()
    {
        OpenCodeWorkspace.CreatePrivateDirectory(_root);
        _durable = Path.Combine(_root, "durable");
        _temporary = Path.Combine(_root, "temporary");
    }

    [Fact]
    public async Task SweepOnceAsync_NothingExpired_KeepsEveryOwner()
    {
        string committed = CreateOwner(_durable, committed: true);
        string fresh = CreateOwner(_durable);
        string leftover = CreateOwner(_temporary);
        Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(0, await Sweeper(retentionDays: 14).SweepOnceAsync(CancellationToken.None));

        Assert.Equal(3, Directory.GetDirectories(_root, "p-*", SearchOption.AllDirectories).Length);
        Assert.True(File.Exists(Path.Combine(committed, Manifest)));
        Assert.True(Directory.Exists(fresh));
        Assert.True(Directory.Exists(leftover));
    }

    [Fact]
    public async Task SweepOnceAsync_CommittedPastRetention_RemovesOnlyExpiredOwners()
    {
        string expired = CreateOwner(_durable, committed: true);
        Advance(TimeSpan.FromDays(15));
        string recent = CreateOwner(_durable, committed: true);
        // 时钟已拨快 15 天：把新检查点的写入时间也拨到「现在」，它才算刚提交。
        File.SetLastWriteTimeUtc(Path.Combine(recent, Manifest), DateTime.UtcNow.AddDays(15));
        string notes = Path.Combine(_durable, "not-a-session.txt");
        await File.WriteAllTextAsync(notes, "kept");

        Assert.Equal(1, await Sweeper(retentionDays: 14).SweepOnceAsync(CancellationToken.None));

        Assert.False(Directory.Exists(expired));
        Assert.True(File.Exists(Path.Combine(recent, Manifest)));
        Assert.Equal("kept", await File.ReadAllTextAsync(notes));
    }

    [Fact]
    public async Task SweepOnceAsync_RetentionDisabled_KeepsExpiredCommittedOwner()
    {
        string expired = CreateOwner(_durable, committed: true);
        Advance(TimeSpan.FromDays(400));

        Assert.Equal(0, await Sweeper(retentionDays: 0).SweepOnceAsync(CancellationToken.None));

        Assert.True(File.Exists(Path.Combine(expired, Manifest)));
    }

    [Theory]
    [InlineData(4, false)]
    [InlineData(6, true)]
    public async Task SweepOnceAsync_OwnerWithoutManifest_RemovedOnlyAfterTheQuietWindow(int idleMinutes, bool removed)
    {
        // 失败轮次使检查点失效或异常退出后，归属目录里已没有可恢复的状态。
        string abandoned = CreateOwner(_durable);
        Advance(TimeSpan.FromMinutes(idleMinutes));

        Assert.Equal(removed ? 1 : 0, await Sweeper(retentionDays: 14).SweepOnceAsync(CancellationToken.None));

        Assert.Equal(!removed, Directory.Exists(abandoned));
    }

    [Theory]
    [InlineData(34, false)]
    [InlineData(36, true)]
    public async Task SweepOnceAsync_TemporaryOwnerWithoutAProcess_RemovedAfterTheTurnTimeout(int idleMinutes, bool removed)
    {
        // 非持久会话的目录只属于一个进程；静默期 = max(轮次超时 1800 秒, 保活 0) + 5 分钟。
        string leftover = CreateOwner(_temporary);
        Advance(TimeSpan.FromMinutes(idleMinutes));

        Assert.Equal(removed ? 1 : 0, await Sweeper(retentionDays: 14).SweepOnceAsync(CancellationToken.None));

        Assert.Equal(!removed, Directory.Exists(leftover));
    }

    [Fact]
    public async Task SweepOnceAsync_LeaseHeldByAnotherInstance_KeepsExpiredOwnerUntilReleased()
    {
        string expired = CreateOwner(_durable, committed: true);
        Advance(TimeSpan.FromDays(15));
        await using (OpenCodeSessionState lease = new(expired))
        {
            Assert.Equal(0, await Sweeper(retentionDays: 14).SweepOnceAsync(CancellationToken.None));
            Assert.True(File.Exists(Path.Combine(expired, Manifest)));
        }

        Assert.Equal(1, await Sweeper(retentionDays: 14).SweepOnceAsync(CancellationToken.None));
        Assert.False(Directory.Exists(expired));
    }

    [Fact]
    public async Task SweepOnceAsync_OldForeignDirectories_LeavesTheirContentsInPlace()
    {
        string backups = Path.Combine(_temporary, "backups");
        string matchingName = Path.Combine(_temporary, NewOwnerName());
        foreach (string directory in new[] { backups, matchingName })
        {
            OpenCodeWorkspace.CreatePrivateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "keep.txt"), "backup");
        }
        Advance(TimeSpan.FromHours(2));

        Assert.Equal(0, await Sweeper(retentionDays: 14).SweepOnceAsync(CancellationToken.None));

        Assert.Equal("backup", await File.ReadAllTextAsync(Path.Combine(backups, "keep.txt")));
        Assert.Equal("backup", await File.ReadAllTextAsync(Path.Combine(matchingName, "keep.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SweepOnceAsync_PinnedOwner_SkipsBothKindsOfStorage(bool durable)
    {
        string owner = CreateOwner(durable ? _durable : _temporary, committed: durable);
        Advance(TimeSpan.FromDays(15));
        using (await OpenCodeOwnerLease.AcquireAsync(owner, CancellationToken.None))
        {
            Assert.Equal(0, await Sweeper(retentionDays: 14).SweepOnceAsync(CancellationToken.None));
            Assert.True(Directory.Exists(owner));
        }

        Assert.Equal(1, await Sweeper(retentionDays: 14).SweepOnceAsync(CancellationToken.None));
        Assert.False(Directory.Exists(owner));
    }

    [Fact]
    public async Task AcquireAsync_WhileSweepHoldsGate_ReopensOnlyAfterRemoval()
    {
        string owner = CreateOwner(_durable, committed: true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task<FileStream> acquisition;
        using (FileStream gate = Assert.IsType<FileStream>(OpenCodeOwnerLease.TryOpenGate(owner, exclusive: true)))
        {
            acquisition = OpenCodeOwnerLease.AcquireAsync(owner, timeout.Token);
            Assert.False(acquisition.IsCompleted);
            Directory.Delete(owner, recursive: true);
        }

        using FileStream lease = await acquisition;
        Assert.True(Directory.Exists(owner));
        Assert.True(File.Exists(Path.Combine(owner, OpenCodeOwnerLease.FileName)));
        Assert.False(File.Exists(Path.Combine(owner, Manifest)));
    }

    [Fact]
    public async Task SweepOnceAsync_RootsDoNotExistYet_ReturnsZero()
    {
        Assert.Equal(0, await Sweeper(retentionDays: 14).SweepOnceAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SweepOnceAsync_RootNestedInsideTheOther_IsNotMistakenForALeftover()
    {
        // 夹具等组合会把持久根放在工作区根之下；内层根目录本身不是归属目录，再久也不能当残留删掉。
        string nested = Path.Combine(_temporary, NewOwnerName(), "p-" + Guid.NewGuid().ToString("N"));
        OpenCodeWorkspace.CreatePrivateDirectory(nested);
        Advance(TimeSpan.FromHours(2));

        Assert.Equal(0, await Sweeper(retentionDays: 14, durable: nested).SweepOnceAsync(CancellationToken.None));

        Assert.True(Directory.Exists(nested));
    }

    [Fact]
    public async Task HostedService_SweepsExpiredOwnersAfterTheHostStarts()
    {
        // 直接调用 SweepOnceAsync 的用例不经过宿主：这里确认后台服务确实随宿主启动，并在启动延迟后完成首轮回收。
        string durable = Path.Combine(_root, "hosted");
        string expired = CreateOwner(durable, committed: true);
        File.SetLastWriteTimeUtc(Path.Combine(expired, Manifest), DateTime.UtcNow.AddDays(-15));
        Dictionary<string, string?> settings = new()
        {
            ["OpenCode:SessionStateRoot"] = durable,
            ["OpenCode:WorkspaceRoot"] = Path.Combine(_root, "hosted-temporary"),
            ["OpenCode:KeepAliveSeconds"] = "0"
        };
        using IHost host = new HostBuilder().ConfigureServices(services => services.AddOpenCodeRuntime(
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build())).Build();

        await host.StartAsync();
        await Task.Delay(TimeSpan.FromSeconds(23));
        await host.StopAsync();

        Assert.False(Directory.Exists(expired));
    }

    private void Advance(TimeSpan by) => _advanced += by;

    private static string NewOwnerName() => Convert.ToHexStringLower(SHA256.HashData(Guid.NewGuid().ToByteArray()));

    /// <summary>建一个归属目录：含租约文件和一个带原生数据库占位的主目录，可带检查点。</summary>
    private string CreateOwner(string root, bool committed = false)
    {
        string owner = Path.Combine(root, NewOwnerName());
        OpenCodeWorkspace.CreatePrivateDirectory(root);
        OpenCodeWorkspace.CreatePrivateDirectory(owner);
        string home = Path.Combine(owner, "p-" + Guid.NewGuid().ToString("N"));
        string database = Path.Combine(home, ".state", "data", "opencode", "opencode.db");
        OpenCodeWorkspace.CreatePrivateDirectory(Path.GetDirectoryName(database)!);
        OpenCodeWorkspace.CreatePrivateDirectory(Path.Combine(home, "workspace"));
        File.WriteAllText(database, "database");
        if (root != _temporary) File.WriteAllBytes(Path.Combine(owner, "session.lock"), []);
        if (committed) File.WriteAllText(Path.Combine(owner, Manifest), "{}");
        return owner;
    }

    private OpenCodeSessionStateSweeper Sweeper(int retentionDays, string? durable = null)
    {
        OpenCodeOptions options = new()
        {
            SessionStateRoot = durable ?? _durable,
            WorkspaceRoot = _temporary,
            SessionStateRetentionDays = retentionDays,
            KeepAliveSeconds = 0,
            TurnTimeoutSeconds = 1800
        };
        return new OpenCodeSessionStateSweeper(Options.Create(options), NullLogger<OpenCodeSessionStateSweeper>.Instance)
        {
            UtcNow = () => DateTime.UtcNow + _advanced
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
