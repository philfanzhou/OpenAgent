using OpenAgent.Engine.Hosting;
using OpenAgent.Application.Tests.TestDoubles;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Runtime;
using Xunit;
using static OpenAgent.Engine.Tests.Hosting.SseToolStreamingTests;

namespace OpenAgent.Engine.Tests.Hosting;

/// <summary>
/// 用真实 MCP 协议验证工具链路：工厂报表类工具按 section 返回多个内容块，
/// McpClientTool 对多内容块返回 AIContent 数组——结果必须被展平为可读文本，
/// 而不是 "Microsoft.Extensions.AI.AIContent[]"。
/// 进程内起真实 StreamableHttp MCP 服务端，Engine 侧走完整的
/// McpToolFactory → McpClientPool → McpClientTool → IsolatedToolFunction 链路；
/// 工具由驱动替身触发（聊天推理不经模型循环）。
/// </summary>
public sealed class McpSseStreamingTests
{
    private const string ServerName = "localmcp";
    private const string ToolName = "scenario_report_query";
    private const string RuntimeToolName = $"mcp__{ServerName}__{ToolName}";

    [Fact]
    public async Task Stream_McpMultiContentTool_ResultIsReadableTextAndRunCompletes()
    {
        var driver = new ToolInvokingDriver(
        [
            (RuntimeToolName, new Dictionary<string, object?>
            {
                ["mode"] = "trusted-query",
                ["reportCode"] = "scenario-one-report-v1"
            })
        ],
        completion: "报告已生成。");
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
        await using StreamingHost host = await StreamingHost.StartAsync(
            driver,
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

    // 延迟加载（search_tools）路径属于已删除的 MAF 执行循环；运行时目录始终全量
    // 进入本轮工具桥，DeferredToolThreshold 不再影响行为，相应回归随之移除。

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
