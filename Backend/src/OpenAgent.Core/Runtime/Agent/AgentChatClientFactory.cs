using System.ClientModel;
using System.ClientModel.Primitives;
using Anthropic;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OpenAgent.Contracts.Configuration;
using OpenAI;
using OpenAI.Responses;
using OpenAgent.Contracts.Conversation;

namespace OpenAgent.Core.Runtime.Agent;

internal interface IAgentChatClientFactory
{
    IChatClient Create(LlmConfig llm);

    IChatClient CreateSummarizationClient(LlmConfig llm, ContextPolicy? policy);
}

internal sealed class AgentChatClientFactory : IAgentChatClientFactory
{
    private readonly TimeSpan _networkTimeout;
    private readonly bool _allowInsecureTls;

    public AgentChatClientFactory(IConfiguration configuration)
    {
        // OpenAI SDK 默认网络读超时为 100s，对推理模型流式输出（两次数据之间可能停顿更久）太短，
        // 会触发 ReadTimeoutStream 在会话中途掐断。默认放宽到 15 分钟；
        // 配置 Llm:NetworkTimeoutSeconds=0 表示不限时。
        int seconds = configuration.GetValue("Llm:NetworkTimeoutSeconds", 900);
        _networkTimeout = seconds <= 0
            ? Timeout.InfiniteTimeSpan
            : TimeSpan.FromSeconds(seconds);
        // This is a deployment-only escape hatch. It intentionally does not belong
        // to AgentConfig or persisted LLM provider profiles.
        _allowInsecureTls = configuration.GetValue("OPENAGENT_LLM_ALLOW_INSECURE_TLS", false)
            || configuration.GetValue("Llm:AllowInsecureTls", false);
    }

    public IChatClient Create(LlmConfig llm)
    {
        return llm.Format switch
        {
            ApiFormat.OpenAIChatCompletions => CreateOpenAIChatCompletions(llm),
            ApiFormat.OpenAIResponses => CreateOpenAIResponses(llm),
            ApiFormat.AnthropicMessages => CreateAnthropic(llm),
            _ => throw new NotSupportedException($"Unsupported API format: {llm.Format}")
        };
    }

    public IChatClient CreateSummarizationClient(LlmConfig llm, ContextPolicy? policy)
    {
        string? summaryModel = policy?.SummarizeOptions?.SummaryModel;
        if (string.IsNullOrWhiteSpace(summaryModel))
        {
            return Create(llm);
        }

        return Create(new LlmConfig
        {
            TenantId = llm.TenantId,
            Provider = llm.Provider,
            Format = llm.Format,
            ModelId = summaryModel,
            ApiKey = llm.ApiKey,
            Endpoint = llm.Endpoint,
            Temperature = llm.Temperature,
            ContextTokens = llm.ContextTokens
        });
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
        AnthropicClient client;
        if (_allowInsecureTls)
        {
            client = string.IsNullOrWhiteSpace(llm.Endpoint)
                ? new AnthropicClient { ApiKey = llm.ApiKey, HttpClient = CreateInsecureHttpClient() }
                : new AnthropicClient
                {
                    ApiKey = llm.ApiKey,
                    BaseUrl = llm.Endpoint.TrimEnd('/'),
                    HttpClient = CreateInsecureHttpClient()
                };
        }
        else
        {
            client = string.IsNullOrWhiteSpace(llm.Endpoint)
                ? new AnthropicClient { ApiKey = llm.ApiKey }
                : new AnthropicClient { ApiKey = llm.ApiKey, BaseUrl = llm.Endpoint.TrimEnd('/') };
        }
        return client.AsAIAgent(model: llm.ModelId, name: "openagent-anthropic-provider").ChatClient;
    }

    private OpenAIClient CreateOpenAIClient(LlmConfig llm, string defaultEndpoint)
    {
        EnsureApiKey(llm, "OpenAI");
        string endpoint = string.IsNullOrWhiteSpace(llm.Endpoint) ? defaultEndpoint : llm.Endpoint;
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(endpoint),
            NetworkTimeout = _networkTimeout
        };
        if (_allowInsecureTls)
        {
            options.Transport = new HttpClientPipelineTransport(CreateInsecureHttpClient());
        }
        return new OpenAIClient(new ApiKeyCredential(llm.ApiKey), options);
    }

    private static HttpClient CreateInsecureHttpClient()
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        return new HttpClient(handler);
    }

    private static void EnsureApiKey(LlmConfig llm, string format)
    {
        if (string.IsNullOrWhiteSpace(llm.ApiKey))
        {
            throw new ArgumentException($"API key is required for {format}.");
        }
    }
}
