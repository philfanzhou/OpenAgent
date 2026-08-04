using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAgent.Core.Capabilities;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Conversation;

namespace OpenAgent.Core.Runtime.Agent;

internal sealed class AgentFactory
{
    private readonly AgentChatClientFactory _chatClients;
    private readonly ConversationHistoryFactory _conversations;
    private readonly CapabilityToolFactory _capabilities;

    public AgentFactory(
        AgentChatClientFactory chatClients,
        ConversationHistoryFactory conversations,
        CapabilityToolFactory capabilities)
    {
        _chatClients = chatClients;
        _conversations = conversations;
        _capabilities = capabilities;
    }

    internal async Task<AgentLease> CreateAsync(
        string agentId,
        AgentConfig config,
        LlmConfig model,
        AgentRequest request,
        IAgentUserContext user,
        CancellationToken cancellationToken)
    {
        IChatClient modelClient = _chatClients.Create(model);
        PlatformChatHistory history = _conversations.Create(agentId, request, user);
        IReadOnlyList<AITool> tools = await _capabilities.CreateAsync(
            agentId,
            config,
            user,
            cancellationToken).ConfigureAwait(false);

        IChatClient chatClient = new FunctionInvokingChatClient(modelClient)
        {
            AllowConcurrentInvocation = false,
            IncludeDetailedErrors = false,
            MaximumConsecutiveErrorsPerRequest = 0,
            MaximumIterationsPerRequest = config.MaxTurns > 0 ? config.MaxTurns : 5,
            TerminateOnUnknownCalls = true
        };

        List<AIContextProvider> providers = [];
        AIContextProvider? compaction = _conversations.CreateCompaction(
            request.ContextPolicy,
            modelClient);
        if (compaction != null)
        {
            providers.Add(compaction);
        }

        AIAgent agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Id = agentId,
            Name = agentId,
            ChatOptions = new ChatOptions
            {
                Temperature = (float?)model.Temperature,
                Tools = tools.ToList()
            },
            ChatHistoryProvider = history,
            AIContextProviders = providers,
            UseProvidedChatClientAsIs = true,
            RequirePerServiceCallChatHistoryPersistence = false
        });
        return new AgentLease(agent, history);
    }
}
