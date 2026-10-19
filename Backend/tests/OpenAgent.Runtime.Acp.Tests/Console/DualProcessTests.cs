using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace OpenAgent.Runtime.Acp.Tests.Console;

/// <summary>
/// Real stdio dual-process coverage: the console agent serves the wire protocol on stdout, a
/// raw JSON client drives it end to end (Chinese/emoji streaming, cancel, session reuse), and
/// the console client mode runs a full client+agent process pair.
/// </summary>
public sealed class DualProcessTests
{
    [Fact]
    public async Task AgentProcess_ServesRealWireForFullLoopAndShutsDownCleanly()
    {
        string consolePath = ConsolePath();
        ProcessStartInfo start = StartInfo();
        start.ArgumentList.Add(consolePath);
        start.ArgumentList.Add("agent");
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start agent process.");
        List<string> stdoutLines = [];
        List<string> stderrLines = [];
        Task pumpOut = PumpAsync(process.StandardOutput, stdoutLines);
        Task pumpErr = PumpAsync(process.StandardError, stderrLines);
        try
        {
            using StreamWriter stdin = process.StandardInput;
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(60));

            // Raw wire: every frame below is hand-written JSON over real stdio.
            string sessionId = await SendAndAwaitSessionAsync(stdin, stdoutLines, deadline.Token);

            // The first prompt parks after its first chunk, so the raw cancel lands mid-turn.
            string cancelledPrompt = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = "p1", method = "session/prompt", @params = new { sessionId, prompt = new object[] { new { type = "text", text = "这一轮马上会被取消 ✂️" } } } });
            await stdin.WriteLineAsync(cancelledPrompt);
            await WaitUntilAsync(() => HasUpdateFrame(stdoutLines), deadline.Token);
            await stdin.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"session/cancel\",\"params\":{\"sessionId\":\"" + sessionId + "\"}}");
            StringBuilder cancelledEcho = new();
            string cancelled = await ReadTurnAsync(stdin, stdoutLines, "p1", cancelledEcho, deadline.Token);
            Assert.Equal("cancelled", cancelled);
            Assert.EndsWith(" [cancelled]", cancelledEcho.ToString());

            // The next turn streams Chinese and emoji per grapheme and reassembles exactly.
            string prompt = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = "p2", method = "session/prompt", @params = new { sessionId, prompt = new object[] { new { type = "text", text = "你好 ACP 🥐" } } } });
            await stdin.WriteLineAsync(prompt);
            StringBuilder echoed = new();
            string stop = await ReadTurnAsync(stdin, stdoutLines, "p2", echoed, deadline.Token);
            Assert.Equal("end_turn", stop);
            Assert.Equal("Echo: 你好 ACP 🥐", echoed.ToString());

            // The session keeps serving turns after the cancel.
            string reuse = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = "p3", method = "session/prompt", @params = new { sessionId, prompt = new object[] { new { type = "text", text = "继续" } } } });
            await stdin.WriteLineAsync(reuse);
            StringBuilder reuseEcho = new();
            string after = await ReadTurnAsync(stdin, stdoutLines, "p3", reuseEcho, deadline.Token);
            Assert.Equal("end_turn", after);
            Assert.Equal("Echo: 继续", reuseEcho.ToString());

            // Closing stdin is the disconnect; the agent exits cleanly within the grace.
            stdin.Close();
            using CancellationTokenSource exit = new(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(exit.Token);
            Assert.Equal(0, process.ExitCode);
            await pumpOut;
            await pumpErr;

            // Agent stdout carries protocol frames only: every line is a JSON object.
            foreach (string line in stdoutLines)
            {
                using JsonDocument _ = JsonDocument.Parse(line);
            }
        }
        finally
        {
            await StopAsync(process);
        }
    }

    [Theory]
    [InlineData(new string[0], "Echo: 你好 ACP 🥐", "end_turn")]
    [InlineData(new[] { "--cancel" }, null, "cancelled")]
    public async Task ClientProcess_RunsDualProcessEchoAndCancelModes(string[] flags, string? expectedEcho, string expectedStop)
    {
        string consolePath = ConsolePath();
        ProcessStartInfo start = StartInfo();
        start.ArgumentList.Add(consolePath);
        start.ArgumentList.Add("client");
        foreach (string flag in flags) start.ArgumentList.Add(flag);
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(flags.Length == 0 ? "你好 ACP 🥐" : "这是一段用于取消的真实进程测试文本 ✂️");
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start client process.");
        List<string> stdoutLines = [];
        try
        {
            Task output = PumpAsync(process.StandardOutput, stdoutLines);
            Task error = PumpAsync(process.StandardError, []);
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(60));
            await process.WaitForExitAsync(deadline.Token);
            Assert.Equal(0, process.ExitCode);
            await output;
            await error;
            string text = string.Join('\n', stdoutLines);
            Assert.Contains("Initialized ACP v1.", text);
            Assert.Contains("Agent: acp-test-echo", text);
            Assert.Contains("Stop reason: " + expectedStop, text);
            if (expectedEcho is not null) Assert.Contains(expectedEcho, text);
        }
        finally
        {
            await StopAsync(process);
        }
    }


    private static bool HasUpdateFrame(List<string> lines)
    {
        lock (lines) return lines.Any(line => line.Contains("session/update"));
    }

    private static string ConsolePath() => Path.Combine(AppContext.BaseDirectory, "OpenAgent.Runtime.Acp.TestConsole.dll");

    private static ProcessStartInfo StartInfo()
    {
        ProcessStartInfo start = new("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add("--runtimeconfig");
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "OpenAgent.Runtime.Acp.Tests.runtimeconfig.json"));
        string deps = Path.Combine(AppContext.BaseDirectory, "OpenAgent.Runtime.Acp.TestConsole.deps.json");
        if (File.Exists(deps))
        {
            start.ArgumentList.Add("--depsfile");
            start.ArgumentList.Add(deps);
        }

        return start;
    }

    private static async Task<string> SendAndAwaitSessionAsync(StreamWriter stdin, List<string> lines, CancellationToken deadline)
    {
        await stdin.WriteLineAsync("""
            {"jsonrpc":"2.0","id":"init","method":"initialize","params":{"protocolVersion":1,"clientCapabilities":{}}}
            """);
        await AwaitResponseAsync(lines, "init", deadline);
        await stdin.WriteLineAsync("""
            {"jsonrpc":"2.0","id":"new","method":"session/new","params":{"cwd":"/tmp","mcpServers":[]}}
            """);
        using JsonDocument answer = await AwaitResponseAsync(lines, "new", deadline);
        return answer.RootElement.GetProperty("result").GetProperty("sessionId").GetString()!;
    }

    /// <summary>Scans collected stdout until the named response appears, consuming update frames as they come.</summary>
    private static async Task<JsonDocument> AwaitResponseAsync(List<string> lines, string id, CancellationToken deadline)
    {
        while (true)
        {
            string? frame = TakeFrame(lines, id);
            if (frame is not null) return JsonDocument.Parse(frame);
            await WaitUntilAsync(() => HasFrame(lines, id), deadline);
        }
    }

    private static bool HasFrame(List<string> lines, string id)
    {
        lock (lines) return lines.Any(line => line.Contains("\"id\":\"" + id + "\""));
    }

    private static bool HasAnyFrame(List<string> lines)
    {
        lock (lines) return lines.Count > 0;
    }

    private static string? TakeFrame(List<string> lines, string id)
    {
        lock (lines)
        {
            for (int index = 0; index < lines.Count; index++)
            {
                if (!lines[index].Contains("\"id\":\"" + id + "\"")) continue;
                string frame = lines[index];
                lines.RemoveAt(index);
                return frame;
            }
        }

        return null;
    }

    /// <summary>Reads one prompt turn: accumulates update text until the final response for the id arrives.</summary>
    private static async Task<string> ReadTurnAsync(StreamWriter stdin, List<string> lines, string id, StringBuilder echoed, CancellationToken deadline)
    {
        while (true)
        {
            string? frame = TakeAnyFrame(lines);
            if (frame is null)
            {
                await WaitUntilAsync(() => HasAnyFrame(lines), deadline);
                continue;
            }

            using JsonDocument document = JsonDocument.Parse(frame);
            if (document.RootElement.TryGetProperty("method", out JsonElement method) && method.GetString() == "session/update")
            {
                echoed.Append(document.RootElement.GetProperty("params").GetProperty("update").GetProperty("content").GetProperty("text").GetString());
                continue;
            }

            if (document.RootElement.TryGetProperty("id", out JsonElement responseId) && responseId.GetString() == id)
            {
                return document.RootElement.GetProperty("result").GetProperty("stopReason").GetString()!;
            }
        }
    }

    private static string? TakeAnyFrame(List<string> lines)
    {
        lock (lines)
        {
            if (lines.Count == 0) return null;
            string frame = lines[0];
            lines.RemoveAt(0);
            return frame;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken deadline)
    {
        while (!condition())
        {
            if (deadline.IsCancellationRequested) throw new TimeoutException("The agent process did not answer within the deadline.");
            await Task.Delay(10, CancellationToken.None);
        }
    }

    private static Task PumpAsync(StreamReader reader, List<string> target) => Task.Run(async () =>
    {
        try
        {
            while (await reader.ReadLineAsync(CancellationToken.None) is { } line)
            {
                lock (target) target.Add(line);
            }
        }
        catch
        {
            // Process teardown ends the pump.
        }
    });

    private static async Task StopAsync(Process process)
    {
        if (process.HasExited) return;
        try
        {
            using CancellationTokenSource grace = new(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(grace.Token);
            return;
        }
        catch (OperationCanceledException)
        {
        }

        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
    }
}
