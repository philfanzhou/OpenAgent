using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Files;
using OpenAgent.Core.Capabilities.Mcp;
using Xunit;
using static OpenAgent.Engine.Tests.Hosting.SseToolStreamingTests;

namespace OpenAgent.Engine.Tests.Hosting;

/// <summary>
/// MCP 资源链路的端到端验证（进程内真实 StreamableHttp MCP 服务端，完整走
/// McpToolFactory → McpClientTool → McpResourcePersistingFunction → IsolatedToolFunction）：
/// 1) 工具结果内嵌 blob 资源 → 对象存储落盘（桩替换）→ [File: ...] fileId 描述符，
///    resource_link 保留 URI，base64 不进模型上下文；
/// 2) read_mcp_resource 桥接工具按 URI 读回资源内容。
/// </summary>
public sealed class McpResourceStreamingTests
{
    private const string ServerName = "localmcp";
    private const string ToolName = "scenario_report_export";
    private const string RuntimeToolName = $"mcp__{ServerName}__{ToolName}";
    private const string BlobUri = "mem://files/report.pdf";
    private const string ResourceUri = "mem://notes.md";

    [Fact]
    public async Task Stream_McpToolWithEmbeddedBlob_PersistsResourceAndRendersDescriptor()
    {
        var provider = new ScriptedChatClient(
        [
            [
                new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("call-export-1", RuntimeToolName,
                        new Dictionary<string, object?> { ["reportCode"] = "scenario-one-report-v1" })])
            ],
            [
                new ChatResponseUpdate(ChatRole.Assistant, "报告与内链已生成。")
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
        await using StreamingHost host = await StreamingHost.StartAsync(
            provider,
            agentConfig,
            new Dictionary<string, string?> { ["Mcp:DeferredToolThreshold"] = "0" },
            services =>
            {
                services.RemoveAll<IMcpResourceStore>();
                services.AddSingleton<IMcpResourceStore>(new StubResourceStore());
            });

        List<(string Event, JsonElement Data)> frames =
            await host.PostStreamAsync("mcp-blob-conversation");

        (string _, JsonElement resultData) = Assert.Single(
            frames, frame => frame.Event == "tool_result");
        string? content = resultData.GetProperty("content").GetString();
        Assert.NotNull(content);
        // 文本块保留、blob 资源替换为 fileId 内链描述符、resource_link 保留 URI。
        Assert.Contains("report generated for scenario-one-report-v1", content, StringComparison.Ordinal);
        Assert.Contains(
            "[File: report.pdf] fileId=file-mcp-e2e (application/pdf, 3 bytes) from mem://files/report.pdf",
            content,
            StringComparison.Ordinal);
        Assert.Contains("mem://spec", content, StringComparison.Ordinal);
        // base64 全文（[1,2,3] = "AQID"）绝不进入模型上下文。
        Assert.DoesNotContain("AQID", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stream_ReadMcpResource_ReturnsResourceContent()
    {
        var provider = new ScriptedChatClient(
        [
            [
                new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("call-resource-1", "read_mcp_resource",
                        new Dictionary<string, object?>
                        {
                            ["server"] = ServerName,
                            ["uri"] = ResourceUri
                        })])
            ],
            [
                new ChatResponseUpdate(ChatRole.Assistant, "已读取资源。")
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
        await using StreamingHost host = await StreamingHost.StartAsync(
            provider,
            agentConfig,
            new Dictionary<string, string?> { ["Mcp:DeferredToolThreshold"] = "0" });

        List<(string Event, JsonElement Data)> frames =
            await host.PostStreamAsync("mcp-resource-conversation");

        (string _, JsonElement resultData) = Assert.Single(
            frames, frame => frame.Event == "tool_result");
        Assert.Equal("read_mcp_resource", resultData.GetProperty("toolName").GetString());
        string? content = resultData.GetProperty("content").GetString();
        Assert.NotNull(content);
        Assert.Contains("resource body text for e2e", content, StringComparison.Ordinal);
    }

    /// <summary>进程内真实 StreamableHttp MCP 服务端：导出工具返回 文本 + 内嵌 blob 资源 + 资源链接。</summary>
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
            (string reportCode) => new CallToolResult
            {
                Content =
                [
                    new TextContentBlock { Text = $"report generated for {reportCode}" },
                    new EmbeddedResourceBlock
                    {
                        Resource = BlobResourceContents.FromBytes(
                            new byte[] { 1, 2, 3 }, BlobUri, "application/pdf")
                    },
                    new ResourceLinkBlock { Uri = "mem://spec", Name = "spec" }
                ]
            },
            new McpServerToolCreateOptions { Name = ToolName }));
        builder.Services.AddSingleton(McpServerResource.Create(
            () => new ReadResourceResult
            {
                Contents =
                [
                    new TextResourceContents { Uri = ResourceUri, Text = "resource body text for e2e" }
                ]
            },
            new McpServerResourceCreateOptions
            {
                UriTemplate = ResourceUri,
                Name = "notes",
                MimeType = "text/markdown"
            }));
        // 第二个工具使工具目录形态与既有宿主一致（本组测试内联注入）。
        builder.Services.AddSingleton(McpServerTool.Create(
            (string keyword) => $"[lookup] no result for {keyword}",
            new McpServerToolCreateOptions { Name = "scenario_lookup" }));

        WebApplication application = builder.Build();
        application.Urls.Clear();
        application.Urls.Add("http://127.0.0.1:0");
        application.MapMcp("/mcp");
        await application.StartAsync().ConfigureAwait(false);
        string endpoint = application.Services
            .GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new McpServerHandle(application, endpoint.TrimEnd('/'));
    }

    private sealed class McpServerHandle(WebApplication application, string endpoint)
        : IAsyncDisposable
    {
        public string Endpoint { get; } = endpoint;

        public async ValueTask DisposeAsync()
        {
            await application.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>对象存储桩：回填固定 FileAsset，验证"描述符内链"形态而非真实 S3 写入。</summary>
    private sealed class StubResourceStore : IMcpResourceStore
    {
        public ValueTask<FileAsset?> TryStoreAsync(
            string fileName,
            string? mediaType,
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken) => ValueTask.FromResult<FileAsset?>(new FileAsset
            {
                FileId = "file-mcp-e2e",
                TenantId = "tenant-1",
                OwnerUserId = "user-1",
                FileName = fileName,
                MediaType = mediaType ?? "application/octet-stream",
                Length = data.Length,
                Sha256 = "00",
                ObjectKey = $"tenant-1/user-1/{fileName}",
                Source = FileAssetSource.Agent,
                State = FileAssetState.Ready,
                CreatedAt = DateTimeOffset.UtcNow
            });
    }
}
