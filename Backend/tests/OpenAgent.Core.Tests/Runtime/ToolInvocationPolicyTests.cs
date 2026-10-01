using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OpenAgent.Contracts.Capabilities;
using OpenAgent.Contracts.Files;
using OpenAgent.Core.Capabilities;
using OpenAgent.Core.Files;
using OpenAgent.Core.Runtime.Agent;
using Xunit;

namespace OpenAgent.Core.Tests.Runtime;

public sealed class ToolInvocationPolicyTests
{
    [Fact]
    public async Task InvokeAsync_DynamicFunctionTimeout_ReturnsEnvelopeAndReleasesGate()
    {
        await using ToolInvocationPolicy policy = Create(new AgentExecutionOptions { ToolCallTimeoutSeconds = 1 });
        AIFunction slow = AIFunctionFactory.Create(async (CancellationToken token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return "unreachable";
        }, "run_skill_script");
        string result = Assert.IsType<string>(await policy.InvokeAsync(Context(slow), default));
        using JsonDocument envelope = JsonDocument.Parse(result);
        Assert.Equal("tool_timeout", envelope.RootElement.GetProperty("code").GetString());
        Assert.True(envelope.RootElement.GetProperty("timedOut").GetBoolean());
        Assert.Equal("ok", await policy.InvokeAsync(Context(new ProbeFunction(ToolConcurrency.Exclusive, () => Task.FromResult<object?>("ok"))), default));
    }

    [Fact]
    public async Task InvokeAsync_RequestCancellation_Propagates()
    {
        await using ToolInvocationPolicy policy = Create(new AgentExecutionOptions());
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await policy.InvokeAsync(Context(AIFunctionFactory.Create(() => "unused", "write")), cancellation.Token));
    }

    [Fact]
    public async Task InvokeAsync_StructuredError_PreservesEnvelopeBelowBudget()
    {
        await using ToolInvocationPolicy policy = Create(new AgentExecutionOptions { DefaultToolResultCharBudget = 10 });
        AIFunction tool = new ProbeFunction(ToolConcurrency.Exclusive, () => Task.FromResult<object?>(ToolResult.Error(new string('e', 300), "invalid_arguments")));
        string result = Assert.IsType<string>(await policy.InvokeAsync(Context(tool), default));
        using JsonDocument envelope = JsonDocument.Parse(result);
        Assert.Equal(300, envelope.RootElement.GetProperty("error").GetString()!.Length);
        Assert.Equal("invalid_arguments", envelope.RootElement.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task InvokeAsync_DeclaredConcurrency_AppliesSharedPolicy(bool readOnly, bool expectedOverlap)
    {
        await using ToolInvocationPolicy policy = Create(new AgentExecutionOptions());
        int active = 0;
        bool overlap = false;
        ProbeFunction tool = new(readOnly ? ToolConcurrency.ReadOnly : ToolConcurrency.Exclusive, async () =>
        {
            if (Interlocked.Increment(ref active) > 1) overlap = true;
            await Task.Delay(100);
            Interlocked.Decrement(ref active);
            return "ok";
        });
        await Task.WhenAll(policy.InvokeAsync(Context(tool), default).AsTask(), policy.InvokeAsync(Context(tool), default).AsTask());
        Assert.Equal(expectedOverlap, overlap);
    }

    private static ToolInvocationPolicy Create(AgentExecutionOptions options) => new(
        options, [], Mock.Of<IFileAssetService>(), new FileAssetExecutionContext(), NullLogger<IsolatedToolFunction>.Instance);

    private static FunctionInvocationContext Context(AIFunction function) => new() { Function = function, Arguments = new AIFunctionArguments() };

    private sealed class ProbeFunction(ToolConcurrency concurrency, Func<Task<object?>> action) : AIFunction, IToolConcurrencyProvider
    {
        public ToolConcurrency Concurrency => concurrency;
        public override string Name => "probe";
        public override string Description => "Probe concurrency.";
        public override JsonElement JsonSchema => JsonSerializer.SerializeToElement(new { type = "object" });
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) => await action();
    }
}
