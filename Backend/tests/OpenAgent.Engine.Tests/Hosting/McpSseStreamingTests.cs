using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Conversation;
using Xunit;
using static OpenAgent.Engine.Tests.Hosting.SseToolStreamingTests;

namespace OpenAgent.Engine.Tests.Hosting;

/// <summary>
/// 用真实 MCP 协议复现生产缺陷形态：工厂报表类工具按 section 返回多个内容块，
/// McpClientTool 对多内容块返回 AIContent 数组——旧实现直接 ToString 得到
/// "Microsoft.Extensions.AI.AIContent[]"，且垃圾结果回喂模型触发空参重试。
/// 这里在进程内起真实 StreamableHttp MCP 服务端，Engine 侧走完整的
/// McpToolFactory → McpClientPool → McpClientTool → IsolatedToolFunction 链路。
/// </summary>
public sealed class McpSseStreamingTests
{
    private const string ServerName = "localmcp";
    private const string ToolName = "scenario_report_query";
    private const string RuntimeToolName = $"mcp__{ServerName}__{ToolName}";

    [Fact]
    public async Task Stream_McpMultiContentTool_ResultIsReadableTextAndRunCompletes()
    {
        var provider = new ScriptedChatClient(
        [
            [
                new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("call-report-1", RuntimeToolName,
                        new Dictionary<string, object?>
                        {
                            ["mode"] = "trusted-query",
                            ["reportCode"] = "scenario-one-report-v1"
                        })])
            ],
            [
                new ChatResponseUpdate(ChatRole.Assistant, "报告已生成。"),
                new ChatResponseUpdate(ChatRole.Assistant,
                    [new UsageContent(new UsageDetails
                    {
                        InputTokenCount = 9,
                        OutputTokenCount = 4,
                        TotalTokenCount = 13
                    })])
            ]
        ]);
        await using McpServerHandle mcpServer = await StartMcpServerAsync();
        AgentConfig agentConfig = new()
        {
            MaxTurns = 6,
            Mcp = new McpConfig
            {
                Servers =
                [
                    new McpServerConfig
                    {
                        Name = ServerName,
                        Url = $"{mcpServer.Endpoint}/mcp",
                        Type = McpServerType.Http
                    }
                ]
            }
        };
        // DeferredToolThreshold=0：全部内联，先验证最短路径（延迟路径另有覆盖）。
        await using StreamingHost host = await StreamingHost.StartAsync(
            provider,
            agentConfig,
            new Dictionary<string, string?> { ["Mcp:DeferredToolThreshold"] = "0" });

        List<(string Event, JsonElement Data)> frames =
            await host.PostStreamAsync("mcp-report-conversation");

        (string _, JsonElement callData) = Assert.Single(frames, frame => frame.Event == "tool_call");
        Assert.Equal(RuntimeToolName, callData.GetProperty("toolName").GetString());

        (string _, JsonElement resultData) = Assert.Single(frames, frame => frame.Event == "tool_result");
        string? content = resultData.GetProperty("content").GetString();
        // 多内容块必须被展平为可读文本：两个 section 都在，且不是类型名字符串。
        Assert.NotNull(content);
        Assert.Contains("[ft] lot V263638_8 summary", content, StringComparison.Ordinal);
        Assert.Contains("[qims] report scenario-one-report-v1", content, StringComparison.Ordinal);
        Assert.DoesNotContain("AIContent", content, StringComparison.Ordinal);

        (string _, JsonElement doneData) = Assert.Single(frames, frame => frame.Event == "done");
        Assert.True(doneData.GetProperty("done").GetBoolean());

        // 回喂模型的下一轮请求：结果已是字符串，垃圾类型名不会进入 wire。
        Assert.Equal(2, provider.Requests.Count);
        FunctionResultContent wireResult = Assert.Single(
            provider.Requests[1].SelectMany(message => message.Contents)
                .OfType<FunctionResultContent>());
        string wire = Assert.IsType<string>(wireResult.Result);
        Assert.Contains("[ft] lot", wire, StringComparison.Ordinal);
        Assert.DoesNotContain("AIContent", wire, StringComparison.Ordinal);

        // 持久化的 tool 行同样干净（旧会话复现/刷新后展示用）。
        ConversationRecord? record = await host.Store.GetRecordAsync(
            "tenant-1", "mcp-report-conversation");
        Assert.NotNull(record);
        ConversationMessage? toolRow = record.Messages?.LastOrDefault(
            row => row.Role == "tool");
        Assert.NotNull(toolRow);
        Assert.Contains("[ft] lot", toolRow!.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("AIContent", toolRow.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stream_McpToolViaDeferredSearch_ResultStillReadable()
    {
        // 用户生产环境大概率命中延迟加载：MCP 工具超过阈值（默认 20）时经
        // search_tools 检索激活。验证激活注入路径同样经 IsolatedToolFunction
        // 包装、多内容块结果同样干净。
        var provider = new ScriptedChatClient(
        [
            [
                new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("search-1", "search_tools",
                        new Dictionary<string, object?> { ["query"] = "scenario report" })])
            ],
            [
                new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("call-report-2", RuntimeToolName,
                        new Dictionary<string, object?>
                        {
                            ["mode"] = "trusted-query",
                            ["reportCode"] = "scenario-one-report-v1"
                        })])
            ],
            [
                new ChatResponseUpdate(ChatRole.Assistant, "报告已生成。")
            ]
        ]);
        await using McpServerHandle mcpServer = await StartMcpServerAsync();
        AgentConfig agentConfig = new()
        {
            MaxTurns = 8,
            Mcp = new McpConfig
            {
                Servers =
                [
                    new McpServerConfig
                    {
                        Name = ServerName,
                        Url = $"{mcpServer.Endpoint}/mcp",
                        Type = McpServerType.Http
                    }
                ]
            }
        };
        await using StreamingHost host = await StreamingHost.StartAsync(
            provider,
            agentConfig,
            new Dictionary<string, string?> { ["Mcp:DeferredToolThreshold"] = "1" });

        List<(string Event, JsonElement Data)> frames =
            await host.PostStreamAsync("mcp-deferred-conversation");

        (string _, JsonElement resultData) = Assert.Single(
            frames,
            frame => frame.Event == "tool_result"
                && frame.Data.GetProperty("toolName").GetString() == RuntimeToolName);
        string? content = resultData.GetProperty("content").GetString();
        Assert.NotNull(content);
        Assert.Contains("[ft] lot V263638_8 summary", content, StringComparison.Ordinal);
        Assert.DoesNotContain("AIContent", content, StringComparison.Ordinal);

        Assert.Single(frames, frame => frame.Event == "done");
    }

    /// <summary>进程内真实 StreamableHttp MCP 服务端：报表工具按 section 返回多个内容块。</summary>
    private static async Task<McpServerHandle> StartMcpServerAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production
        });
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddMcpServer(options => options.ServerInfo = new()
        {
            Name = ServerName,
            Version = "1.0.0"
        }).WithHttpTransport();
        builder.Services.AddSingleton(McpServerTool.Create(
            (string mode, string reportCode) => new TextContent[]
            {
                new($"[ft] lot V263638_8 summary for mode={mode}"),
                new($"[qims] report {reportCode}")
            },
            new McpServerToolCreateOptions { Name = ToolName }));
        // 第二个工具使工具总数超过阈值 1，触发延迟加载路径（search_tools）。
        builder.Services.AddSingleton(McpServerTool.Create(
            (string keyword) => $"[lookup] no result for {keyword}",
            new McpServerToolCreateOptions { Name = "unrelated_lookup" }));

        WebApplication application = builder.Build();
        application.Urls.Clear();
        application.Urls.Add("http://127.0.0.1:0");
        application.MapMcp("/mcp");
        await application.StartAsync().ConfigureAwait(false);
        string endpoint = application.Services
            .GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new McpServerHandle(application, endpoint);
    }

    private sealed class McpServerHandle(WebApplication application, string endpoint)
        : IAsyncDisposable
    {
        public string Endpoint { get; } = endpoint.TrimEnd('/');

        public async ValueTask DisposeAsync()
        {
            await application.DisposeAsync().ConfigureAwait(false);
        }
    }
}
