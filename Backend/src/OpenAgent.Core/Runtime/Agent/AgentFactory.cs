using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
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
    private readonly ILoggerFactory _loggerFactory;

    public AgentFactory(
        IAgentChatClientFactory chatClients,
        ConversationHistoryFactory conversations,
        CapabilityToolFactory capabilities,
        McpToolFactory mcpTools,
        AgentSkillsProviderFactory skills,
        FileAssetExecutionContext files,
        ILoggerFactory loggerFactory)
    {
        _chatClients = chatClients;
        _conversations = conversations;
        _capabilities = capabilities;
        _mcpTools = mcpTools;
        _skills = skills;
        _files = files;
        _loggerFactory = loggerFactory;
    }

    internal async Task<AgentExecutionScope> CreateAsync(
        AgentRuntimeProfile profile,
        AgentRequest request,
        IAgentUserContext user,
        IReadOnlyList<FileAssetContent> files,
        CancellationToken cancellationToken) =>
        await CreateCoreAsync(
            profile,
            request,
            user,
            files,
            recordUserInput: true,
            allowAwaitingApproval: false,
            cancellationToken).ConfigureAwait(false);

    internal async Task<AgentExecutionScope> CreateForResumeAsync(
        AgentRuntimeProfile profile,
        AgentRequest request,
        IAgentUserContext user,
        CancellationToken cancellationToken) =>
        await CreateCoreAsync(
            profile,
            request,
            user,
            [],
            recordUserInput: false,
            allowAwaitingApproval: true,
            cancellationToken).ConfigureAwait(false);

    private async Task<AgentExecutionScope> CreateCoreAsync(
        AgentRuntimeProfile profile,
        AgentRequest request,
        IAgentUserContext user,
        IReadOnlyList<FileAssetContent> files,
        bool recordUserInput,
        bool allowAwaitingApproval,
        CancellationToken cancellationToken)
    {
        IChatClient modelClient = _chatClients.Create(profile.Model);
        IChatClient summarizationClient = _chatClients.CreateSummarizationClient(
            profile.Model,
            profile.Config.ContextPolicy);
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
            recordUserInput,
            allowAwaitingApproval);
        CapabilityToolRuntime capabilityRuntime = await _capabilities.CreateRuntimeAsync(
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
                profile.Config.ContextPolicy,
                summarizationClient,
                user.TenantId,
                request.ConversationId);
            IChatClient compactingClient = modelClient
                .AsBuilder()
                .UseAIContextProviders(compaction)
                .Build();
            ChatClientBuilder chatClientBuilder = new(compactingClient);
            Dictionary<string, ApprovalTarget> approvalTargets = new(StringComparer.Ordinal);
            foreach ((string name, ApprovalTarget target) in capabilityRuntime.ApprovalTargets)
            {
                approvalTargets.Add(name, target);
            }
            foreach ((string name, ApprovalTarget target) in mcpRuntime.ApprovalTargets)
            {
                approvalTargets.Add(name, target);
            }
            bool requiresApproval = approvalTargets.Count > 0 || skillsRuntime.RequiresApproval;
            if (requiresApproval)
            {
                // Builder registrations are outer-to-inner. Binding must observe
                // the model-originated request before function invocation handles it.
                chatClientBuilder.UseApprovalResponseBinding(_loggerFactory);
                chatClientBuilder.UseApprovalNotRequiredFunctionBypassing(_loggerFactory);
            }
            chatClientBuilder.Use(innerClient => new FunctionInvokingChatClient(innerClient)
            {
                AllowConcurrentInvocation = false,
                IncludeDetailedErrors = false,
                MaximumConsecutiveErrorsPerRequest = 3,
                MaximumIterationsPerRequest = profile.Config.MaxTurns > 0 ? profile.Config.MaxTurns : 5,
                TerminateOnUnknownCalls = true
            });
            IChatClient chatClient = chatClientBuilder.Build();

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
                    Tools = capabilityRuntime.Tools.Concat(mcpRuntime.Tools).ToList()
                },
                ChatHistoryProvider = history,
                AIContextProviders = providers,
                UseProvidedChatClientAsIs = true,
                RequirePerServiceCallChatHistoryPersistence = false
            });
            if (requiresApproval)
            {
                agent = new ToolApprovalAgent(agent, new ToolApprovalAgentOptions
                {
                    AutoApprovalRules = skillsRuntime.RequiresApproval
                        ? [skillsRuntime.AutoApprovalRule]
                        : null
                });
            }
            return new AgentExecutionScope(
                agent,
                history,
                new ApprovalTargetResolver(
                    approvalTargets,
                    skillsRuntime.HighRiskNames),
                mcpRuntime,
                skillsRuntime);
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
