using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAgent.Core.Capabilities;
using OpenAgent.Core.Capabilities.Mcp;
using OpenAgent.Core.Capabilities.Skill;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Runtime;
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
    private readonly IServiceProvider _services;
    private readonly TimeSpan _toolCallTimeout;

    public AgentFactory(
        IAgentChatClientFactory chatClients,
        ConversationHistoryFactory conversations,
        CapabilityToolFactory capabilities,
        McpToolFactory mcpTools,
        AgentSkillsProviderFactory skills,
        FileAssetExecutionContext files,
        IServiceProvider services,
        IOptions<AgentExecutionOptions> executionOptions)
    {
        _chatClients = chatClients;
        _conversations = conversations;
        _capabilities = capabilities;
        _mcpTools = mcpTools;
        _skills = skills;
        _files = files;
        _services = services;
        // 小于等于 0 视为不限时；正数作为所有工具（能力+MCP）单次调用的统一上限。
        int seconds = executionOptions.Value.ToolCallTimeoutSeconds;
        _toolCallTimeout = seconds <= 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// 轮次身份坐标（租户/用户/会话/TraceId/AgentId）统一由 TurnContext 携带，
    /// 此处只负责投影；轮内剩余输入（query/文件）仍以参数显式传递。
    /// </summary>
    internal async Task<AgentExecutionScope> CreateAsync(
        AgentRuntimeProfile profile,
        TurnContext turn,
        IAgentUserContext user,
        string input,
        IReadOnlyList<FileAsset> files,
        CancellationToken cancellationToken)
    {
        IChatClient modelClient = _chatClients.Create(
            profile.Model,
            turn.ToCapture(LlmInteractionSource.AgentTurn));
        _files.Set(turn);
        PlatformChatHistory history = _conversations.Create(
            turn,
            profile.Model.ModelId,
            input,
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
                profile.Config,
                user,
                cancellationToken).ConfigureAwait(false);

            // 自动压缩暂时禁用（ConversationStore:EnableAutoCompaction，默认 false）：
            // 压缩可能把当前轮 user query 一并摘要，Qwen 系服务端模板会拒绝无 user 消息的请求。
            IChatClient compactingClient = modelClient;
            if (_conversations.AutoCompactionEnabled)
            {
                IChatClient summarizationClient = _chatClients.CreateSummarizationClient(
                    profile.Model,
                    profile.Config.ContextPolicy,
                    turn.ToCapture(LlmInteractionSource.Compaction));
                AIContextProvider compaction = _conversations.CreateCompaction(
                    profile.Model.ContextTokens,
                    profile.Config.ContextPolicy,
                    summarizationClient,
                    turn);
                compactingClient = modelClient
                    .AsBuilder()
                    .UseAIContextProviders(compaction)
                    .Build();
            }
            // MAF-registered tools (e.g. read_skill_resource) declare required
            // IServiceProvider parameters; without function invocation services the
            // call fails at argument binding with "Services are required for
            // parameter 'serviceProvider'" and surfaces as a 500.
            IChatClient chatClient = new FunctionInvokingChatClient(
                compactingClient,
                functionInvocationServices: _services)
            {
                AllowConcurrentInvocation = false,
                IncludeDetailedErrors = false,
                MaximumConsecutiveErrorsPerRequest = 3,
                MaximumIterationsPerRequest = profile.Config.MaxTurns > 0 ? profile.Config.MaxTurns : 5,
                // 未知工具调用（如某次运行中 MCP server 连接失败、工具未注册，或模型
                // 幻觉出名字）必须以 "tool not found" 结果回传给模型，让它自行调整；
                // 终止循环会让模型在没有任何回复的情况下直接停止。
                TerminateOnUnknownCalls = false
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
                    // 工具（MCP/能力）异常与超时在调用处被隔离成错误结果回传给模型
                    // （超时带 timedOut 标记），避免 FunctionInvokingChatClient
                    // 连续失败后重抛导致整轮执行终止。
                    Tools = tools.Concat(mcpRuntime.Tools)
                        .Select(tool => IsolatedToolFunction.Wrap(tool, _toolCallTimeout))
                        .ToList()
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
        TurnContext turn,
        string input,
        CancellationToken cancellationToken) =>
        _conversations.EnsureConversationAsync(turn, input, cancellationToken);
}
