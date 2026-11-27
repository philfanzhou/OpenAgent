using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAgent.Runtime.OpenCode;
using Xunit;

namespace OpenAgent.Runtime.Tests;

/// <summary>已提交会话状态的落盘形态：进程回收后只保留原生数据库与工作区，同一归属目录不留旧主目录。</summary>
[Collection(RealOpenCodeCollection.Name)]
public sealed class OpenCodeSessionStateLifecycleTests
{
    private const string Manifest = "committed-session.json";

    [Fact]
    public void ReleaseRuntimeState_KeepsNativeDatabaseAndWorkspaceOnly()
    {
        string home = Path.Combine(Path.GetTempPath(), "opencode-runtime-state-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (string directory in new[] { "home", "config", "data/opencode", "cache", "state", "opencode", "tmp" })
            {
                OpenCodeWorkspace.CreatePrivateDirectory(Path.Combine(home, ".state", directory));
            }
            OpenCodeWorkspace.CreatePrivateDirectory(Path.Combine(home, "workspace", "outputs"));
            OpenCodeWorkspace.CreatePrivateDirectory(Path.Combine(home, ".file-exchange"));
            string database = Path.Combine(home, ".state", "data", "opencode", "opencode.db");
            File.WriteAllText(database, "native session");
            File.WriteAllText(Path.Combine(home, ".state", "tmp", ".jit.so"), "mapping");

            OpenCodeWorkspace.ReleaseRuntimeState(home, NullLogger.Instance);
            OpenCodeWorkspace.ReleaseRuntimeState(home, NullLogger.Instance);

            Assert.Equal("native session", File.ReadAllText(database));
            Assert.Equal("outputs", Path.GetFileName(Assert.Single(Directory.GetDirectories(Path.Combine(home, "workspace")))));
            Assert.Equal("data", Path.GetFileName(Assert.Single(Directory.GetDirectories(Path.Combine(home, ".state")))));
            Assert.Equal([".state", "workspace"], Directory.GetFileSystemEntries(home).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        }
        finally
        {
            if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
        }
    }

    [OpenCodeFact]
    public async Task CommittedSession_AfterProcessReclaim_KeepsOnlyNativeDatabaseAndWorkspace()
    {
        string durable = Path.Combine(Path.GetTempPath(), "acp-state-reclaim-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using ModelFixture model = await ModelFixture.StartAsync((context, _) => ModelFixture.TextAsync(context, "answer"));
            // 默认不保活：轮次结束即回收进程。
            await using RuntimeFixture runtime = new(model.Endpoint, config => config["OpenCode:SessionStateRoot"] = durable);
            await runtime.CollectAsync(runtime.Request with
            {
                HistoryCheckpoint = "initial", CommitAsync = _ => Task.FromResult<string?>("committed")
            });

            string home = Assert.Single(Directory.GetDirectories(durable, "p-*", SearchOption.AllDirectories));
            Assert.Equal("data", Path.GetFileName(Assert.Single(Directory.GetDirectories(Path.Combine(home, ".state")))));
            Assert.True(File.Exists(Path.Combine(home, ".state", "data", "opencode", "opencode.db")));
            Assert.Empty(Directory.GetDirectories(Path.Combine(home, ".state"), "tmp"));
            Assert.Empty(Directory.GetDirectories(home, ".file-exchange"));
            Assert.True(Directory.Exists(Path.Combine(home, "workspace")));
        }
        finally
        {
            if (Directory.Exists(durable)) Directory.Delete(durable, recursive: true);
        }
    }

    [OpenCodeFact]
    public async Task CommittedSession_WhenLaunchIdentityChanges_SupersedesTheOldHome()
    {
        string durable = Path.Combine(Path.GetTempPath(), "acp-state-supersede-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using ModelFixture model = await ModelFixture.StartAsync((context, _) => ModelFixture.TextAsync(context, "answer"));
            for (int round = 1; round <= 2; round++)
            {
                await using RuntimeFixture runtime = new(model.Endpoint, config => config["OpenCode:SessionStateRoot"] = durable);
                // 第二轮换了指令：启动身份变化，第一轮的主目录不再可复用。
                await runtime.CollectAsync((round == 1 ? runtime.Request : runtime.Request with { Instructions = "Answer briefly." }) with
                {
                    HistoryCheckpoint = round == 1 ? "initial" : "committed-first",
                    CommitAsync = _ => Task.FromResult<string?>(round == 1 ? "committed-first" : "committed-second")
                });
            }

            string home = Assert.Single(Directory.GetDirectories(durable, "p-*", SearchOption.AllDirectories));
            OpenCodeSessionState.Snapshot snapshot = JsonSerializer.Deserialize<OpenCodeSessionState.Snapshot>(
                await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(durable, Manifest, SearchOption.AllDirectories))))!;
            Assert.Equal(Path.GetFileName(home), snapshot.Home);
        }
        finally
        {
            if (Directory.Exists(durable)) Directory.Delete(durable, recursive: true);
        }
    }

    [OpenCodeFact]
    public async Task KeptAliveOwner_PastRetention_IsSweptOnlyAfterTheProcessIsReclaimed()
    {
        string durable = Path.Combine(Path.GetTempPath(), "acp-state-kept-" + Guid.NewGuid().ToString("N"));
        OpenCodeSessionStateSweeper sweeper = null!;
        try
        {
            await using ModelFixture model = await ModelFixture.StartAsync((context, _) => ModelFixture.TextAsync(context, "answer"));
            await using (RuntimeFixture runtime = new(model.Endpoint, config =>
            {
                config["OpenCode:SessionStateRoot"] = durable;
                config["OpenCode:KeepAliveSeconds"] = "60";
            }))
            {
                await runtime.CollectAsync(runtime.Request with
                {
                    HistoryCheckpoint = "initial", CommitAsync = _ => Task.FromResult<string?>("committed")
                });
                string manifest = Assert.Single(Directory.GetFiles(durable, Manifest, SearchOption.AllDirectories));
                sweeper = new(runtime.Keeper, Options.Create(new OpenCodeOptions
                {
                    SessionStateRoot = durable, WorkspaceRoot = runtime.Root, KeepAliveSeconds = 60
                }), NullLogger<OpenCodeSessionStateSweeper>.Instance)
                {
                    // 时钟拨快 15 天：这份状态已过 14 天保留期。
                    UtcNow = () => DateTime.UtcNow.AddDays(15)
                };

                // 保活进程还在使用该目录：过期也不回收，否则它下一次追问会读到被删掉的原生库。
                Assert.Equal(1, runtime.Keeper.KeptCount);
                Assert.Equal(0, await sweeper.SweepOnceAsync(CancellationToken.None));
                Assert.True(File.Exists(manifest));
            }

            // 夹具释放时回收了保活进程。
            Assert.Equal(1, await sweeper.SweepOnceAsync(CancellationToken.None));
            Assert.Empty(Directory.GetDirectories(durable));
        }
        finally
        {
            if (Directory.Exists(durable)) Directory.Delete(durable, recursive: true);
        }
    }
}
