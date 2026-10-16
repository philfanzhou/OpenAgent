using System.Diagnostics;
using System.Text.Json;
using OpenAgent.Runtime.Acp.Client;
using OpenAgent.Runtime.Acp.Processes;
using OpenAgent.Runtime.Acp.Protocol;
namespace OpenAgent.Runtime.Acp.TestConsole;

internal static class Interop
{
    internal static async Task<int> RunAsync(string[] command)
    {
        if (command.Length == 0) throw new ArgumentException("Usage: interop -- <executable> [args...]");
        Stopwatch timer = Stopwatch.StartNew();
        await using AcpProcess process = AcpProcess.Start(new(command[0], command[1..]) { StandardError = Console.Error.Write });
        await using AcpClient client = new(process.Transport);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        await client.InitializeAsync(cancellationToken: timeout.Token);
        await using AcpSession session = await client.NewSessionAsync(new(Path.GetFullPath(Environment.CurrentDirectory), []), timeout.Token);
        TaskCompletionSource first = new(TaskCreationOptions.RunContinuationsAsynchronously), started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task consume = ConsumeAsync();
        await using (AcpPromptTurn turn = await session.StartPromptAsync([new TextContent("hello")], timeout.Token))
            if ((await turn.Completion).StopReason != "end_turn") throw new IOException("Unexpected prompt result.");
        await first.Task.WaitAsync(timeout.Token);
        await using (AcpPromptTurn turn = await session.StartPromptAsync([new TextContent("slow")], timeout.Token))
        {
            await started.Task.WaitAsync(timeout.Token);
            await turn.CancelAndWaitAsync(timeout.Token);
            if ((await turn.Completion).StopReason != "cancelled") throw new IOException("Cancellation was not confirmed.");
        }
        await session.DisposeAsync(); await consume;
        ScenarioReport[] report = [new("interop", "PASS", timer.ElapsedMilliseconds, null)];
        Console.WriteLine(JsonSerializer.Serialize(report, ReportJsonContext.Default.ScenarioReportArray)); return 0;
        async Task ConsumeAsync()
        {
            await foreach (SessionNotification item in session.ReadUpdatesAsync(timeout.Token))
                if (item.Update is AgentMessageChunk { Content: TextContent text })
                {
                    first.TrySetResult();
                    if (text.Text == "started") started.TrySetResult();
                }
        }
    }
}
