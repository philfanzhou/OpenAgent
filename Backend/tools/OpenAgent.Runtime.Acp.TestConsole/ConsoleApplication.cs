using System.Text;
using System.Text.Json;
using OpenAgent.Runtime.Acp.Transport;
using OpenAgent.Runtime.Acp.TestConsole.Scenarios;

namespace OpenAgent.Runtime.Acp.TestConsole;

internal static class ConsoleApplication
{
    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        if (args is ["--help"] or ["help"])
        {
            await output.WriteLineAsync("Usage: acp-test [list | run framing | run lifecycle | run all]").ConfigureAwait(false);
            return 0;
        }

        if (args.Length == 0 || args is ["list"])
        {
            await output.WriteLineAsync("framing\tP0\tBounded SDK Stream transport, Unicode, CRLF and EOF").ConfigureAwait(false);
            await output.WriteLineAsync("lifecycle\tP0\tEOF drain, cancellation failure and shared disposal").ConfigureAwait(false);
            return 0;
        }

        if (args is not ["run", "framing" or "lifecycle" or "all"])
        {
            await error.WriteLineAsync("Unknown command or scenario. Use list to see implemented scenarios.").ConfigureAwait(false);
            return 2;
        }

        try
        {
            if (args[1] is "framing" or "all")
            {
                await VerifyFramingAsync().ConfigureAwait(false);
                await output.WriteLineAsync("PASS framing (SDK transport; no Agent or model invoked)").ConfigureAwait(false);
            }

            if (args[1] is "lifecycle" or "all")
            {
                await LifecycleScenario.RunAsync().ConfigureAwait(false);
                await output.WriteLineAsync("PASS lifecycle (SDK cleanup and failure propagation)").ConfigureAwait(false);
            }
            return 0;
        }
        catch (Exception exception)
        {
            await error.WriteLineAsync($"FAIL {args[1]}: {exception.GetType().Name}").ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task VerifyFramingAsync()
    {
        foreach (string delimiter in new[] { "\n", "\r\n" })
        {
            await using AcpStreamTransport bounded = new(
                new MemoryStream(Encoding.UTF8.GetBytes("{}" + delimiter)), new MemoryStream(),
                new AcpTransportOptions { MaxMessageBytes = 2 });
            if (!(await bounded.ReadAsync().ConfigureAwait(false))!.AsSpan().SequenceEqual("{}"u8))
            {
                throw new InvalidDataException("Delimiter counted toward message limit.");
            }
        }

        byte[] source = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"method\":\"probe\",\"params\":{\"text\":\"你好🥐\"}}\r\n{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}\n");
        using MemoryStream input = new(source);
        using MemoryStream output = new();
        await using AcpStreamTransport transport = new(input, output, new AcpTransportOptions { LeaveOpen = true });
        byte[] first = await transport.ReadAsync().ConfigureAwait(false)
            ?? throw new InvalidDataException("Missing first frame.");
        byte[] second = await transport.ReadAsync().ConfigureAwait(false)
            ?? throw new InvalidDataException("Missing second frame.");
        using JsonDocument notification = JsonDocument.Parse(first);
        using JsonDocument response = JsonDocument.Parse(second);
        if (notification.RootElement.GetProperty("params").GetProperty("text").GetString() != "你好🥐" ||
            response.RootElement.GetProperty("id").GetInt32() != 1 ||
            await transport.ReadAsync().ConfigureAwait(false) is not null)
        {
            throw new InvalidDataException("Unexpected frame contents or EOF.");
        }

        await transport.Completion.ConfigureAwait(false);
        // The output path is tested on a separate live transport after input EOF.
        using MemoryStream writeInput = new();
        await using AcpStreamTransport writer = new(writeInput, output, new AcpTransportOptions { LeaveOpen = true });
        await writer.WriteAsync(first).ConfigureAwait(false);
        if (!output.ToArray().AsSpan().SequenceEqual(first.Concat(new byte[] { (byte)'\n' }).ToArray()))
        {
            throw new InvalidDataException("Unexpected serialized frame.");
        }
    }
}
