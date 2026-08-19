using System.ClientModel;
using Anthropic;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OpenAgent.Contracts.Configuration;
using OpenAI;
using OpenAI.Responses;

namespace OpenAgent.Core.Runtime.Agent;

internal sealed class AgentChatClientFactory
{
    private readonly TimeSpan _networkTimeout;

    public AgentChatClientFactory(IConfiguration configuration)
    {
        // OpenAI SDK 默认网络读超时为 100s，对推理模型流式输出（两次数据之间可能停顿更久）太短，
        // 会触发 ReadTimeoutStream 在会话中途掐断。默认放宽到 15 分钟；
        // 配置 Llm:NetworkTimeoutSeconds=0 表示不限时。
        int seconds = configuration.GetValue("Llm:NetworkTimeoutSeconds", 900);
        _networkTimeout = seconds <= 0
            ? Timeout.InfiniteTimeSpan
            : TimeSpan.FromSeconds(seconds);
    }

    internal IChatClient Create(LlmConfig llm)
    {
        return llm.Format switch
        {
            ApiFormat.OpenAIChatCompletions => CreateOpenAIChatCompletions(llm),
            ApiFormat.OpenAIResponses => CreateOpenAIResponses(llm),
            ApiFormat.AnthropicMessages => CreateAnthropic(llm),
            _ => throw new NotSupportedException($"Unsupported API format: {llm.Format}")
        };
    }

    private IChatClient CreateOpenAIChatCompletions(LlmConfig llm)
    {
        OpenAIClient client = CreateOpenAIClient(llm, "https://api.openai.com/v1");
        return client.GetChatClient(llm.ModelId)
            .AsIChatClient()
            .AsBuilder()
            .Use(static (messages, options, next, cancellationToken) =>
                next(
                    AgentMessageAdapter.RemoveEmptyOpenAIToolCallText(messages),
                    options,
                    cancellationToken))
            .Build();
    }

    private IChatClient CreateOpenAIResponses(LlmConfig llm)
    {
        OpenAIClient client = CreateOpenAIClient(llm, "https://api.openai.com/v1");
        return client.GetResponsesClient().AsIChatClientWithStoredOutputDisabled(llm.ModelId);
    }

    private IChatClient CreateAnthropic(LlmConfig llm)
    {
        EnsureApiKey(llm, "Anthropic Messages");
        AnthropicClient client = string.IsNullOrWhiteSpace(llm.Endpoint)
            ? new AnthropicClient { ApiKey = llm.ApiKey }
            : new AnthropicClient { ApiKey = llm.ApiKey, BaseUrl = llm.Endpoint.TrimEnd('/') };
        return client.AsAIAgent(model: llm.ModelId, name: "openagent-anthropic-provider").ChatClient;
    }

    private OpenAIClient CreateOpenAIClient(LlmConfig llm, string defaultEndpoint)
    {
        EnsureApiKey(llm, "OpenAI");
        string endpoint = string.IsNullOrWhiteSpace(llm.Endpoint) ? defaultEndpoint : llm.Endpoint;
        return new OpenAIClient(
            new ApiKeyCredential(llm.ApiKey),
            new OpenAIClientOptions
            {
                Endpoint = new Uri(endpoint),
                NetworkTimeout = _networkTimeout
            });
    }

    private static void EnsureApiKey(LlmConfig llm, string format)
    {
        if (string.IsNullOrWhiteSpace(llm.ApiKey))
        {
            throw new ArgumentException($"API key is required for {format}.");
        }
    }
}
