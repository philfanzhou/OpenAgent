using System.Diagnostics;
using System.Text.Json;

namespace OpenAgent.Acp.Demo.Tests.TestDoubles;

/// <summary>An independent raw-JSON client talking to the actual agent process.</summary>
internal sealed class AgentHarness : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task<string> _stderr;

    internal AgentHarness()
    {
        ProcessStartInfo start = new(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(typeof(AcpAgent).Assembly.Location);
        start.ArgumentList.Add("agent");
        _process = Process.Start(start) ?? throw new IOException("Unable to start the test agent.");
        _process.StandardInput.AutoFlush = true;
        _stderr = _process.StandardError.ReadToEndAsync();
    }

    internal Task SendAsync(object message) => SendLineAsync(JsonSerializer.Serialize(message));

    internal Task SendLineAsync(string line) => _process.StandardInput.WriteLineAsync(line);

    internal async Task<JsonElement> ReadAsync()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        string line = await _process.StandardOutput.ReadLineAsync(timeout.Token)
            ?? throw new EndOfStreamException("Agent exited before replying.");
        using JsonDocument document = JsonDocument.Parse(line);
        return document.RootElement.Clone();
    }

    internal async Task<JsonElement> ReadResponseAsync(string id, List<JsonElement>? updates = null)
    {
        while (true)
        {
            JsonElement message = await ReadAsync();
            if (message.TryGetProperty("id", out JsonElement actualId) && actualId.ValueKind == JsonValueKind.String && actualId.GetString() == id)
            {
                return message;
            }

            updates?.Add(message);
        }
    }

    internal async Task<string> InitializeSessionAsync(int protocolVersion = 1)
    {
        await SendAsync(new
        {
            jsonrpc = "2.0", id = "init", method = "initialize", @params = new { protocolVersion, clientCapabilities = new { } }
        });
        JsonElement initialized = await ReadAsync();
        if (initialized.GetProperty("result").GetProperty("protocolVersion").GetInt32() != 1)
        {
            throw new InvalidDataException("Unexpected protocol version.");
        }

        await SendAsync(new
        {
            jsonrpc = "2.0", id = "new", method = "session/new",
            @params = new { cwd = Directory.GetCurrentDirectory(), mcpServers = Array.Empty<object>() }
        });
        JsonElement created = await ReadAsync();
        return created.GetProperty("result").GetProperty("sessionId").GetString()!;
    }

    internal Task PromptAsync(string sessionId, string text, string id = "prompt") => SendAsync(new
    {
        jsonrpc = "2.0", id, method = "session/prompt",
        @params = new { sessionId, prompt = new[] { new { type = "text", text } } }
    });

    public async ValueTask DisposeAsync()
    {
        _process.StandardInput.Close();
        int exitCode;
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            await _process.WaitForExitAsync(timeout.Token);
            exitCode = _process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
            throw new TimeoutException("Agent did not exit after stdin EOF.");
        }
        finally
        {
            _process.Dispose();
        }

        string stderr = await _stderr;
        if (exitCode != 0 || !string.IsNullOrEmpty(stderr))
        {
            throw new IOException($"Agent exited with code {exitCode}. Stderr: {stderr}");
        }
    }
}
