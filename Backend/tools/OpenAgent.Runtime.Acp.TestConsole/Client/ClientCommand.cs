using System.Text;
using OpenAgent.Runtime.Acp.Client;
using OpenAgent.Runtime.Acp.Processes;
using OpenAgent.Runtime.Acp.Protocol;
using OpenAgent.Runtime.Acp.TestConsole.Agents;

namespace OpenAgent.Runtime.Acp.TestConsole.Client;

/// <summary>
/// The `client` command: one full SDK client over a real stdio link, spawned by the SDK
/// process module (P4). Without options it starts this console's own echo agent; `--agent`/
/// `--agent-arg` target any local stdio agent — that includes real model-backed agents, whose
/// runs are always explicit invocations like this one and never part of `run all`.
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

        AcpProcessOptions processOptions = new()
        {
            Executable = agentExecutable ?? AgentSpawn.Host,
            Arguments = agentExecutable is null ? AgentSpawn.Self("agent") : [.. agentArguments],
            Environment = agentExecutable is null ? AgentSpawn.ChildEnvironment() : null,
            OnStdErrorLine = line => error.WriteLine($"[agent] {line}"),
            ShutdownGracePeriod = ChildStopGrace
        };
        AcpProcessLease lease;
        try
        {
            lease = await AcpProcessLauncher.StartAsync(processOptions).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await error.WriteLineAsync($"FAIL client: the agent process could not start: {exception.Message}").ConfigureAwait(false);
            return 1;
        }

        await using AcpProcessLease owned = lease;
        using CancellationTokenSource lifetime = new(timeout);
        try
        {
            int code = await RunClientAsync(lease, cancel, cwd, string.Join(' ', promptParts), lifetime.Token, output, error).ConfigureAwait(false);
            return code;
        }
        finally
        {
            // Staged shutdown: stdin EOF first, then the grace window, then the tree kill.
            await owned.DisposeAsync().ConfigureAwait(false);
            AcpProcessExit exit = await owned.Completion.ConfigureAwait(false);
            if (exit.Failure is not null)
            {
                await error.WriteLineAsync($"[agent] teardown: {exit.Failure.GetType().Name}: {exit.Failure.Message}").ConfigureAwait(false);
            }
            else if (!exit.Killed && exit.ExitCode is not (0 or null))
            {
                await error.WriteLineAsync($"[agent] exited with code {exit.ExitCode}").ConfigureAwait(false);
            }

            lifetime.Dispose();
        }
    }

    private static async Task<int> RunClientAsync(
        AcpProcessLease lease,
        bool cancel,
        string cwd,
        string prompt,
        CancellationToken lifetime,
        TextWriter output,
        TextWriter error)
    {
        await using AcpClientConnection client = new(lease.Transport);
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
}
