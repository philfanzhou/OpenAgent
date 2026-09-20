using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Execution;
using Xunit;

namespace OpenAgent.Runner.Tests;

public class SessionSandboxTests
{
    [Fact]
    public void BuildSessionArguments_KeepsHardeningAndAddsChannelWithPersistentInput()
    {
        var settings = new RunnerOptions
        {
            PythonPath = "/opt/openagent-code/venv/bin/python",
            SessionIdleMinutes = 120,
            TimeoutSeconds = 120
        };
        IReadOnlyList<string> arguments = BubblewrapCodeExecutor.BuildSessionArguments(
            settings, "/var/lib/runner/session-demo/channel", "/opt/runner/sandbox");

        Assert.Contains("--unshare-user", arguments);
        Assert.Contains("--unshare-pid", arguments);
        Assert.Contains("--unshare-net", arguments);
        Assert.Contains("--disable-userns", arguments);
        Assert.Contains("--die-with-parent", arguments);
        Assert.Contains("--clearenv", arguments);
        Assert.Contains("--remount-ro", arguments);
        Assert.DoesNotContain("--share-net", arguments);
        Assert.Contains("--ro-bind", arguments);
        // Read-write control channel and persistent /input tmpfs.
        Assert.True(ContainsSequence(arguments, ["--bind", "/var/lib/runner/session-demo/channel", "/channel"]));
        Assert.True(ContainsSequence(arguments, ["--size", "67108864", "--perms", "1777", "--tmpfs", "/input"]));
        // The supervisor replaces the prlimit-wrapped one-shot entry point.
        Assert.DoesNotContain("/usr/bin/prlimit", arguments);
        Assert.DoesNotContain("/sandbox/execute.py", arguments);
        Assert.Contains("/sandbox/supervisor.py", arguments);
        Assert.Equal("120", EnvironmentValue(arguments, "SANDBOX_TIMEOUT"));
        Assert.Equal("7500", EnvironmentValue(arguments, "SANDBOX_MAX_IDLE_SECONDS"));
        Assert.Equal("1610612736", EnvironmentValue(arguments, "SANDBOX_AS_BYTES"));
        Assert.Equal(settings.NodePath, EnvironmentValue(arguments, "EXECUTION_NODE"));
    }

    [Fact]
    public async Task Manager_SerializesSameSessionAndReportsBusy()
    {
        using var blocker = new SemaphoreSlim(0, 1);
        FakeSandbox alpha = new("alpha") { OnExecute = _ => { blocker.Wait(); return Ok(); } };
        SessionSandboxManager manager = CreateManager(new RunnerOptions { MaxSessionSandboxes = 8 }, "alpha", () => alpha);

        Task running = Task.Run(() => manager.ExecuteAsync("alpha", Request(), CancellationToken.None));
        await alpha.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<RunnerBusyException>(
            () => manager.ExecuteAsync("alpha", Request(), CancellationToken.None));
        blocker.Release();
        await running;
        Assert.Equal(1, manager.LiveCount);
    }

    [Fact]
    public async Task Manager_DoesNotEvictBusySandboxAndRejectsNewSession()
    {
        using var blocker = new SemaphoreSlim(0, 1);
        FakeSandbox alpha = new("alpha") { OnExecute = _ => { blocker.Wait(); return Ok(); } };
        SessionSandboxManager manager = CreateManager(new RunnerOptions { MaxSessionSandboxes = 1 }, "alpha", () => alpha);

        Task running = Task.Run(() => manager.ExecuteAsync("alpha", Request(), CancellationToken.None));
        await alpha.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<RunnerBusyException>(
            () => manager.ExecuteAsync("beta", Request(), CancellationToken.None));
        blocker.Release();
        await running;
        Assert.Null(alpha.ReleasedReason);
    }

