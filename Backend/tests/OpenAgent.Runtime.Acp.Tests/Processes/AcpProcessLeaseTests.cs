using System.Text;
using OpenAgent.Runtime.Acp.Client;
using OpenAgent.Runtime.Acp.Processes;
using OpenAgent.Runtime.Acp.Protocol;
using Xunit;

namespace OpenAgent.Runtime.Acp.Tests.Processes;

/// <summary>
/// Process lifecycle over real children of the test console (its agent mode and fixture
/// behaviors only, inside temporary directories — SPEC: default tests use temp dirs and
/// self-started processes). Covers V11 (process faults) and V18 (ownership, no leaks, no
/// shared-process kill).
/// </summary>
public sealed class AcpProcessLeaseTests
{
    private const string ParentMarker = "ACP_P4_PARENT_MARKER";
    private const string ForbiddenMarker = "ACP_P4_FORBIDDEN_MARKER";

    [Fact]
    public async Task StartAsync_ServesHandshakeOverLeasedChild_AndExitsGracefullyOnStdinEof()
    {
        using TempDir temp = new();
        List<string> stderr = [];
        await using AcpProcessLease lease = await AcpProcessLauncher.StartAsync(new AcpProcessOptions
        {
            Executable = ConsoleHost(),
            Arguments = ConsoleArgs("agent"),
            WorkingDirectory = temp.Path,
            OnStdErrorLine = stderr.Add
        });
        await using AcpClientConnection client = new(lease.Transport);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(60));
        client.Start();
        InitializeResponse negotiated = await client.InitializeAsync(deadline.Token);
        Assert.Equal(1, negotiated.ProtocolVersion);
        AcpSession session = await client.NewSessionAsync(temp.Path, deadline.Token);
        AcpPromptTurn turn = await session.StartPromptAsync([new ContentBlock(new ContentBlockText("进程握手 ✅"))], deadline.Token);
        PromptResponse final = await turn.Completion.WaitAsync(deadline.Token);
        Assert.Equal(StopReason.EndTurn, final.StopReason);
        Assert.Empty(stderr);

