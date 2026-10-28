using System.ClientModel.Primitives;
using System.ClientModel;
using Anthropic;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI.Responses;
using OpenAI;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Core.Observability;

namespace OpenAgent.Core.ModelProviders;

internal interface IAgentChatClientFactory
{
    IChatClient Create(LlmConfig llm, LlmInteractionCapture? capture = null);

    IChatClient CreateSummarizationClient(LlmConfig llm, ContextPolicy? policy, LlmInteractionCapture? capture = null);
}
