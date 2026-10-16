namespace OpenAgent.Acp.Demo;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.FirstOrDefault() == "agent")
            {
                await using JsonLineTransport transport = new(Console.OpenStandardInput(), Console.OpenStandardOutput());
                AcpAgent agent = new(transport);
                await agent.RunAsync().ConfigureAwait(false);
                return 0;
            }

            if (args.Length == 0 || args[0] == "client")
            {
                ClientOptions options = ClientOptions.Parse(args.Skip(1).ToArray());
                await DemoClient.RunAsync(options)
                    .ConfigureAwait(false);
                return 0;
            }

            await Console.Error.WriteLineAsync("Usage: OpenAgent.Acp.Demo [agent | client [--cancel] [--agent command] [--agent-arg value] [--cwd path] [--timeout-seconds seconds] [--] [prompt]]")
                .ConfigureAwait(false);
            return 2;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync($"Demo failed: {exception.Message}").ConfigureAwait(false);
            return 1;
        }
    }
}
