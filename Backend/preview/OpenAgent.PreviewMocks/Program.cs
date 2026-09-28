// PR #143 预览用 mock 服务（本地验证 MCP resource_link → read_mcp_resource → 落盘链路）：
//   /mcp                 真实 StreamableHttp MCP 服务端：export_report 返回 文本 + 两个 resource_link
//                        （markdown 报告 + png 图表），按 URI 经 resources/read 读回。
//   /v1/chat/completions OpenAI 兼容 mock：按对话中已出现的工具结果决定下一步
//                        （export_report → read_mcp_resource ×2 → 最终总结），流式 SSE。
//   /v1/models           OpenAI SDK 构造客户端时可能探测模型列表。
// 不进 OpenAgent.sln，仅供本地预览（dotnet run）。

using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:53080");

builder.Services.AddMcpServer(options =>
{
    options.ServerInfo = new() { Name = "mockmcp", Version = "1.0.0" };
}).WithHttpTransport();

const string ReportUri = "mem://reports/q3.md";
const string ChartUri = "mem://files/chart.png";

builder.Services.AddSingleton(McpServerTool.Create(
    (string code) => new CallToolResult
    {
        // 纯 resource_link（不带内嵌 blob）：触发 McpClientTool 的 JsonElement 兜底形态，
        // 正是 read_mcp_resource 桥接工具要覆盖的场景。
        Content =
        [
            new TextContentBlock { Text = $"Quarterly report '{code}' generated. Full content is in the linked resources." },
            new ResourceLinkBlock { Uri = ReportUri, Name = "q3-report" },
            new ResourceLinkBlock { Uri = ChartUri, Name = "revenue-chart" }
        ]
    },
    new McpServerToolCreateOptions
    {
        Name = "export_report",
        Description = "Generate the quarterly report package; returns resource links to the report document and chart."
    }));

builder.Services.AddSingleton(McpServerResource.Create(
    () => new ReadResourceResult
    {
        Contents = [new TextResourceContents
        {
            Uri = ReportUri,
            MimeType = "text/markdown",
            Text = "# Q3 Report\n\nRevenue: 1,284,500 USD (up 18% QoQ).\nActive tenants: 312.\nTop cost driver: egress."
        }]
    },
    new McpServerResourceCreateOptions { UriTemplate = ReportUri, Name = "q3-report", MimeType = "text/markdown" }));

// 1x1 红色 PNG：真实二进制资源，验证 read_mcp_resource 的 blob → 对象存储 → fileId 描述符路径。
byte[] chartPng = Convert.FromHexString(
        "89504E470D0A1A0A0000000D4948445200000001000000010802000000907753DE0000000C4944415408D763F8CFC000000301010018DD8DB00000000049454E44AE426082");
builder.Services.AddSingleton(McpServerResource.Create(
    () => new ReadResourceResult
    {
        Contents = [BlobResourceContents.FromBytes(chartPng, ChartUri, "image/png")]
    },
    new McpServerResourceCreateOptions { UriTemplate = ChartUri, Name = "revenue-chart", MimeType = "image/png" }));

var app = builder.Build();
app.MapMcp("/mcp");

app.MapGet("/v1/models", () => Results.Json(new
{
    @object = "list",
    data = new[] { new { id = "mock-model", @object = "model" } }
}));

app.MapPost("/v1/chat/completions", async (HttpContext http) =>
{
    using var document = await JsonDocument.ParseAsync(http.Request.Body);
    JsonElement root = document.RootElement;
    bool stream = root.TryGetProperty("stream", out JsonElement streamElement) && streamElement.GetBoolean();
    string turn = DecideTurn(root);

    if (!stream)
    {
        await Results.Json(BuildCompletion(turn)).ExecuteAsync(http);
        return;
    }

    http.Response.ContentType = "text/event-stream";
    foreach (string payload in BuildChunks(turn))
    {
        await http.Response.WriteAsync($"data: {payload}\n\n", Encoding.UTF8);
        await http.Response.Body.FlushAsync();
    }
    await http.Response.WriteAsync("data: [DONE]\n\n", Encoding.UTF8);
});

