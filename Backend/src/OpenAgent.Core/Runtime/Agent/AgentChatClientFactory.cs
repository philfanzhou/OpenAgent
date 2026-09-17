using System.ClientModel;
using System.ClientModel.Primitives;
using Anthropic;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Conversation;
using OpenAI;
using OpenAI.Responses;

namespace OpenAgent.Core.Runtime.Agent;

internal interface IAgentChatClientFactory
{
    IChatClient Create(LlmConfig llm, LlmInteractionCapture? capture = null);

    IChatClient CreateSummarizationClient(LlmConfig llm, ContextPolicy? policy, LlmInteractionCapture? capture = null);
}

internal sealed class AgentChatClientFactory : IAgentChatClientFactory
{
    private readonly TimeSpan _networkTimeout;
    private readonly bool _allowInsecureTls;
    private readonly ILlmInteractionStore _interactionStore;
    private readonly LlmInteractionOptions _interactionOptions;
    private readonly ILoggerFactory _loggerFactory;

    public AgentChatClientFactory(
        IConfiguration configuration,
        ILlmInteractionStore interactionStore,
        IOptions<LlmInteractionOptions> interactionOptions,
        ILoggerFactory loggerFactory)
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
        _allowInsecureTls = configuration.GetValue("OPENAGENT_ALLOW_INSECURE_TLS", false);
        _interactionStore = interactionStore;
        _interactionOptions = interactionOptions.Value;
        _loggerFactory = loggerFactory;
    }

    public IChatClient Create(LlmConfig llm, LlmInteractionCapture? capture = null)
    {
        IChatClient client = llm.Format switch
        {
            ApiFormat.OpenAIChatCompletions => CreateOpenAIChatCompletions(llm),
            ApiFormat.OpenAIResponses => CreateOpenAIResponses(llm),
            ApiFormat.AnthropicMessages => CreateAnthropic(llm),
            _ => throw new NotSupportedException($"Unsupported API format: {llm.Format}")
        };
        return WrapWithRecorder(client, llm, capture);
    }

    public IChatClient CreateSummarizationClient(
        LlmConfig llm,
        ContextPolicy? policy,
        LlmInteractionCapture? capture = null)
    {
        string? summaryModel = policy?.SummarizeOptions?.SummaryModel;
        if (string.IsNullOrWhiteSpace(summaryModel))
        {
            return Create(llm, capture);
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
        }, capture);
    }

    /// <summary>
    /// 记录器包在 provider client 最外层，位于上下文压缩与工具循环之下，
    /// 捕获的是真正发往 provider 的最终 wire 请求。
    /// </summary>
    private IChatClient WrapWithRecorder(IChatClient client, LlmConfig llm, LlmInteractionCapture? capture)
    {
        if (capture == null || !_interactionOptions.Enabled)
        {
            return client;
        }

        return new LlmInteractionRecorder(
            client,
            capture,
            llm,
            _interactionStore,
            _interactionOptions,
            _loggerFactory.CreateLogger("OpenAgent.LlmInteraction"));
    }

    private IChatClient CreateOpenAIChatCompletions(LlmConfig llm)
    {
        OpenAIClient client = CreateOpenAIClient(llm, "https://api.openai.com/v1");
        return client.GetChatClient(llm.ModelId)
            .AsIChatClient()
            .AsBuilder()
            .Use(static (messages, options, next, cancellationToken) =>
                next(
                    AgentMessageAdapter.NormalizeEmptyToolArguments(
                        AgentMessageAdapter.RemoveEmptyOpenAIToolCallText(messages)),
                    options,
                    cancellationToken))
            .Build();
    }

    private IChatClient CreateOpenAIResponses(LlmConfig llm)
    {
        OpenAIClient client = CreateOpenAIClient(llm, "https://api.openai.com/v1");
        return WithToolArgumentNormalization(
            client.GetResponsesClient().AsIChatClientWithStoredOutputDisabled(llm.ModelId));
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
        return WithToolArgumentNormalization(
            client.AsAIAgent(model: llm.ModelId, name: "openagent-anthropic-provider").ChatClient);
    }

    /// <summary>
    /// 出站统一把空参数工具调用规格化为 {}：null 参数会被序列化成 "null"/null，
    /// 被严格网关拒绝后模型传入空参数就会导致整轮执行终止。
    /// </summary>
    private static IChatClient WithToolArgumentNormalization(IChatClient client) =>
        client.AsBuilder()
            .Use(static (messages, options, next, cancellationToken) =>
                next(
                    AgentMessageAdapter.NormalizeEmptyToolArguments(messages),
                    options,
                    cancellationToken))
            .Build();

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
