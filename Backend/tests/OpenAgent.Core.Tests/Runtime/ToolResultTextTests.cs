using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using OpenAgent.Contracts.Files;
using OpenAgent.Core.Capabilities.Mcp;
using OpenAgent.Core.Runtime.Agent;
using Xunit;

namespace OpenAgent.Core.Tests.Runtime;

public class ToolResultTextTests
{
    [Fact]
    public void Render_McpMultiContentArray_JoinsTextParts()
    {
        // MCP 工具返回多个内容块时结果为 AIContent 数组；旧实现 ToString 只剩
        // "Microsoft.Extensions.AI.AIContent[]"，既展示给用户又回喂模型。
        AIContent[] contents =
        [
            new TextContent("first part"),
            new TextContent("second part")
        ];

        string? rendered = ToolResultText.Render(contents);

        Assert.Equal("first part\nsecond part", rendered);
    }

    [Fact]
    public void Render_MixedContentArray_DescribesNonTextParts()
    {
        AIContent[] contents =
        [
            new TextContent("caption"),
            new DataContent(new ReadOnlyMemory<byte>([1, 2, 3]), "image/png")
        ];

        string? rendered = ToolResultText.Render(contents);

        Assert.StartsWith("caption", rendered, StringComparison.Ordinal);
        Assert.Contains("[binary content: image/png, 3 bytes]", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_SingleTextContent_ReturnsText()
    {
        Assert.Equal("only block", ToolResultText.Render(new TextContent("only block")));
    }

    [Fact]
    public void Render_Null_ReturnsNull()
    {
        Assert.Null(ToolResultText.Render(null));
    }

    [Fact]
    public void Render_JsonElement_ReturnsRawJson()
    {
        using JsonDocument document = JsonDocument.Parse("{\"ok\":true}");
        JsonElement element = document.RootElement.Clone();

        Assert.Equal("{\"ok\":true}", ToolResultText.Render(element));
    }

    [Fact]
    public void Render_UnknownObject_ReturnsJsonSerialization()
    {
        Assert.Equal("{\"a\":1}", ToolResultText.Render(new Dictionary<string, object?> { ["a"] = 1 }));
    }

    [Fact]
    public void Render_TextContentFromEmbeddedTextResource_ReturnsBodyOnly()
    {
        // MCP 内嵌文本资源经 SDK 投影成 TextContent：text 块只取正文，不附带资源 URI。
        var content = new TextContent("resource body")
        {
            RawRepresentation = new EmbeddedResourceBlock
            {
                Resource = new TextResourceContents { Uri = "file:///notes.md", Text = "resource body" }
            }
        };

        Assert.Equal("resource body", ToolResultText.Render(content));
    }

    [Fact]
    public void Render_DataContentFromEmbeddedBlobResource_KeepsUriWithPlaceholder()
    {
        // MCP 内嵌二进制资源经 SDK 投影成 DataContent，资源 URI 只在
        // RawRepresentation 里：占位符必须带上 URI，模型才能凭它再获取该资源。
        byte[] bytes = [1, 2, 3];
        var content = new DataContent(new ReadOnlyMemory<byte>(bytes), "application/pdf")
        {
            RawRepresentation = new EmbeddedResourceBlock
            {
                Resource = BlobResourceContents.FromBytes(bytes, "file:///report.pdf", "application/pdf")
            }
        };

        string? rendered = ToolResultText.Render(content);

        Assert.Equal(
            "file:///report.pdf [binary content: application/pdf, 3 bytes]",
            rendered);
    }

    [Fact]
    public void Render_CallToolResult_TextBlocks_ExtractBodies()
    {
        // 带 _meta/isError/structuredContent 的结果由 McpClientTool 序列化成
        // JsonElement，经落盘管道反序列化后走此渲染：按内容块规则展平，
        // 而不是把原始 JSON 灌给模型。
        var result = new CallToolResult
        {
            Content =
            [
                new TextContentBlock { Text = "first" },
                new TextContentBlock { Text = "second" }
            ]
        };

        Assert.Equal("first\nsecond", ToolResultText.RenderCallToolResult(result));
    }

    [Fact]
    public void Render_CallToolResult_TextResource_ExtractsBody()
    {
        var result = new CallToolResult
        {
            Content =
            [
                new EmbeddedResourceBlock
                {
                    Resource = new TextResourceContents { Uri = "file:///notes.md", Text = "resource body" }
                }
            ]
        };

        Assert.Equal("resource body", ToolResultText.RenderCallToolResult(result));
    }

    [Fact]
    public void Render_CallToolResult_BlobResource_KeepsUriAndPlaceholder()
    {
        var result = new CallToolResult
        {
            Content =
            [
                new EmbeddedResourceBlock
                {
                    Resource = BlobResourceContents.FromBytes(new byte[] { 1, 2, 3 }, "file:///report.pdf", "application/pdf")
                }
            ]
        };

        Assert.Equal(
            "file:///report.pdf [binary content: application/pdf, 3 bytes]",
            ToolResultText.RenderCallToolResult(result));
    }

    [Fact]
    public void Render_CallToolResult_ImageBlock_RendersBinaryPlaceholder()
    {
        var result = new CallToolResult
        {
            Content = [ImageContentBlock.FromBytes(new byte[] { 1, 2, 3 }, "image/png")]
        };

        Assert.Equal("[binary content: image/png, 3 bytes]", ToolResultText.RenderCallToolResult(result));
    }

    [Fact]
    public void Render_CallToolResult_ResourceLink_KeepsUri()
    {
        // resource_link 无法投影成 AIContent，是触发 JsonElement 兜底形态的典型块。
        var result = new CallToolResult
        {
            Content = [new ResourceLinkBlock { Uri = "mem://spec", Name = "spec" }]
        };

        Assert.Equal("mem://spec", ToolResultText.RenderCallToolResult(result));
    }

    [Fact]
    public void Render_CallToolResult_ErrorResult_PrefixesToolError()
    {
        // MCP 的 isError 结果不抛异常，靠渲染层标注，模型才能与正常结果区分。
        var result = new CallToolResult
        {
            Content = [new TextContentBlock { Text = "invalid date range" }],
            IsError = true
        };

        Assert.Equal("[tool error] invalid date range", ToolResultText.RenderCallToolResult(result));
    }

    [Fact]
    public void Render_CallToolResult_StructuredContent_AppendsJson()
    {
        var result = new CallToolResult
        {
            Content = [new TextContentBlock { Text = "done" }],
            StructuredContent = JsonDocument.Parse("{\"rows\":2}").RootElement.Clone()
        };

        Assert.Equal("done\n{\"rows\":2}", ToolResultText.RenderCallToolResult(result));
    }

    [Fact]
    public async Task Wrap_McpErrorResultJson_RendersBlocksAndAppliesBudget()
    {
        // isError/structuredContent/_meta 形态的 JsonElement 由落盘装饰器归一成
        // 文本，再进隔离/预算管道：可读且不超预算。
        string longText = new('x', 3_000);
        JsonElement json = Serialize(new CallToolResult
        {
            Content = [new TextContentBlock { Text = $"HEAD-{longText}-TAIL" }],
            IsError = true
        });
        AITool wrapped = IsolatedToolFunction.Wrap(
            McpResourcePersistingFunction.Wrap(
                new StubTool("mcp__srv__report", _ => ValueTask.FromResult<object?>(json)),
                new NullStore()),
            budget: new ToolResultBudget(1_000, "narrow the query"));

        string result = Assert.IsType<string>(await InvokeAsync(wrapped));

        Assert.StartsWith("[tool error] HEAD-", result, StringComparison.Ordinal);
        Assert.EndsWith("-TAIL", result, StringComparison.Ordinal);
        Assert.True(result.Length <= 1_000, $"truncated length {result.Length} must not exceed the budget");
    }

    [Fact]
    public async Task Wrap_McpMultiContentResult_RendersJoinedTextNotTypeName()
    {
        AITool wrapped = IsolatedToolFunction.Wrap(
            new StubTool("mcp__srv__fetch", _ => ValueTask.FromResult<object?>(
                new AIContent[] { new TextContent("part-1"), new TextContent("part-2") })),
            budget: ToolResultBudget.Unlimited);

        object? result = await InvokeAsync(wrapped);

        Assert.Equal("part-1\npart-2", result);
    }

    [Fact]
    public async Task Wrap_McpMultiContentResult_AppliesBudget()
    {
        string longText = new('x', 3_000);
        AITool wrapped = IsolatedToolFunction.Wrap(
            new StubTool("mcp__srv__dump", _ => ValueTask.FromResult<object?>(
                new AIContent[] { new TextContent($"HEAD-{longText}-TAIL") })),
            budget: new ToolResultBudget(1_000, "narrow the query"));

        string result = Assert.IsType<string>(await InvokeAsync(wrapped));

        Assert.True(result.Length <= 1_000, $"truncated length {result.Length} must not exceed the budget");
        Assert.StartsWith("HEAD-", result, StringComparison.Ordinal);
        Assert.EndsWith("-TAIL", result, StringComparison.Ordinal);
    }

    private static async Task<object?> InvokeAsync(AITool wrapped) =>
        await Assert.IsAssignableFrom<AIFunction>(wrapped)
            .InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

    /// <summary>按 McpClientTool 的兜底路径把结果序列化成 CallToolResult JSON。</summary>
    private static JsonElement Serialize(CallToolResult result) =>
        JsonSerializer.SerializeToElement(result, McpJsonUtilities.DefaultOptions);

    private sealed class NullStore : IMcpResourceStore
    {
        public ValueTask<FileAsset?> TryStoreAsync(
            string fileName,
            string? mediaType,
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken) => ValueTask.FromResult<FileAsset?>(null);
    }

    private sealed class StubTool(string name, Func<AIFunctionArguments, ValueTask<object?>> invoke) : AIFunction
    {
        public override string Name => name;
        public override string Description => "stub";
        public override JsonElement JsonSchema =>
            JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone();

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken) => invoke(arguments);
    }
}