// 依据对话里已出现的工具结果决定下一步：无 → export；已导出 → 读文本；已读文本 → 读图表；
// 已读图表（结果含 [File: 描述符） → 总结。
static string DecideTurn(JsonElement root)
{
    if (!root.TryGetProperty("messages", out JsonElement messages)
        || messages.ValueKind != JsonValueKind.Array)
    {
        return "final";
    }

    string toolTranscript = string.Join("\n", messages.EnumerateArray()
        .Where(message => message.TryGetProperty("role", out JsonElement role)
            && role.GetString() == "tool"
            && message.TryGetProperty("content", out JsonElement content)
            && content.ValueKind == JsonValueKind.String)
        .Select(message => message.GetProperty("content").GetString() ?? string.Empty));

    if (toolTranscript.Contains("[File: chart.png]", StringComparison.Ordinal)
        || (toolTranscript.Contains(ChartUri, StringComparison.Ordinal)
            && toolTranscript.Contains("[binary content:", StringComparison.Ordinal)))
    {
        return "final";
    }
    if (toolTranscript.Contains("# Q3 Report", StringComparison.Ordinal))
    {
        return "read-chart";
    }
    if (toolTranscript.Contains(ReportUri, StringComparison.Ordinal))
    {
        return "read-report";
    }
    return "export";
}

static object BuildCompletion(string turn) => turn switch
{
    "export" => Completion(new[] { ToolCall("call-1", "mcp__mockmcp__export_report", """{"code":"q3"}""") }, "tool_calls"),
    "read-report" => Completion(new[] { ToolCall("call-2", "read_mcp_resource", $$"""{"server":"mockmcp","uri":"{{ReportUri}}"}""") }, "tool_calls"),
    "read-chart" => Completion(new[] { ToolCall("call-3", "read_mcp_resource", $$"""{"server":"mockmcp","uri":"{{ChartUri}}"}""") }, "tool_calls"),
    _ => Completion(null, "stop"),
};

static IEnumerable<string> BuildChunks(string turn)
{
    switch (turn)
    {
        case "export":
            yield return Chunk(new { tool_calls = new object[] { ToolCall("call-1", "mcp__mockmcp__export_report", """{"code":"q3"}""") } }, null);
            yield return Chunk(new { }, "tool_calls");
            break;
        case "read-report":
            yield return Chunk(new { tool_calls = new object[] { ToolCall("call-2", "read_mcp_resource", $$"""{"server":"mockmcp","uri":"{{ReportUri}}"}""") } }, null);
            yield return Chunk(new { }, "tool_calls");
            break;
        case "read-chart":
            yield return Chunk(new { tool_calls = new object[] { ToolCall("call-3", "read_mcp_resource", $$"""{"server":"mockmcp","uri":"{{ChartUri}}"}""") } }, null);
            yield return Chunk(new { }, "tool_calls");
            break;
        default:
            yield return Chunk(new { content = "Q3 报告已读取：营收 1,284,500 USD（环比 +18%）；图表已存为文件资产，可经 fileId 发布给用户。" }, null);
            yield return Chunk(new { }, "stop");
            break;
    }
}

static object ToolCall(string id, string name, string arguments) => new
{
    index = 0,
    id,
    type = "function",
    function = new { name, arguments }
};

static string Chunk(object delta, string? finishReason) => JsonSerializer.Serialize(new
{
    id = "chatcmpl-mock",
    @object = "chat.completion.chunk",
    created = 1760000000,
    model = "mock-model",
    choices = new[] { new { index = 0, delta, finish_reason = finishReason } }
});

static object Completion(object? toolCalls, string finishReason) => new
{
    id = "chatcmpl-mock",
    @object = "chat.completion",
    created = 1760000000,
    model = "mock-model",
    choices = new[]
    {
        new
        {
            index = 0,
            message = toolCalls is null
                ? (object)new { role = "assistant", content = "Q3 报告已读取：营收 1,284,500 USD（环比 +18%）；图表已存为文件资产。" }
                : new { role = "assistant", content = (string?)null, tool_calls = toolCalls },
            finish_reason = finishReason
        }
    }
};

app.Run();
