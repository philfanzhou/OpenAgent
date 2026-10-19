using System.Diagnostics;
using System.Text;
using OpenAgent.Runtime.Acp.Client;
using OpenAgent.Runtime.Acp.Processes;
using OpenAgent.Runtime.Acp.Protocol;
using OpenAgent.Runtime.Acp.TestConsole.Agents;
using OpenAgent.Runtime.Acp.Transport;

namespace OpenAgent.Runtime.Acp.TestConsole.Scenarios;

/// <summary>
/// P4 process lifecycle, fully offline: the SDK launcher starts real children of this console
/// only (its own agent mode and deterministic fixtures), inside one temporary directory.
/// Covers the handshake over a leased transport, the shared-agent session rule, startup
/// failure, exit codes, sustained stderr, staged shutdown with kill, exit during the grace
/// window, the environment policy and slow IO.
/// </summary>
internal static class ProcessScenario
{
    private const string ParentMarker = "ACP_P4_PARENT_MARKER";
    private const string ForbiddenMarker = "ACP_P4_FORBIDDEN_MARKER";
    private const int StderrLines = 5000;

    internal static async Task RunAsync()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "acp-test-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            await HandshakeOverLeaseAsync(tempDir).ConfigureAwait(false);
            await SharedAgentSurvivesSessionDisposeAsync(tempDir).ConfigureAwait(false);
            await MissingExecutableFailsAsync(tempDir).ConfigureAwait(false);
            await StderrDrainAndExitCodeAsync(tempDir).ConfigureAwait(false);
            await KillAfterGraceAsync(tempDir).ConfigureAwait(false);
            await ExitDuringGraceAsync(tempDir).ConfigureAwait(false);
            await EnvironmentPolicyAsync(tempDir).ConfigureAwait(false);
            await SlowIoAsync(tempDir).ConfigureAwait(false);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>A full client handshake over a leased child process, ending in a graceful staged shutdown.</summary>
    private static async Task HandshakeOverLeaseAsync(string tempDir)
    {
        List<string> stderr = [];
        await using AcpProcessLease lease = await AcpProcessLauncher.StartAsync(new AcpProcessOptions
        {
            Executable = AgentSpawn.Host,
            Arguments = AgentSpawn.Self("agent"),
            Environment = AgentSpawn.ChildEnvironment(),
            WorkingDirectory = tempDir,
            OnStdErrorLine = stderr.Add
        }).ConfigureAwait(false);
        await using AcpClientConnection client = new(lease.Transport);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        client.Start();
        InitializeResponse negotiated = await client.InitializeAsync(deadline.Token).ConfigureAwait(false);
        Assert(negotiated.ProtocolVersion == AcpProtocol.Version, "leased child negotiated v1");
        AcpSession session = await client.NewSessionAsync(tempDir, deadline.Token).ConfigureAwait(false);
        List<string> chunks = [];
        Task consumer = EchoScenario.CollectUpdatesAsync(session, chunks, deadline.Token);
        PromptResponse final = await PromptAsync(session, "你好进程 🥐", chunks, "Echo: 你好进程 🥐", deadline).ConfigureAwait(false);
        Assert(final.StopReason == StopReason.EndTurn, "leased child turn ends with end_turn");
        Assert(stderr.Count == 0, "quiet agent writes no stderr");
        await client.DisposeAsync().ConfigureAwait(false);
        await lease.DisposeAsync().ConfigureAwait(false);
        AcpProcessExit exit = await lease.Completion.ConfigureAwait(false);
        Assert(exit is { ExitCode: 0, Killed: false }, "stdin EOF ended the agent gracefully with exit 0");
        Assert(!Directory.EnumerateFileSystemEntries(tempDir).Any(), "the child left no files behind in its cwd");
    }

