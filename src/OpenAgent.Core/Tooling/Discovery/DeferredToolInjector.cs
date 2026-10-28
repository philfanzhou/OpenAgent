using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAgent.Contracts.Capabilities;

namespace OpenAgent.Core.Tooling.Discovery;

/// <summary>
/// 请求边界的激活工具注入器：包在 FunctionInvokingChatClient 外层，把已激活的
/// 延迟工具（经 wrap 工厂包装，超时/预算/独占信号量与内联工具一致）合并进
/// 当轮 options.Tools——既影响发往 provider 的定义序列化，也让 FICC 能解析
/// 模型对这些工具的调用。幂等：已在列表中的不重复追加。
/// </summary>
internal sealed class DeferredToolInjector(
    IChatClient inner,
    DeferredToolCatalog catalog,
    Func<AITool, AITool> wrap) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        inner.GetResponseAsync(messages, WithActivatedTools(options), cancellationToken);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        inner.GetStreamingResponseAsync(messages, WithActivatedTools(options), cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        inner.GetService(serviceType, serviceKey);

    public void Dispose() => inner.Dispose();

    private ChatOptions? WithActivatedTools(ChatOptions? options)
    {
        IReadOnlyList<string> activated = catalog.Activated;
        if (activated.Count == 0 || options == null)
        {
            return options;
        }
        List<AITool> tools = [.. (options.Tools ?? [])];
        HashSet<string> present = new(tools.Select(tool => tool.Name), StringComparer.Ordinal);
        foreach (AITool tool in catalog.ActivatedTools())
        {
            if (present.Add(tool.Name))
            {
                tools.Add(wrap(tool));
            }
        }
        options.Tools = tools;
        return options;
    }
}