        // The stdin EOF from the transport disposal is the child's graceful stop signal.
        await client.DisposeAsync();
        Assert.Equal(0, client.TrackedTaskCount);
        await lease.DisposeAsync();
        AcpProcessExit exit = await lease.Completion;
        Assert.False(exit.Killed);
        Assert.Equal(0, exit.ExitCode);
        Assert.Null(exit.Failure);
    }

    [Fact]
    public async Task StartAsync_MissingExecutable_ThrowsWithoutLeavingAProcess()
    {
        using TempDir temp = new();
        string missing = Path.Combine(temp.Path, "no-such-agent");
        AcpProcessStartException exception = await Assert.ThrowsAsync<AcpProcessStartException>(
            () => AcpProcessLauncher.StartAsync(new AcpProcessOptions { Executable = missing }));
        Assert.Equal(missing, exception.Executable);
    }

    [Fact]
    public async Task Completion_ReportsNonZeroExit_AfterDrainingSustainedStderr()
    {
        using TempDir temp = new();
        List<string> lines = [];
        await using AcpProcessLease lease = await AcpProcessLauncher.StartAsync(new AcpProcessOptions
        {
            Executable = ConsoleHost(),
            Arguments = ConsoleArgs("fixture", "stderr", "3000", "3"),
            WorkingDirectory = temp.Path,
            OnStdErrorLine = lines.Add
        });
        AcpProcessExit exit = await lease.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(3, exit.ExitCode);
        Assert.False(exit.Killed);
        Assert.Equal(3000, lines.Count);
        Assert.All(lines.Take(3), line => Assert.StartsWith("stderr-line-", line));

        // The lease shutdown after a self-exit converges and leaves the transport closed.
        await lease.DisposeAsync();
        using CancellationTokenSource bound = new(TimeSpan.FromSeconds(5));
        await lease.Transport.Completion.WaitAsync(bound.Token);
    }

    [Fact]
    public async Task DisposeAsync_KillsTheTree_WhenTheGraceExpires()
    {
        using TempDir temp = new();
        AcpProcessLease lease = await AcpProcessLauncher.StartAsync(new AcpProcessOptions
        {
            Executable = ConsoleHost(),
            Arguments = ConsoleArgs("fixture", "hang", "60"),
            ShutdownGracePeriod = TimeSpan.FromMilliseconds(200)
        });
        try
        {
            await lease.DisposeAsync();
            AcpProcessExit exit = await lease.Completion;
            Assert.True(exit.Killed);
            Assert.NotEqual(0, exit.ExitCode);
            Assert.Null(exit.Failure);
        }
        finally
        {
            await lease.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposeAsync_RecordsACrashInsideTheGraceWindow_WithoutKilling()
    {
        using TempDir temp = new();
        await using AcpProcessLease lease = await AcpProcessLauncher.StartAsync(new AcpProcessOptions
        {
            Executable = ConsoleHost(),
            Arguments = ConsoleArgs("fixture", "delayed-exit", "500", "7")
        });
        await lease.DisposeAsync();
        AcpProcessExit exit = await lease.Completion;
        Assert.False(exit.Killed);
        Assert.Equal(7, exit.ExitCode);
    }

    [Fact]
    public async Task Completion_PublishesASelfExit_EvenWithoutDispose()
    {
        using TempDir temp = new();
        await using AcpProcessLease lease = await AcpProcessLauncher.StartAsync(new AcpProcessOptions
        {
            Executable = ConsoleHost(),
            Arguments = ConsoleArgs("fixture", "delayed-exit", "100", "0")
        });
        AcpProcessExit exit = await lease.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(exit.Killed);
        Assert.Equal(0, exit.ExitCode);
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent_AndConverges()
    {
        using TempDir temp = new();
        AcpProcessLease lease = await AcpProcessLauncher.StartAsync(new AcpProcessOptions
        {
            Executable = ConsoleHost(),
            Arguments = ConsoleArgs("fixture", "hang", "60"),
            ShutdownGracePeriod = TimeSpan.FromMilliseconds(200)
        });
        try
        {
            await lease.DisposeAsync();
            AcpProcessExit exit = await lease.Completion;
            Assert.True(exit.Killed);
            await lease.DisposeAsync();
            await lease.DisposeAsync();
            AcpProcessExit repeat = await lease.Completion;
            Assert.Equal(exit, repeat);
        }
        finally
        {
            await lease.DisposeAsync();
        }
    }

    /// <summary>Disposing one session must never end the process the other sessions share (V18).</summary>
    [Fact]
    public async Task SessionDispose_DoesNotKillTheSharedAgent()
    {
        using TempDir temp = new();
        await using AcpProcessLease lease = await AcpProcessLauncher.StartAsync(new AcpProcessOptions
        {
            Executable = ConsoleHost(),
            Arguments = ConsoleArgs("agent"),
            WorkingDirectory = temp.Path
        });
        await using AcpClientConnection client = new(lease.Transport);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(60));
        client.Start();
        await client.InitializeAsync(deadline.Token);
        AcpSession first = await client.NewSessionAsync(temp.Path, deadline.Token);
        AcpSession second = await client.NewSessionAsync(temp.Path, deadline.Token);

        PromptResponse firstTurn = await PromptAsync(first, "第一会话", deadline.Token);
        Assert.Equal(StopReason.EndTurn, firstTurn.StopReason);
        await first.DisposeAsync();
        Assert.False(lease.Completion.IsCompleted, "the shared process must keep running after one session closed");

        PromptResponse secondTurn = await PromptAsync(second, "第二会话仍在", deadline.Token);
        Assert.Equal(StopReason.EndTurn, secondTurn.StopReason);

        await client.DisposeAsync();
        Assert.Equal(0, client.TrackedTaskCount);
        await lease.DisposeAsync();
        AcpProcessExit exit = await lease.Completion;
        Assert.False(exit.Killed);
        Assert.Equal(0, exit.ExitCode);
    }

    [Fact]
    public async Task Environment_InheritPassesTheParent_AllowListStartsCleared()
    {
        using TempDir temp = new();
        Environment.SetEnvironmentVariable(ParentMarker, "from-parent");
        Environment.SetEnvironmentVariable(ForbiddenMarker, "denied");
        try
        {
            await using AcpProcessLease inherit = await AcpProcessLauncher.StartAsync(FixtureEnv(temp.Path, ParentMarker, null));
            Assert.Equal($"{ParentMarker}=from-parent", await ReadLineAsync(inherit));
            await inherit.DisposeAsync();

            await using AcpProcessLease allowList = await AcpProcessLauncher.StartAsync(FixtureEnv(temp.Path, ForbiddenMarker,
                new Dictionary<string, string> { [ParentMarker] = "sdk-only" }));
            Assert.Equal($"{ForbiddenMarker}=<unset>", await ReadLineAsync(allowList));
            await allowList.DisposeAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable(ParentMarker, null);
            Environment.SetEnvironmentVariable(ForbiddenMarker, null);
        }
    }

    [Fact]
    public async Task SlowChild_IsReadToEof_AndExitsCleanly()
    {
        using TempDir temp = new();
        await using AcpProcessLease lease = await AcpProcessLauncher.StartAsync(new AcpProcessOptions
        {
            Executable = ConsoleHost(),
            Arguments = ConsoleArgs("fixture", "slow", "6", "40")
        });
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        for (int index = 1; index <= 6; index++)
        {
            byte[]? line = await lease.Transport.ReadAsync(deadline.Token);
            Assert.NotNull(line);
            Assert.Equal($"slow-line-{index}", Encoding.UTF8.GetString(line));
        }

        Assert.Null(await lease.Transport.ReadAsync(deadline.Token));
        await lease.DisposeAsync();
        AcpProcessExit exit = await lease.Completion;
        Assert.Equal(0, exit.ExitCode);
    }

    [Fact]
    public async Task SlowReader_ConsumesEveryWrittenFrame_BeforeTheEofShutdown()
    {
        using TempDir temp = new();
        List<string> stderr = [];
        await using AcpProcessLease lease = await AcpProcessLauncher.StartAsync(new AcpProcessOptions
        {
            Executable = ConsoleHost(),
            Arguments = ConsoleArgs("fixture", "read-stdin", "20"),
            OnStdErrorLine = stderr.Add
        });
        for (int index = 0; index < 5; index++)
        {
            await lease.Transport.WriteAsync(Encoding.UTF8.GetBytes($"{{\"frame\":{index}}}"));
        }

        await lease.DisposeAsync();
        AcpProcessExit exit = await lease.Completion;
        Assert.Equal(0, exit.ExitCode);
        Assert.Contains("frames=5", stderr);
    }

    private static async Task<PromptResponse> PromptAsync(AcpSession session, string text, CancellationToken deadline)
    {
        AcpPromptTurn turn = await session.StartPromptAsync([new ContentBlock(new ContentBlockText(text))], deadline);
        return await turn.Completion.WaitAsync(deadline);
    }

    private static AcpProcessOptions FixtureEnv(string tempDir, string marker, Dictionary<string, string>? curated) => new()
    {
        Executable = ConsoleHost(),
        Arguments = ConsoleArgs("fixture", "env", marker),
        WorkingDirectory = tempDir,
        EnvironmentMode = curated is null ? AcpEnvironmentMode.Inherit : AcpEnvironmentMode.AllowList,
        Environment = curated
    };

    private static async Task<string> ReadLineAsync(AcpProcessLease lease)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        byte[]? line = await lease.Transport.ReadAsync(deadline.Token);
        Assert.NotNull(line);
        return Encoding.UTF8.GetString(line);
    }

    private static string ConsoleHost() => "dotnet";

    private static string[] ConsoleArgs(params string[] arguments)
    {
        List<string> args = ["exec", "--runtimeconfig", Path.Combine(AppContext.BaseDirectory, "OpenAgent.Runtime.Acp.Tests.runtimeconfig.json")];
        string deps = Path.Combine(AppContext.BaseDirectory, "OpenAgent.Runtime.Acp.TestConsole.deps.json");
        if (File.Exists(deps))
        {
            args.Add("--depsfile");
            args.Add(deps);
        }

        args.Add(Path.Combine(AppContext.BaseDirectory, "OpenAgent.Runtime.Acp.TestConsole.dll"));
        args.AddRange(arguments);
        return [.. args];
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "acp-process-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
        }
    }
}