    /// <summary>Disposing one session must not end the shared process; the second session keeps prompting (SPEC: ProcessLease).</summary>
    private static async Task SharedAgentSurvivesSessionDisposeAsync(string tempDir)
    {
        await using AcpProcessLease lease = await StartAgentAsync(tempDir).ConfigureAwait(false);
        await using AcpClientConnection client = new(lease.Transport);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        client.Start();
        await client.InitializeAsync(deadline.Token).ConfigureAwait(false);
        AcpSession first = await client.NewSessionAsync(tempDir, deadline.Token).ConfigureAwait(false);
        AcpSession second = await client.NewSessionAsync(tempDir, deadline.Token).ConfigureAwait(false);
        List<string> chunks = [];
        Task consumer = EchoScenario.CollectUpdatesAsync(first, chunks, deadline.Token);
        Task consumer2 = EchoScenario.CollectUpdatesAsync(second, chunks, deadline.Token);
        await PromptAsync(first, "第一会话", chunks, "Echo: 第一会话", deadline).ConfigureAwait(false);
        await first.DisposeAsync().ConfigureAwait(false);
        Assert(!lease.Completion.IsCompleted, "the shared process is still running after one session closed");
        PromptResponse after = await PromptAsync(second, "第二会话仍在", chunks, "Echo: 第二会话仍在", deadline).ConfigureAwait(false);
        Assert(after.StopReason == StopReason.EndTurn, "the surviving session still prompts the shared agent");
        await client.DisposeAsync().ConfigureAwait(false);
        await lease.DisposeAsync().ConfigureAwait(false);
        AcpProcessExit exit = await lease.Completion.ConfigureAwait(false);
        Assert(exit is { ExitCode: 0, Killed: false }, "the shared agent still exits gracefully at teardown");
    }

    private static async Task MissingExecutableFailsAsync(string tempDir)
    {
        string missing = Path.Combine(tempDir, "no-such-agent");
        try
        {
            await using AcpProcessLease _ = await AcpProcessLauncher.StartAsync(new AcpProcessOptions { Executable = missing }).ConfigureAwait(false);
            throw new InvalidDataException("Starting a missing executable should have failed.");
        }
        catch (AcpProcessStartException exception)
        {
            Assert(exception.Executable == missing, "the start failure names the executable");
        }
    }

    /// <summary>Sustained stderr is drained while the child runs and the exit code still arrives.</summary>
    private static async Task StderrDrainAndExitCodeAsync(string tempDir)
    {
        List<string> lines = [];
        await using AcpProcessLease lease = await StartFixtureAsync(["stderr", StderrLines.ToString(), "3"], tempDir, lines).ConfigureAwait(false);
        AcpProcessExit exit = await lease.Completion.ConfigureAwait(false);
        Assert(exit.ExitCode == 3, "the fixture's non-zero exit code is reported");
        Assert(!exit.Killed, "a self-exiting child is never marked killed");
        Assert(lines.Count == StderrLines, $"all {StderrLines} stderr lines were drained");
        await lease.DisposeAsync().ConfigureAwait(false);
        await AssertTransportClosedAsync(lease).ConfigureAwait(false);
    }

