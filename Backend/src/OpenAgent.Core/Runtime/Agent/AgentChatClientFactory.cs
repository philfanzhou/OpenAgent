using System.ClientModel;
using Anthropic;
using Microsoft.Extensions.AI;
using OpenAgent.Contracts.Configuration;
using OpenAI;
using OpenAI.Responses;

namespace OpenAgent.Core.Runtime.Agent;

internal sealed class AgentChatClientFactory
{
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

    private static IChatClient CreateOpenAIChatCompletions(LlmConfig llm)
    {
        OpenAIClient client = CreateOpenAIClient(llm, "https://api.openai.com/v1");
        return client.GetChatClient(llm.ModelId).AsIChatClient();
    }

    private static IChatClient CreateOpenAIResponses(LlmConfig llm)
    {
        OpenAIClient client = CreateOpenAIClient(llm, "https://api.openai.com/v1");
        return client.GetResponsesClient().AsIChatClientWithStoredOutputDisabled(llm.ModelId);
    }

    private static IChatClient CreateAnthropic(LlmConfig llm)
    {
        EnsureApiKey(llm, "Anthropic Messages");
        AnthropicClient client = string.IsNullOrWhiteSpace(llm.Endpoint)
            ? new AnthropicClient { ApiKey = llm.ApiKey }
            : new AnthropicClient { ApiKey = llm.ApiKey, BaseUrl = llm.Endpoint.TrimEnd('/') };
        return client.AsAIAgent(model: llm.ModelId, name: "openagent-anthropic-provider").ChatClient;
    }

    private static OpenAIClient CreateOpenAIClient(LlmConfig llm, string defaultEndpoint)
    {
        EnsureApiKey(llm, "OpenAI");
        string endpoint = string.IsNullOrWhiteSpace(llm.Endpoint) ? defaultEndpoint : llm.Endpoint;
        return new OpenAIClient(
            new ApiKeyCredential(llm.ApiKey),
            new OpenAIClientOptions { Endpoint = new Uri(endpoint) });
    }

    private static void EnsureApiKey(LlmConfig llm, string format)
    {
        if (string.IsNullOrWhiteSpace(llm.ApiKey))
        {
            throw new ArgumentException($"API key is required for {format}.");
        }
    }
}
