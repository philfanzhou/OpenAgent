using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAgent.Contracts.Capabilities;
using OpenAgent.Core.Runtime.Agent;
using Xunit;

namespace OpenAgent.Core.Tests.Runtime;

public class ToolResultBudgetTests
{
    [Fact]
    public async Task Wrap_ResultExceedsBudget_KeepsHeadAndTailWithOmissionMarker()
    {
        // 头尾保留：开头（元信息）和结尾（结论/退出状态）可见，中间折叠并给出收窄提示。
        string content = $"HEAD-{new string('x', 5_000)}-TAIL";
        AITool wrapped = IsolatedToolFunction.Wrap(
            new StubTool("mcp__srv__dump", _ => ValueTask.FromResult<object?>(content)),
            budget: new ToolResultBudget(1_000, "narrow the query"));

        string result = await InvokeAsync(wrapped);

        Assert.True(result.Length <= 1_000, $"truncated length {result.Length} must not exceed the budget");
        Assert.StartsWith("HEAD-", result, StringComparison.Ordinal);
        Assert.EndsWith("-TAIL", result, StringComparison.Ordinal);
        Assert.Contains("characters omitted", result, StringComparison.Ordinal);
        Assert.Contains("narrow the query", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wrap_ResultWithinBudget_ReturnedUnchanged()
    {
        string content = "short result";
        AITool wrapped = IsolatedToolFunction.Wrap(
            new StubTool("read_file", _ => ValueTask.FromResult<object?>(content)),
            budget: new ToolResultBudget(1_000, "hint"));

        Assert.Equal("short result", await InvokeAsync(wrapped));
    }

    [Fact]
    public async Task Wrap_ZeroBudget_DisablesTruncation()
    {
        string content = new string('y', 3_000);
        AITool wrapped = IsolatedToolFunction.Wrap(
            new StubTool("read_file", _ => ValueTask.FromResult<object?>(content)),
            budget: ToolResultBudget.Unlimited);

        Assert.Equal(content, await InvokeAsync(wrapped));
    }

    [Fact]
    public async Task Wrap_ErrorEnvelope_IsNeverTruncated()
    {
        string longError = new string('e', 3_000);
        AITool wrapped = IsolatedToolFunction.Wrap(
            new StubTool("read_file", _ => ValueTask.FromResult<object?>(ToolResult.Error(longError))),
            budget: new ToolResultBudget(100, "hint"));

        string result = await InvokeAsync(wrapped);
        using JsonDocument document = JsonDocument.Parse(result);
        Assert.Equal(3_000, document.RootElement.GetProperty("error").GetString()!.Length);
    }

    [Fact]
    public async Task Wrap_ToolResultText_AppliesBudget()
    {
        string content = $"A{new string('z', 2_000)}Z";
        AITool wrapped = IsolatedToolFunction.Wrap(
            new StubTool("read_file", _ => ValueTask.FromResult<object?>(ToolResult.Text(content))),
            budget: new ToolResultBudget(500, "hint"));

        string result = await InvokeAsync(wrapped);
        Assert.True(result.Length <= 500);
        Assert.StartsWith("A", result, StringComparison.Ordinal);
        Assert.EndsWith("Z", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_ClassifiesToolsByKind()
    {
        var options = new AgentExecutionOptions();
        Assert.Equal(options.ReadToolResultCharBudget, ToolResultBudgets.Resolve(options, "read_file").MaxChars);
        Assert.Equal(options.ExecutionToolResultCharBudget, ToolResultBudgets.Resolve(options, "execute_code").MaxChars);
        Assert.Equal(options.McpToolResultCharBudget, ToolResultBudgets.Resolve(options, "mcp__any__tool").MaxChars);
        Assert.Equal(options.DefaultToolResultCharBudget, ToolResultBudgets.Resolve(options, "write_file").MaxChars);
        // 未识别工具必须落到默认预算，而不是不限。
        Assert.Equal(options.DefaultToolResultCharBudget, ToolResultBudgets.Resolve(options, "unknown_tool").MaxChars);
    }

    private static async Task<string> InvokeAsync(AITool wrapped) =>
        Assert.IsType<string>(await Assert.IsAssignableFrom<AIFunction>(wrapped)
            .InvokeAsync(new AIFunctionArguments(), CancellationToken.None));

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
