using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAgent.Core.Capabilities;
using OpenAgent.Core.Capabilities.Mcp;
using OpenAgent.Core.Capabilities.Skill;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Conversation;
using OpenAgent.Core.Files;

namespace OpenAgent.Core.Runtime.Agent;

internal sealed class AgentFactory
{
    private readonly IAgentChatClientFactory _chatClients;
    private readonly ConversationHistoryFactory _conversations;
    private readonly CapabilityToolFactory _capabilities;
    private readonly McpToolFactory _mcpTools;
    private readonly AgentSkillsProviderFactory _skills;
    private readonly FileAssetExecutionContext _files;

    public AgentFactory(
        IAgentChatClientFactory chatClients,
        ConversationHistoryFactory conversations,
        CapabilityToolFactory capabilities,
        McpToolFactory mcpTools,
        AgentSkillsProviderFactory skills,
        FileAssetExecutionContext files)
    {
        _chatClients = chatClients;
        _conversations = conversations;
        _capabilities = capabilities;
        _mcpTools = mcpTools;
        _skills = skills;
        _files = files;
    }

    internal async Task<AgentExecutionScope> CreateAsync(
        AgentRuntimeProfile profile,
        AgentRequest request,
        IAgentUserContext user,
        IReadOnlyList<FileAsset> files,
        CancellationToken cancellationToken)
    {
        IChatClient modelClient = _chatClients.Create(profile.Model);
        IChatClient summarizationClient = _chatClients.CreateSummarizationClient(
            profile.Model,
            profile.ContextPolicy);
        _files.Set(new OpenAgent.Contracts.Files.FileAssetScope
        {
            TenantId = user.TenantId ?? string.Empty,
            UserId = user.UserId,
            ConversationId = request.ConversationId
        });
        PlatformChatHistory history = _conversations.Create(
            profile.AgentId,
            profile.Model.ModelId,
            request,
            user,
            files,
            profile.Model.Modality == ModelModality.Multimodal);
        IReadOnlyList<AITool> tools = await _capabilities.CreateAsync(
            profile.AgentId,
            profile.Config,
            user,
            cancellationToken).ConfigureAwait(false);
        McpToolRuntime mcpRuntime = McpToolRuntime.Empty;
        AgentSkillsRuntime skillsRuntime = AgentSkillsRuntime.Empty;
        try
        {
            mcpRuntime = await _mcpTools.CreateAsync(
                profile.AgentId,
                profile.Config.Mcp,
                user,
                cancellationToken).ConfigureAwait(false);
            skillsRuntime = await _skills.CreateAsync(
                profile.AgentId,
                profile.Config.Skills,
                user,
                cancellationToken).ConfigureAwait(false);

            AIContextProvider compaction = _conversations.CreateCompaction(
                profile.ContextPolicy,
                summarizationClient,
                user.TenantId,
                request.ConversationId);
            IChatClient compactingClient = modelClient
                .AsBuilder()
                .UseAIContextProviders(compaction)
                .Build();
            IChatClient chatClient = new FunctionInvokingChatClient(compactingClient)
            {
                AllowConcurrentInvocation = false,
                IncludeDetailedErrors = false,
                MaximumConsecutiveErrorsPerRequest = 3,
                MaximumIterationsPerRequest = profile.Config.MaxTurns > 0 ? profile.Config.MaxTurns : 5,
                TerminateOnUnknownCalls = true
            };

            List<AIContextProvider> providers = [];
            if (skillsRuntime.Provider != null)
            {
                providers.Add(skillsRuntime.Provider);
            }
            AIAgent agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
            {
                Id = profile.AgentId,
                Name = profile.AgentId,
                ChatOptions = new ChatOptions
                {
                    Instructions = string.IsNullOrWhiteSpace(profile.Config.Instructions)
                        ? null
                        : profile.Config.Instructions,
                    Temperature = (float?)profile.Model.Temperature,
                    Tools = tools.Concat(mcpRuntime.Tools).ToList()
                },
                ChatHistoryProvider = history,
                AIContextProviders = providers,
                UseProvidedChatClientAsIs = true,
                RequirePerServiceCallChatHistoryPersistence = false
            });
            return new AgentExecutionScope(agent, history, mcpRuntime, skillsRuntime);
        }
        catch
        {
            await mcpRuntime.DisposeAsync().ConfigureAwait(false);
            await skillsRuntime.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal Task EnsureConversationAsync(
        string agentId,
        AgentRequest request,
        IAgentUserContext user,
        CancellationToken cancellationToken) =>
        _conversations.EnsureConversationAsync(agentId, request, user, cancellationToken);
}
