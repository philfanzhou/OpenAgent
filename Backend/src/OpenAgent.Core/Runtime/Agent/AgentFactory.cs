using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAgent.Core.Capabilities;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Conversation;
using OpenAgent.Core.Files;
using OpenAgent.Contracts.Files;

namespace OpenAgent.Core.Runtime.Agent;

internal sealed class AgentFactory
{
    private readonly AgentChatClientFactory _chatClients;
    private readonly ConversationHistoryFactory _conversations;
    private readonly CapabilityToolFactory _capabilities;
    private readonly FileAssetExecutionContext _files;

    public AgentFactory(
        AgentChatClientFactory chatClients,
        ConversationHistoryFactory conversations,
        CapabilityToolFactory capabilities,
        FileAssetExecutionContext files)
    {
        _chatClients = chatClients;
        _conversations = conversations;
        _capabilities = capabilities;
        _files = files;
    }

    internal async Task<AgentExecutionScope> CreateAsync(
        AgentRuntimeProfile profile,
        AgentRequest request,
        IAgentUserContext user,
        IReadOnlyList<FileAssetContent> files,
        CancellationToken cancellationToken)
    {
        IChatClient modelClient = _chatClients.Create(profile.Model);
        _files.Set(new OpenAgent.Contracts.Files.FileAssetScope
        {
            TenantId = user.TenantId ?? string.Empty,
            UserId = user.UserId,
            ConversationId = request.ConversationId
        });
        PlatformChatHistory history = _conversations.Create(profile.AgentId, request, user, files);
        IReadOnlyList<AITool> tools = await _capabilities.CreateAsync(
            profile.AgentId,
            profile.Config,
            user,
            cancellationToken).ConfigureAwait(false);

        IChatClient chatClient = new FunctionInvokingChatClient(modelClient)
        {
            AllowConcurrentInvocation = false,
            // 工具调用失败时捕获异常回传给模型重试，而不是让整个会话失败。
            // IncludeDetailedErrors 保持 false：不把原始异常消息/内部路径泄露给模型，
            // 工具侧的校验失败统一返回净化后的错误文本（见 FileAssetCapabilitySource）。
            IncludeDetailedErrors = false,
            MaximumConsecutiveErrorsPerRequest = 3,
            MaximumIterationsPerRequest = profile.Config.MaxTurns > 0 ? profile.Config.MaxTurns : 5,
            TerminateOnUnknownCalls = true
        };

        List<AIContextProvider> providers = [];
        AIContextProvider? compaction = _conversations.CreateCompaction(
            profile.Config.ContextPolicy,
            modelClient);
        if (compaction != null)
        {
            providers.Add(compaction);
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
                Tools = tools.ToList()
            },
            ChatHistoryProvider = history,
            AIContextProviders = providers,
            UseProvidedChatClientAsIs = true,
            RequirePerServiceCallChatHistoryPersistence = false
        });
        return new AgentExecutionScope(agent, history);
    }
}
