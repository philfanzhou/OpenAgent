using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using OpenAgent.Runtime.Acp.Protocol;
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
            await output.WriteLineAsync("Usage: acp-test [list | run framing|lifecycle|schema|all [--report <json-path>] [--fixtures <json-path>]]").ConfigureAwait(false);
            return 0;
        }
        if (args.Length == 0 || args is ["list"])
        {
            await output.WriteLineAsync("framing\tP0\tBounded SDK Stream transport, Unicode, CRLF and EOF").ConfigureAwait(false);
            await output.WriteLineAsync("lifecycle\tP0\tEOF drain, cancellation failure and shared disposal").ConfigureAwait(false);
            await output.WriteLineAsync("schema\tP1\tPinned v1 models, 25 methods, 14 updates and rejected input").ConfigureAwait(false);
            return 0;
        }
        string? reportPath = null;
        string fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "contracts.json");
        bool valid = args.Length >= 2 && args[0] == "run" && args[1] is "framing" or "lifecycle" or "schema" or "all";
        HashSet<string> seen = new(StringComparer.Ordinal);
        for (int i = 2; valid && i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || args[i] is not ("--report" or "--fixtures") || !seen.Add(args[i]) || string.IsNullOrWhiteSpace(args[i + 1])) valid = false;
            else if (args[i] == "--report") reportPath = args[i + 1];
            else if (args[1] is "schema" or "all") fixturePath = args[i + 1];
            else valid = false;
        }
        if (!valid)
        {
            await error.WriteLineAsync("Unknown command or scenario. Use list to see implemented scenarios.").ConfigureAwait(false);
            return 2;
        }
        JsonArray results = new();
        string[] scenarios = args[1] == "all" ? ["framing", "lifecycle", "schema"] : [args[1]];
        int exitCode = 0;
        foreach (string scenario in scenarios)
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            JsonObject result = new() { ["scenario"] = scenario, ["phase"] = scenario == "schema" ? "P1" : "P0", ["protocolVersion"] = AcpProtocol.Version };
            try
            {
                if (scenario == "framing") await VerifyFramingAsync().ConfigureAwait(false);
                else if (scenario == "lifecycle") await LifecycleScenario.RunAsync().ConfigureAwait(false);
                else result["coverage"] = await SchemaScenario.RunAsync(fixturePath).ConfigureAwait(false);
                result["status"] = "PASS";
                await output.WriteLineAsync(scenario == "schema"
                    ? "PASS schema protocolVersion=1 methods=25 updates=14 definitions=178 (model coverage)"
                    : $"PASS {scenario} (SDK transport and cleanup)").ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                result["status"] = "FAIL";
                result["error"] = exception.GetType().Name;
                await error.WriteLineAsync($"FAIL {scenario}: {exception.GetType().Name}").ConfigureAwait(false);
                exitCode = 1;
            }
            result["elapsedMs"] = elapsed.Elapsed.TotalMilliseconds;
            results.Add(result);
        }
        if (reportPath is not null)
        {
            JsonObject report = new()
            {
                ["status"] = exitCode == 0 ? "PASS" : "FAIL", ["protocolVersion"] = AcpProtocol.Version,
                ["sourceCommit"] = AcpProtocol.SourceCommit, ["results"] = results,
                ["notTested"] = new JsonArray("RPC execution", "real Agent/model", "interoperability", "AOT", "Docker")
            };
            try { await File.WriteAllTextAsync(reportPath, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n").ConfigureAwait(false); }
            catch (Exception exception)
            {
                await error.WriteLineAsync($"FAIL report: {exception.GetType().Name}").ConfigureAwait(false);
                exitCode = 1;
            }
        }
        return exitCode;
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
