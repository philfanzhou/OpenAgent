using System.Text.Json;
using OpenAgent.Runtime.Acp.Agent;
using OpenAgent.Runtime.Acp.Client;
using OpenAgent.Runtime.Acp.Processes;
using OpenAgent.Runtime.Acp.Protocol;
using OpenAgent.Runtime.Acp.Transport;
namespace OpenAgent.Runtime.Acp.TestConsole;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args is ["terminal-worker"]) { Console.Write("prefix你好🥐"); return 0; }
            if (args is ["fail-worker"]) return 17;
            if (args is ["noisy-worker"]) { for (int i = 0; i < 1024; i++) Console.Error.Write(new string('x', 4096)); return 17; }
            if (args is ["stall-worker"]) { await Task.Delay(Timeout.Infinite); return 0; }
            if (args is ["agent"])
            {
                await using AcpAgentHost host = new(new AcpStreamTransport(Console.OpenStandardInput(), Console.OpenStandardOutput()), new EchoAgent().Handlers);
                await host.RunAsync(); return 0;
            }
            if (args.Length == 0 || args is ["list"])
            { Console.WriteLine(string.Join(Environment.NewLine, Scenarios.Names)); return 0; }
            if (args.Length >= 2 && args[0] == "interop" && args[1] == "--") return await Interop.RunAsync(args[2..]);
            if (args.Length >= 2 && args[0] == "run") return await Scenarios.RunAsync(args[1], args.Contains("--json", StringComparer.Ordinal));
            if (args.Length >= 2 && args[0] == "client")
            {
                int separator = Array.IndexOf(args, "--");
                if (separator < 2 || separator + 1 >= args.Length) throw new ArgumentException("Usage: client <prompt> [--cancel-after-ms N] -- <executable> [args...]");
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
                int cancelMs = separator == 4 && args[2] == "--cancel-after-ms" ? int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 0;
                if (separator != 2 && cancelMs <= 0) throw new ArgumentException("Invalid client options.");
                await using AcpProcess process = AcpProcess.Start(new(args[separator + 1], args[(separator + 2)..]));
                await using AcpClient client = new(process.Transport); await client.InitializeAsync(cancellationToken: timeout.Token);
                await using AcpSession session = await client.NewSessionAsync(new(Path.GetFullPath(Environment.CurrentDirectory), []));
                Task display = DisplayAsync(session);
                using CancellationTokenSource cancel = new();
                await using AcpPromptTurn turn = await session.StartPromptAsync([new TextContent(args[1])], cancel.Token);
                if (cancelMs > 0) cancel.CancelAfter(cancelMs);
                PromptResponse result = await turn.Completion; await session.DisposeAsync(); await display;
                Console.WriteLine($"Stop: {result.StopReason}"); return 0;
            }
            Console.Error.WriteLine("Usage: list | run <scenario|all> [--json] | agent | client <prompt> -- <executable> [args...]"); return 2;
        }
        catch (ArgumentException error) { Console.Error.WriteLine(error.Message); return 2; }
        catch (Exception error) { Console.Error.WriteLine($"FAIL: {error.GetType().Name}: {error.Message}"); return 1; }
    }
    private static async Task DisplayAsync(AcpSession session)
    { await foreach (SessionNotification item in session.ReadUpdatesAsync()) if (item.Update is AgentMessageChunk { Content: TextContent text }) Console.Write(text.Text); }
}
