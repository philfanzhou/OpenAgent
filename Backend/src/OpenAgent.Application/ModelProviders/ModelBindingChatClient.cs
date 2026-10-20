using Microsoft.Extensions.AI;

namespace OpenAgent.Application.ModelProviders;

/// <summary>
/// 把构造时的 ModelId 写入每次调用的 ChatOptions。MAF 的 provider 扩展在构造期绑模，
/// 换成 M.EAI 原生适配器后由该包装补齐，避免请求落到 SDK 默认模型。
/// </summary>
internal sealed class ModelBindingChatClient(IChatClient inner, string modelId) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        inner.GetResponseAsync(messages, Bind(options), cancellationToken);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        inner.GetStreamingResponseAsync(messages, Bind(options), cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(ModelBindingChatClient) ? this : inner.GetService(serviceType, serviceKey);

    public void Dispose() => inner.Dispose();

    private ChatOptions Bind(ChatOptions? options)
    {
        options ??= new ChatOptions();
        options.ModelId ??= modelId;
        return options;
    }
}
