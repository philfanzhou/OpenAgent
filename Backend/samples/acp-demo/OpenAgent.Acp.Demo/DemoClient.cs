using System.Text.Json;

namespace OpenAgent.Acp.Demo;

internal static class DemoClient
{
    internal static async Task RunAsync(ClientOptions options, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);
        await using AgentProcess process = AgentProcess.Start(options.AgentCommand, options.AgentArguments, options.WorkingDirectory);
        JsonLineTransport transport = process.Transport;
        ClientConnection connection = new(transport);
        string? sessionId = null;
        string? model = null;
        bool cancellationSent = false;

        async Task OnNotificationAsync(JsonElement message)
        {
            if (message.GetProperty("method").GetString() != "session/update")
            {
                return;
            }

            JsonElement parameters = message.GetProperty("params");
            if (sessionId is not null && parameters.GetProperty("sessionId").GetString() != sessionId)
            {
                throw new InvalidDataException("Received an update for a different session.");
            }

            JsonElement update = parameters.GetProperty("update");
            string? kind = update.GetProperty("sessionUpdate").GetString();
            if (kind == "config_option_update")
            {
                ReportModel(update, ref model);
            }

            if (kind == "agent_message_chunk" && update.GetProperty("content").GetProperty("type").GetString() == "text")
            {
                Console.Write(update.GetProperty("content").GetProperty("text").GetString());
                if (options.Cancel && !cancellationSent && sessionId is not null)
                {
                    cancellationSent = true;
                    await transport.WriteAsync(new
                    {
                        jsonrpc = "2.0", method = "session/cancel", @params = new { sessionId }
                    }, timeout.Token).ConfigureAwait(false);
                }
            }
        }

        JsonElement initialized = await connection.RequestAsync("init", "initialize", new
        {
            protocolVersion = 1, clientCapabilities = new { }, clientInfo = new { name = "dotnet-demo-client", version = "0.1.0" }
        }, OnNotificationAsync, timeout.Token).ConfigureAwait(false);
        if (initialized.GetProperty("protocolVersion").GetInt32() != 1)
        {
            throw new InvalidDataException("Agent did not negotiate ACP version 1.");
        }

        Console.WriteLine("Initialized ACP v1.");
        if (initialized.TryGetProperty("agentInfo", out JsonElement agentInfo) && agentInfo.ValueKind == JsonValueKind.Object)
        {
            Console.WriteLine($"Agent: {agentInfo.GetProperty("name").GetString()} ({agentInfo.GetProperty("version").GetString()})");
        }

        JsonElement created = await connection.RequestAsync("new", "session/new", new
        {
            cwd = options.WorkingDirectory, mcpServers = Array.Empty<object>()
        }, OnNotificationAsync, timeout.Token).ConfigureAwait(false);
        sessionId = created.GetProperty("sessionId").GetString()!;
        Console.WriteLine($"Session: {sessionId}");
        ReportModel(created, ref model);

        JsonElement result = await connection.RequestAsync("prompt", "session/prompt", new
        {
            sessionId, prompt = new[] { new { type = "text", text = options.Prompt } }
        }, OnNotificationAsync, timeout.Token).ConfigureAwait(false);
        Console.WriteLine();
        Console.WriteLine($"Stop reason: {result.GetProperty("stopReason").GetString()}");
    }

    private static void ReportModel(JsonElement state, ref string? currentModel)
    {
        if (!state.TryGetProperty("configOptions", out JsonElement options) || options.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement option in options.EnumerateArray())
        {
            bool isModel = option.TryGetProperty("category", out JsonElement category) &&
                category.ValueKind == JsonValueKind.String && category.GetString() == "model";
            isModel |= option.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.String && id.GetString() == "model";
            if (isModel && option.TryGetProperty("currentValue", out JsonElement value) && value.ValueKind == JsonValueKind.String &&
                value.GetString() != currentModel)
            {
                currentModel = value.GetString();
                Console.WriteLine($"Model: {currentModel}");
            }
        }
    }
}
