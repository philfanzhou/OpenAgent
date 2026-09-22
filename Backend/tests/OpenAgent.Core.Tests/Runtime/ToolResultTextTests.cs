using System.Text.Json;
using Microsoft.Extensions.AI;
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
