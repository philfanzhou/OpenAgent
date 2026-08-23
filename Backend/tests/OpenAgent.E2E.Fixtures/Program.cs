using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: OpenAgent.E2E.Fixtures <mcp|llm> [options]");
    return 2;
}

if (string.Equals(args[0], "mcp", StringComparison.OrdinalIgnoreCase))
{
    await RunMcpAsync(args.Skip(1).FirstOrDefault()).ConfigureAwait(false);
    return 0;
}

if (string.Equals(args[0], "llm", StringComparison.OrdinalIgnoreCase))
{
    await RunLlmAsync(args.Skip(1).ToArray()).ConfigureAwait(false);
    return 0;
}

Console.Error.WriteLine($"Unknown fixture mode '{args[0]}'.");
return 2;

static async Task RunMcpAsync(string? protocolVersion)
{
    McpServerTool tool = McpServerTool.Create(
        (int left, int right) => new { sum = left + right, source = "official-mcp-stdio" },
        new McpServerToolCreateOptions
        {
            Name = "add",
            Description = "Adds two integers and identifies the official MCP stdio fixture."
        });
    var tools = new McpServerPrimitiveCollection<McpServerTool>();
    tools.Add(tool);
    var options = new McpServerOptions
    {
        ServerInfo = new Implementation { Name = "openagent-e2e", Version = "1.0.0" },
        ProtocolVersion = string.IsNullOrWhiteSpace(protocolVersion) ? null : protocolVersion,
        ToolCollection = tools
    };
    await using var transport = new StdioServerTransport(options, NullLoggerFactory.Instance);
    await using McpServer server = McpServer.Create(
        transport,
        options,
        NullLoggerFactory.Instance,
        serviceProvider: null);
    await server.RunAsync().ConfigureAwait(false);
}

static async Task RunLlmAsync(string[] fixtureArgs)
{
    WebApplicationOptions options = new()
    {
        Args = fixtureArgs,
        ApplicationName = typeof(Program).Assembly.FullName
    };
    WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(options);
    WebApplication app = builder.Build();
    app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
    app.MapPost("/v1/chat/completions", HandleChatCompletionAsync);
    await app.RunAsync().ConfigureAwait(false);
}

static async Task HandleChatCompletionAsync(HttpContext context)
{
    using JsonDocument document = await JsonDocument.ParseAsync(
        context.Request.Body,
        cancellationToken: context.RequestAborted).ConfigureAwait(false);
    JsonElement request = document.RootElement;
    bool streaming = request.TryGetProperty("stream", out JsonElement streamElement)
        && streamElement.ValueKind == JsonValueKind.True;
    int completedTools = CountCompletedTools(request);
    string? toolName = SelectTool(request, completedTools);
    string content = BuildFinalContent(request);
    Console.Error.WriteLine(
        "E2E model request: completedTools={0}, selectedTool={1}, offeredTools={2}",
        completedTools,
        toolName ?? "none",
        string.Join(",", ReadToolNames(request)));

    if (streaming)
    {
        context.Response.ContentType = "text/event-stream";
        if (toolName != null)
        {
            string arguments = BuildToolArguments(request, toolName);
            await WriteChunkAsync(
                context,
                new Dictionary<string, object?>
                {
                    ["role"] = "assistant",
                    ["tool_calls"] = new[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["index"] = 0,
                            ["id"] = $"call-{completedTools + 1}",
                            ["type"] = "function",
                            ["function"] = new Dictionary<string, object?>
                            {
                                ["name"] = toolName,
                                ["arguments"] = arguments
                            }
                        }
                    }
                },
                finishReason: null).ConfigureAwait(false);
            await WriteChunkAsync(context, new Dictionary<string, object?>(), "tool_calls").ConfigureAwait(false);
        }
        else
        {
            await WriteChunkAsync(
                context,
                new Dictionary<string, object?> { ["role"] = "assistant", ["content"] = content },
                finishReason: null).ConfigureAwait(false);
            await WriteChunkAsync(context, new Dictionary<string, object?>(), "stop").ConfigureAwait(false);
        }
        await context.Response.WriteAsync("data: [DONE]\n\n", context.RequestAborted).ConfigureAwait(false);
        return;
    }

    object message = toolName == null
        ? new Dictionary<string, object?> { ["role"] = "assistant", ["content"] = content }
        : new Dictionary<string, object?>
        {
            ["role"] = "assistant",
            ["content"] = null,
            ["tool_calls"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["id"] = $"call-{completedTools + 1}",
                    ["type"] = "function",
                    ["function"] = new Dictionary<string, object?>
                    {
                        ["name"] = toolName,
                        ["arguments"] = BuildToolArguments(request, toolName)
                    }
                }
            }
        };
    await context.Response.WriteAsJsonAsync(
        CompletionEnvelope(message, toolName == null ? "stop" : "tool_calls"),
        context.RequestAborted).ConfigureAwait(false);
}

