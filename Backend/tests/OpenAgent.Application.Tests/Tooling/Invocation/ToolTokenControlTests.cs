using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAgent.Contracts.Capabilities;
using OpenAgent.Application.Tooling.Discovery;
using Xunit;

namespace OpenAgent.Application.Tests.Runtime;

public class ToolTokenControlTests
{
    // 延迟加载（DeferredToolCatalog/search_tools/DeferredToolInjector）随平台侧
    // MCP 工具代理一并移除：外部 server 经 ACP 透传后由 harness 自行管理工具目录。

    private sealed class CapturingChatClient : IChatClient
    {
        public ChatOptions? LastOptions { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            LastOptions = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => this;

        public void Dispose() { }
    }

    private static async Task<string> InvokeAsync(AIFunction function, IReadOnlyDictionary<string, object?> arguments)
    {
        object? result = await function.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>(arguments)),
            CancellationToken.None);
        return Assert.IsType<string>(result);
    }

    private static AITool Stub(string name, string description) => new TaggedTool(name, description);

    private sealed class TaggedTool(string name, string description = "") : AIFunction
    {
        public override string Name => name;
        public override string Description => description;
        public override JsonElement JsonSchema =>
            JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone();

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<object?>("ok");
    }
}