    /// <summary>A child that ignores stdin EOF is killed when the grace expires.</summary>
    private static async Task KillAfterGraceAsync(string tempDir)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        await using AcpProcessLease lease = await AcpProcessLauncher.StartAsync(new AcpProcessOptions
        {
            Executable = AgentSpawn.Host,
            Arguments = AgentSpawn.Self("fixture", "hang", "60"),
            Environment = AgentSpawn.ChildEnvironment(),
            ShutdownGracePeriod = TimeSpan.FromMilliseconds(300)
        }).ConfigureAwait(false);
        await lease.DisposeAsync().ConfigureAwait(false);
        AcpProcessExit exit = await lease.Completion.ConfigureAwait(false);
        elapsed.Stop();
        Assert(exit.Killed, "the grace expiry killed the tree");
        Assert(exit.ExitCode is not 0, "a killed child does not report success");
        Assert(elapsed.Elapsed < TimeSpan.FromSeconds(10), "the staged shutdown stayed bounded");
    }

    /// <summary>A child exiting non-zero inside the graceful window is recorded, not killed.</summary>
    private static async Task ExitDuringGraceAsync(string tempDir)
    {
        await using AcpProcessLease lease = await AcpProcessLauncher.StartAsync(new AcpProcessOptions
        {
            Executable = AgentSpawn.Host,
            Arguments = AgentSpawn.Self("fixture", "delayed-exit", "800", "7"),
            Environment = AgentSpawn.ChildEnvironment()
        }).ConfigureAwait(false);
        await lease.DisposeAsync().ConfigureAwait(false);
        AcpProcessExit exit = await lease.Completion.ConfigureAwait(false);
        Assert(exit is { ExitCode: 7, Killed: false }, "the mid-grace crash is recorded with its exit code");
    }

    /// <summary>Inherit passes the parent environment; AllowList starts from a cleared one.</summary>
    private static async Task EnvironmentPolicyAsync(string tempDir)
    {
        Environment.SetEnvironmentVariable(ParentMarker, "from-parent");
        Environment.SetEnvironmentVariable(ForbiddenMarker, "denied");
        try
        {
            string inherited = await RunFixtureLineAsync(["env", ParentMarker], tempDir, null).ConfigureAwait(false);
            Assert(inherited == $"{ParentMarker}=from-parent", "Inherit mode passes the parent environment");
            string denied = await RunFixtureLineAsync(["env", ForbiddenMarker], tempDir, null).ConfigureAwait(false);
            Assert(denied == $"{ForbiddenMarker}=denied", "Inherit mode passes every parent variable");

            string allowed = await RunFixtureLineAsync(
                ["env", ParentMarker], tempDir,
                new Dictionary<string, string> { [ParentMarker] = "sdk-only", [ForbiddenMarker] = "leaked?" }).ConfigureAwait(false);
            Assert(allowed == $"{ParentMarker}=sdk-only", "AllowList mode passes only the curated entries");
            string cleared = await RunFixtureLineAsync(
                ["env", ForbiddenMarker], tempDir,
                new Dictionary<string, string> { [ParentMarker] = "sdk-only" }).ConfigureAwait(false);
            Assert(cleared == $"{ForbiddenMarker}=<unset>", "AllowList mode never leaks a parent variable");
        }
        finally
        {
            Environment.SetEnvironmentVariable(ParentMarker, null);
            Environment.SetEnvironmentVariable(ForbiddenMarker, null);
        }
    }

    /// <summary>A slowly writing child is read to the end; a slowly reading child consumes every written frame.</summary>
    private static async Task SlowIoAsync(string tempDir)
    {
        await using AcpProcessLease lease = await AcpProcessLauncher.StartAsync(new AcpProcessOptions
        {
            Executable = AgentSpawn.Host,
            Arguments = AgentSpawn.Self("fixture", "slow", "8", "60"),
            Environment = AgentSpawn.ChildEnvironment()
        }).ConfigureAwait(false);
        for (int index = 1; index <= 8; index++)
        {
            byte[]? line = await lease.Transport.ReadAsync().ConfigureAwait(false)
                ?? throw new InvalidDataException($"The slow child ended the stream at line {index}.");
            Assert(Encoding.UTF8.GetString(line) == $"slow-line-{index}", "slow stdout arrives in order");
        }

        Assert(await lease.Transport.ReadAsync().ConfigureAwait(false) is null, "the slow child ended with EOF");
        await lease.DisposeAsync().ConfigureAwait(false);
        AcpProcessExit exit = await lease.Completion.ConfigureAwait(false);
        Assert(exit is { ExitCode: 0, Killed: false }, "the slow writer exits cleanly");

        List<string> readerLines = [];
        await using AcpProcessLease reader = await AcpProcessLauncher.StartAsync(new AcpProcessOptions
        {
            Executable = AgentSpawn.Host,
            Arguments = AgentSpawn.Self("fixture", "read-stdin", "40"),
            Environment = AgentSpawn.ChildEnvironment(),
            OnStdErrorLine = readerLines.Add
        }).ConfigureAwait(false);
        for (int index = 0; index < 10; index++)
        {
            await reader.Transport.WriteAsync(Encoding.UTF8.GetBytes($"{{\"frame\":{index}}}")).ConfigureAwait(false);
        }

        await reader.DisposeAsync().ConfigureAwait(false);
        AcpProcessExit readerExit = await reader.Completion.ConfigureAwait(false);
        Assert(readerExit is { ExitCode: 0, Killed: false }, "the slow reader consumed the frames and exited on EOF");
        Assert(readerLines.Count == 1 && readerLines[0] == "frames=10", "the slow reader counted every written frame");
    }

    private static async Task<AcpProcessLease> StartAgentAsync(string tempDir) =>
        await AcpProcessLauncher.StartAsync(new AcpProcessOptions
        {
            Executable = AgentSpawn.Host,
            Arguments = AgentSpawn.Self("agent"),
            Environment = AgentSpawn.ChildEnvironment(),
            WorkingDirectory = tempDir
        }).ConfigureAwait(false);

    private static async Task<AcpProcessLease> StartFixtureAsync(string[] behavior, string tempDir, List<string>? stderr) =>
        await AcpProcessLauncher.StartAsync(new AcpProcessOptions
        {
            Executable = AgentSpawn.Host,
            Arguments = AgentSpawn.Self(["fixture", .. behavior]),
            Environment = ChildEnvironment(null),
            WorkingDirectory = tempDir,
            OnStdErrorLine = stderr is null ? null : stderr.Add
        }).ConfigureAwait(false);

    /// <summary>Runs one fixture to completion and returns its single stdout line.</summary>
    private static async Task<string> RunFixtureLineAsync(string[] behavior, string tempDir, Dictionary<string, string>? allowedEnvironment)
    {
        List<string> childErrors = [];
        await using AcpProcessLease lease = await AcpProcessLauncher.StartAsync(new AcpProcessOptions
        {
            Executable = AgentSpawn.Host,
            Arguments = AgentSpawn.Self(["fixture", .. behavior]),
            WorkingDirectory = tempDir,
            EnvironmentMode = allowedEnvironment is null ? AcpEnvironmentMode.Inherit : AcpEnvironmentMode.AllowList,
            Environment = ChildEnvironment(allowedEnvironment),
            ShutdownGracePeriod = TimeSpan.FromSeconds(3),
            OnStdErrorLine = childErrors.Add
        }).ConfigureAwait(false);
        byte[]? line = await lease.Transport.ReadAsync().ConfigureAwait(false)
            ?? throw new InvalidDataException($"The fixture printed nothing for: {string.Join(' ', behavior)}; stderr={string.Join(" | ", childErrors)}");
        AcpProcessExit exit = await lease.Completion.ConfigureAwait(false);
        Assert(exit is { ExitCode: 0, Killed: false }, $"the fixture exited cleanly: {string.Join(' ', behavior)}");
        await lease.DisposeAsync().ConfigureAwait(false);
        return Encoding.UTF8.GetString(line);
    }

    private static async Task AssertTransportClosedAsync(AcpProcessLease lease)
    {
        using CancellationTokenSource bound = new(TimeSpan.FromSeconds(5));
        await lease.Transport.Completion.WaitAsync(bound.Token).ConfigureAwait(false);
    }

    private static async Task<PromptResponse> PromptAsync(AcpSession session, string prompt, List<string> chunks, string expectedEcho, CancellationTokenSource deadline)
    {
        int before = EchoScenario.Count(chunks);
        AcpPromptTurn turn = await session.StartPromptAsync([new ContentBlock(new ContentBlockText(prompt))], deadline.Token).ConfigureAwait(false);
        PromptResponse final = await turn.Completion.WaitAsync(deadline.Token).ConfigureAwait(false);
        await EchoScenario.WaitForAsync(() => EchoScenario.Joined(chunks, before) == expectedEcho, deadline.Token, "echo text");
        return final;
    }

    private static void Assert(bool condition, string what)
    {
        if (!condition) throw new InvalidDataException($"{what} failed.");
    }

    /// <summary>Combines the apphost runtime entries with the stage's own curated map.</summary>
    private static IReadOnlyDictionary<string, string> ChildEnvironment(IReadOnlyDictionary<string, string>? extra)
    {
        Dictionary<string, string> merged = new();
        if (AgentSpawn.ChildEnvironment() is { } runtime)
        {
            foreach (KeyValuePair<string, string> entry in runtime) merged[entry.Key] = entry.Value;
        }

        if (extra is not null)
        {
            foreach (KeyValuePair<string, string> entry in extra) merged[entry.Key] = entry.Value;
        }

        return merged;
    }
}