static async Task WriteChunkAsync(
    HttpContext context,
    IReadOnlyDictionary<string, object?> delta,
    string? finishReason)
{
    var chunk = new Dictionary<string, object?>
    {
        ["id"] = "chatcmpl-openagent-e2e",
        ["object"] = "chat.completion.chunk",
        ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        ["model"] = "openagent-e2e",
        ["choices"] = new[]
        {
            new Dictionary<string, object?>
            {
                ["index"] = 0,
                ["delta"] = delta,
                ["finish_reason"] = finishReason
            }
        }
    };
    await context.Response.WriteAsync(
        $"data: {JsonSerializer.Serialize(chunk)}\n\n",
        context.RequestAborted).ConfigureAwait(false);
    await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
}

static IReadOnlyDictionary<string, object?> CompletionEnvelope(object message, string finishReason) =>
    new Dictionary<string, object?>
    {
        ["id"] = "chatcmpl-openagent-e2e",
        ["object"] = "chat.completion",
        ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        ["model"] = "openagent-e2e",
        ["choices"] = new[]
        {
            new Dictionary<string, object?>
            {
                ["index"] = 0,
                ["message"] = message,
                ["finish_reason"] = finishReason
            }
        },
        ["usage"] = new Dictionary<string, object?>
        {
            ["prompt_tokens"] = 1,
            ["completion_tokens"] = 1,
            ["total_tokens"] = 2
        }
    };

static int CountCompletedTools(JsonElement request)
{
    if (!request.TryGetProperty("messages", out JsonElement messages)) return 0;
    return messages.EnumerateArray().Count(message =>
        message.TryGetProperty("role", out JsonElement role)
        && string.Equals(role.GetString(), "tool", StringComparison.Ordinal));
}

static string? SelectTool(JsonElement request, int completedTools)
{
    string[] priorities = completedTools switch
    {
        0 => ["mcp__"],
        1 => ["load_skill"],
        2 => ["run_skill_script"],
        _ => []
    };
    if (priorities.Length == 0
        || !request.TryGetProperty("tools", out JsonElement tools))
    {
        return null;
    }

    foreach (string priority in priorities)
    {
        foreach (JsonElement tool in tools.EnumerateArray())
        {
            string? name = tool.GetProperty("function").GetProperty("name").GetString();
            if (name != null && (name == priority || name.StartsWith(priority, StringComparison.Ordinal)))
            {
                return name;
            }
        }
    }
    return null;
}

static IReadOnlyList<string> ReadToolNames(JsonElement request)
{
    if (!request.TryGetProperty("tools", out JsonElement tools)) return [];
    return tools.EnumerateArray()
        .Select(tool => tool.GetProperty("function").GetProperty("name").GetString())
        .Where(name => name != null)
        .Select(name => name!)
        .ToArray();
}

static string BuildToolArguments(JsonElement request, string toolName)
{
    JsonElement function = request.GetProperty("tools")
        .EnumerateArray()
        .Select(tool => tool.GetProperty("function"))
        .First(tool => tool.GetProperty("name").GetString() == toolName);
    var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    if (function.TryGetProperty("parameters", out JsonElement parameters)
        && parameters.TryGetProperty("properties", out JsonElement properties))
    {
        foreach (JsonProperty property in properties.EnumerateObject())
        {
            string normalized = property.Name.ToLowerInvariant();
            values[property.Name] = normalized switch
            {
                "left" => 20,
                "right" => 22,
                "scriptname" => "scripts/calculate.py",
                "arguments" => new[] { "20", "22" },
                _ when normalized.Contains("skill", StringComparison.Ordinal) => "secure-calculator",
                _ when property.Value.TryGetProperty("type", out JsonElement type)
                    && type.GetString() == "array" => new[] { "20", "22" },
                _ => "secure-calculator"
            };
        }
    }
    string arguments = JsonSerializer.Serialize(values);
    Console.Error.WriteLine("E2E model arguments: tool={0}, arguments={1}", toolName, arguments);
    return arguments;
}

static string BuildFinalContent(JsonElement request)
{
    var results = new List<string>();
    if (request.TryGetProperty("messages", out JsonElement messages))
    {
        foreach (JsonElement message in messages.EnumerateArray())
        {
            if (message.TryGetProperty("role", out JsonElement role)
                && role.GetString() == "tool"
                && message.TryGetProperty("content", out JsonElement content))
            {
                results.Add(content.ValueKind == JsonValueKind.String
                    ? content.GetString() ?? string.Empty
                    : content.GetRawText());
            }
        }
    }
    string evidence = string.Join("\n", results);
    bool mcpSucceeded = evidence.Contains("official-mcp-stdio", StringComparison.Ordinal)
        && evidence.Contains("42", StringComparison.Ordinal);
    bool skillSucceeded = evidence.Contains("isolated-skill-python", StringComparison.Ordinal)
        && evidence.Contains("\"success\": true", StringComparison.Ordinal);
    return $"PR21 真实链路验收完成：MCP stdio 计算结果为 {(mcpSucceeded ? "42" : "未验证")}；"
        + $"secure-calculator Skill 沙盒脚本计算结果为 {(skillSucceeded ? "42" : "未验证")}。"
        + "会话已记录 MCP、Skill 加载和 Skill 脚本三类操作。";
}
