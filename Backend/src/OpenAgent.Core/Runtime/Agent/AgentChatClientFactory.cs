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
        IChatClient provider = llm.Format switch
        {
            ApiFormat.OpenAIChatCompletions => CreateOpenAIChatCompletions(llm),
            ApiFormat.OpenAIResponses => CreateOpenAIResponses(llm),
            ApiFormat.AnthropicMessages => CreateAnthropic(llm),
            _ => throw new NotSupportedException($"Unsupported API format: {llm.Format}")
        };
        // 记录器紧贴 provider、出站规格化包在记录器外层：
        // 交互日志捕获的就是规格化后真正发往 provider 的最终消息。
        // MAF's OpenTelemetryAgent auto-wires below-FICC chat telemetry and
        // propagates its privacy setting, keeping Agent and model spans linked.
        return NormalizeOutbound(WrapWithRecorder(provider, llm, capture));
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
    /// 出站规格化层：空工具结果、空 assistant 文本、null 工具参数统一在此处理，
    /// 全部 API 格式共用。它位于交互记录器外层，使日志与 wire 请求一致。
    /// </summary>
    private static IChatClient NormalizeOutbound(IChatClient client) =>
        client.AsBuilder()
            .Use(static (messages, options, next, cancellationToken) =>
                next(
                    AgentMessageAdapter.NormalizeOutbound(messages),
                    options,
                    cancellationToken))
            .Build();

    /// <summary>
    /// 记录器紧贴 provider，位于出站规格化与上下文压缩、工具循环之下，
    /// 捕获规格化后真正发往大模型的最终 wire 请求。
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
        return client.GetChatClient(llm.ModelId).AsIChatClient();
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
