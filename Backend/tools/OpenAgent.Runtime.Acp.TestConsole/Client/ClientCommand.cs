using System.Diagnostics;
using System.Text;
using OpenAgent.Runtime.Acp.Client;
using OpenAgent.Runtime.Acp.Protocol;
using OpenAgent.Runtime.Acp.Transport;

namespace OpenAgent.Runtime.Acp.TestConsole.Client;

/// <summary>
/// The `client` command: one full SDK client over a real stdio link. Without options it spawns
/// this console's own echo agent; `--agent`/`--agent-arg` target any local stdio agent. The
/// child-process plumbing here is console tooling only — the SDK process module (P4) replaces it.
/// </summary>
internal static class ClientCommand
{
    private static readonly TimeSpan ChildStopGrace = TimeSpan.FromSeconds(5);

    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        bool cancel = false;
        string? agentExecutable = null;
        List<string> agentArguments = [];
        string cwd = Path.GetTempPath();
        TimeSpan timeout = TimeSpan.FromSeconds(30);
        List<string> promptParts = [];
        bool afterSeparator = false;
        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            if (afterSeparator)
            {
                promptParts.Add(argument);
                continue;
            }

            switch (argument)
            {
                case "--":
                    afterSeparator = true;
                    break;
                case "--cancel":
                    cancel = true;
                    break;
                case "--agent" when index + 1 < args.Length:
                    agentExecutable = args[++index];
                    break;
                case "--agent-arg" when index + 1 < args.Length:
                    agentArguments.Add(args[++index]);
                    break;
                case "--cwd" when index + 1 < args.Length:
                    cwd = args[++index];
                    break;
                case "--timeout-seconds" when index + 1 < args.Length
                    && int.TryParse(args[++index], out int seconds) && seconds is >= 1 and <= 1800:
                    timeout = TimeSpan.FromSeconds(seconds);
                    break;
                default:
                    await error.WriteLineAsync($"Unknown client option '{argument}'.").ConfigureAwait(false);
                    return 2;
            }
        }

        if (promptParts.Count == 0)
        {
            await error.WriteLineAsync("client requires a prompt: client [--cancel] [--agent <exe>] [--agent-arg <arg>]... [--cwd <path>] [--timeout-seconds <n>] -- <prompt>").ConfigureAwait(false);
            return 2;
        }

        // Console-only interim plumbing until the P4 process module lands: the child runs with
        // explicit argv, no shell, and is always reaped on exit.
        Process? started = await StartAgentAsync(agentExecutable, agentArguments, error).ConfigureAwait(false);
        if (started is null) return 1;
        using Process process = started;
        using CancellationTokenSource lifetime = new(timeout);
        try
        {
            return await RunClientAsync(process, cancel, cwd, string.Join(' ', promptParts), timeout, lifetime.Token, output, error).ConfigureAwait(false);
        }
        finally
        {
            await StopChildAsync(process).ConfigureAwait(false);
            lifetime.Dispose();
        }
    }

    private static async Task<int> RunClientAsync(
        Process process,
        bool cancel,
        string cwd,
        string prompt,
        TimeSpan timeout,
        CancellationToken lifetime,
        TextWriter output,
        TextWriter error)
    {
        await using AcpStreamTransport transport = new(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
        await using AcpClientConnection client = new(transport);
        client.Start();
        InitializeResponse negotiated;
        try
        {
            negotiated = await client.InitializeAsync(lifetime).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await error.WriteLineAsync($"FAIL client: initialize: {exception.GetType().Name}: {exception.Message}").ConfigureAwait(false);
            return 1;
        }

        Implementation agent = negotiated.AgentInfo.HasValue ? negotiated.AgentInfo.Value : new Implementation("unknown", "-");
        await output.WriteLineAsync($"Initialized ACP v{negotiated.ProtocolVersion}.").ConfigureAwait(false);
        await output.WriteLineAsync($"Agent: {agent.Name} ({agent.Version})").ConfigureAwait(false);

        AcpSession session;
        try
        {
            session = await client.NewSessionAsync(cwd, lifetime).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await error.WriteLineAsync($"FAIL client: session/new: {exception.GetType().Name}: {exception.Message}").ConfigureAwait(false);
            return 1;
        }

        await output.WriteLineAsync($"Session: {session.SessionId}").ConfigureAwait(false);

        StringBuilder echoed = new();
        TaskCompletionSource firstChunk = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken consumerToken = lifetime;
        Task consumer = Task.Run(async () =>
        {
            try
            {
                await foreach (SessionNotification update in session.ReadUpdatesAsync(consumerToken).ConfigureAwait(false))
                {
                    if (update.Update.Json.TryGetProperty("sessionUpdate", out var tag) && tag.GetString() == "agent_message_chunk")
                    {
                        lock (echoed) echoed.Append(update.Update.AsAgentMessageChunk().Content.AsText().Text);
                        firstChunk.TrySetResult();
                    }
                }
            }
            catch
            {
                // The consumer always loses the race to turn disposal and process teardown.
            }
        }, CancellationToken.None);

        AcpPromptTurn turn;
        try
        {
            turn = await session.StartPromptAsync([new ContentBlock(new ContentBlockText(prompt))], lifetime).ConfigureAwait(false);
            if (cancel)
            {
                await firstChunk.Task.WaitAsync(lifetime).ConfigureAwait(false);
                await turn.CancelAsync(lifetime).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            await error.WriteLineAsync($"FAIL client: prompt: {exception.GetType().Name}: {exception.Message}").ConfigureAwait(false);
            return 1;
        }

        PromptResponse final;
        try
        {
            final = await turn.Completion.WaitAsync(lifetime).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await error.WriteLineAsync($"FAIL client: turn: {exception.GetType().Name}: {exception.Message}").ConfigureAwait(false);
            return 1;
        }

        // Completion guarantees updates are enqueued; give the local consumer a bounded moment to catch up.
        await consumer.WaitAsync(TimeSpan.FromMilliseconds(500), CancellationToken.None).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        lock (echoed) output.WriteLine(echoed.ToString());
        await output.WriteLineAsync($"Stop reason: {final.StopReason}").ConfigureAwait(false);
        return 0;
    }

    private static async Task<Process?> StartAgentAsync(string? agentExecutable, List<string> agentArguments, TextWriter error)
    {
        ProcessStartInfo start;
        if (agentExecutable is null)
        {
            // Default peer: this console in agent mode, spawned as a real child process.
            string consolePath = Path.Combine(AppContext.BaseDirectory, "OpenAgent.Runtime.Acp.TestConsole.dll");
            string host = Environment.ProcessPath ?? "dotnet";
            // Under the dotnet host the DLL is the first argument; an apphost already binds its
            // own DLL, so passing the DLL path again would land in the console as a stray argument.
            bool viaDotnetHost = Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
            start = new ProcessStartInfo(host) { RedirectStandardOutput = true, RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false };
            if (viaDotnetHost) start.ArgumentList.Add(consolePath);
            start.ArgumentList.Add("agent");
        }
        else
        {
            start = new ProcessStartInfo(agentExecutable) { RedirectStandardOutput = true, RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (string argument in agentArguments) start.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("The agent process could not start.");
        }
        catch (Exception exception)
        {
            await error.WriteLineAsync($"FAIL client: the agent process could not start: {exception.Message}").ConfigureAwait(false);
            return null;
        }

        _ = PumpErrorsAsync(process.StandardError, error, process);
        return process;
    }

    /// <summary>Agent diagnostics arrive on the child's stderr and stay on our stderr; stdout is never mixed in.</summary>
    private static async Task PumpErrorsAsync(StreamReader reader, TextWriter error, Process process)
    {
        try
        {
            while (await reader.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
            {
                await error.WriteLineAsync($"[agent] {line}").ConfigureAwait(false);
            }
        }
        catch
        {
            // A dead child or a closing writer ends the pump; the process exit path reports the rest.
        }
    }

    private static async Task StopChildAsync(Process process)
    {
        if (process.HasExited) return;
        try
        {
            using CancellationTokenSource grace = new(ChildStopGrace);
            await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException)
        {
            // The grace elapsed: reap the child ourselves, never leaving an owned process behind.
        }

        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync().ConfigureAwait(false);
    }
}