    [Fact]
    public async Task Manager_EvictsLongestIdleAtCapacity()
    {
        FakeSandbox alpha = new("alpha");
        FakeSandbox beta = new("beta");
        SessionSandboxManager manager = CreateManager(new RunnerOptions { MaxSessionSandboxes = 1 },
            "beta", () => beta, ("alpha", () => alpha));

        await manager.ExecuteAsync("alpha", Request(), CancellationToken.None);
        alpha.LastUsedUtc = DateTimeOffset.UtcNow.AddMinutes(-5);
        CodeExecutionResult result = await manager.ExecuteAsync("beta", Request(), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("evicted", alpha.ReleasedReason);
        Assert.Null(beta.ReleasedReason);
        Assert.Equal(1, manager.LiveCount);
    }

    [Fact]
    public async Task Manager_ReapsIdleSandboxes()
    {
        FakeSandbox alpha = new("alpha");
        SessionSandboxManager manager = CreateManager(new RunnerOptions(), "alpha", () => alpha);

        await manager.ExecuteAsync("alpha", Request(), CancellationToken.None);
        alpha.LastUsedUtc = DateTimeOffset.UtcNow.AddMinutes(-30);
        await manager.ReapIdleAsync(TimeSpan.FromMinutes(10));

        Assert.Equal("idle", alpha.ReleasedReason);
        Assert.Equal(0, manager.LiveCount);
    }

    [Fact]
    public async Task Manager_ReportsSandboxResetWhenSandboxDied()
    {
        FakeSandbox initial = new("alpha");
        FakeSandbox respawned = new("alpha");
        var spawns = new Queue<Func<ISessionSandbox>>([() => initial, () => respawned]);
        SessionSandboxManager manager = new(Options.Create(new RunnerOptions()),
            NullLogger<SessionSandboxManager>.Instance,
            _ => Task.FromResult(spawns.Dequeue()()));

        CodeExecutionResult first = await manager.ExecuteAsync("alpha", Request(), CancellationToken.None);
        Assert.False(first.SandboxReset);

        initial.IsAlive = false;
        CodeExecutionResult second = await manager.ExecuteAsync("alpha", Request(), CancellationToken.None);
        Assert.True(second.SandboxReset);
        Assert.Equal("dead", initial.ReleasedReason);
        Assert.Null(respawned.ReleasedReason);
    }

    [Fact]
    public async Task Manager_ReportsSandboxResetWhenRecoveringLeftoverDirectory()
    {
        FakeSandbox recovered = new("alpha") { Recovered = true };
        SessionSandboxManager manager = CreateManager(new RunnerOptions(), "alpha", () => recovered);

        CodeExecutionResult result = await manager.ExecuteAsync("alpha", Request(), CancellationToken.None);
        Assert.True(result.SandboxReset);
    }

    [Fact]
    public async Task Manager_RespawnsOnceThenFailsWhenSupervisorStaysUnreachable()
    {
        var spawns = new Queue<Func<ISessionSandbox>>(
        [
            () => new FakeSandbox("alpha") { ThrowUnavailable = true },
            () => new FakeSandbox("alpha") { ThrowUnavailable = true }
        ]);
        SessionSandboxManager manager = new(Options.Create(new RunnerOptions()),
            NullLogger<SessionSandboxManager>.Instance,
            _ => Task.FromResult(spawns.Dequeue()()));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.ExecuteAsync("alpha", Request(), CancellationToken.None));
        Assert.Empty(spawns);
        Assert.Equal(0, manager.LiveCount);
    }

    private static CodeExecutionRequest Request() => new() { Code = "print(1)" };

    private static CodeExecutionResult Ok() => new() { ExitCode = 0, Stdout = "ok" };

    private static SessionSandboxManager CreateManager(
        RunnerOptions settings, string defaultKey, Func<FakeSandbox> defaultSpawn,
        params (string Key, Func<FakeSandbox> Spawn)[] extra)
    {
        var map = extra.ToDictionary(item => item.Key, item => (Func<ISessionSandbox>)(() => item.Spawn()));
        return new SessionSandboxManager(Options.Create(settings), NullLogger<SessionSandboxManager>.Instance,
            key => Task.FromResult(map.TryGetValue(key, out Func<ISessionSandbox>? spawn)
                ? spawn()
                : defaultKey == key ? defaultSpawn() : new FakeSandbox(key)));
    }

    private static bool ContainsSequence(IReadOnlyList<string> arguments, string[] sequence)
    {
        for (int index = 0; index <= arguments.Count - sequence.Length; index++)
        {
            if (Enumerable.Range(0, sequence.Length).All(offset => arguments[index + offset] == sequence[offset]))
            {
                return true;
            }
        }
        return false;
    }

    private static string? EnvironmentValue(IReadOnlyList<string> arguments, string name)
    {
        for (int index = 0; index < arguments.Count - 2; index++)
        {
            if (arguments[index] == "--setenv" && arguments[index + 1] == name)
            {
                return arguments[index + 2];
            }
        }
        return null;
    }

    private sealed class FakeSandbox(string sessionKey) : ISessionSandbox
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<CodeExecutionRequest, CodeExecutionResult>? OnExecute { get; set; }
        public bool ThrowUnavailable { get; set; }
        public string? ReleasedReason { get; private set; }

        public string SessionKey { get; } = sessionKey;
        public DateTimeOffset LastUsedUtc { get; set; } = DateTimeOffset.UtcNow;
        public bool IsAlive { get; set; } = true;
        public bool Recovered { get; set; }

        public Task<CodeExecutionResult> ExecuteAsync(CodeExecutionRequest request, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            if (ThrowUnavailable)
            {
                throw new SessionSandboxUnavailableException("supervisor is gone");
            }
            return Task.FromResult(OnExecute?.Invoke(request) ?? new CodeExecutionResult { ExitCode = 0, Stdout = "ok" });
        }

        public ValueTask ReleaseAsync(string reason)
        {
            ReleasedReason = reason;
            IsAlive = false;
            return ValueTask.CompletedTask;
        }
    }
}
