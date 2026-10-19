using OpenAgent.Runtime.Acp.Agent;
using OpenAgent.Runtime.Acp.TestConsole.Agents;
using OpenAgent.Runtime.Acp.Transport;

namespace OpenAgent.Runtime.Acp.TestConsole.Agents;

/// <summary>
/// The `agent` command: serves the echo agent over stdio. Stdout carries protocol frames only;
/// every diagnostic goes to stderr, so a host can wire stdout straight into the SDK transport.
/// </summary>
internal static class AgentCommand
{
    internal static async Task<int> RunAsync(TextWriter error)
    {
        await using AcpStreamTransport transport = new(Console.OpenStandardInput(), Console.OpenStandardOutput());
        await using AcpAgentConnection agent = new(transport, EchoAgent.CreateBuilder(TimeSpan.FromMilliseconds(250)));
        agent.Start();
        try
        {
            // Run until EOF, close, or fault; a clean peer disconnect is success.
            await agent.Completion.ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception)
        {
            await error.WriteLineAsync($"FAIL agent: {exception.GetType().Name}: {exception.Message}").ConfigureAwait(false);
            return 1;
        }
    }
}
