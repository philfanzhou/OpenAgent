using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAgent.Core.Capabilities;
using OpenAgent.Core.Capabilities.Mcp;
using OpenAgent.Contracts.Execution;
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
    private readonly ILogger<IsolatedToolFunction> _toolLogger;
    private readonly ILogger<AgentFactory> _logger;
    private readonly AgentExecutionOptions _executionOptions;
    private readonly McpExecutionOptions _mcpOptions;
    private readonly TimeSpan _toolCallTimeout;

    public AgentFactory(
        IAgentChatClientFactory chatClients,
        ConversationHistoryFactory conversations,
        CapabilityToolFactory capabilities,
        McpToolFactory mcpTools,
        AgentSkillsProviderFactory skills,
        FileAssetExecutionContext files,
        IServiceProvider services,
        ILogger<IsolatedToolFunction> toolLogger,
        ILogger<AgentFactory> logger,
        IOptions<AgentExecutionOptions> executionOptions,
        IOptions<McpExecutionOptions> mcpOptions)
    {
        _chatClients = chatClients;
        _conversations = conversations;
        _capabilities = capabilities;
        _mcpTools = mcpTools;
        _skills = skills;
        _files = files;
        _services = services;
        _toolLogger = toolLogger;
        _logger = logger;
        _executionOptions = executionOptions.Value;
        _mcpOptions = mcpOptions.Value;
        // 小于等于 0 视为不限时；正数作为所有工具（能力+MCP）单次调用的统一上限。
        int seconds = _executionOptions.ToolCallTimeoutSeconds;
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
            // Exclusive 工具的每轮共享信号量：与 ChatClientAgent 同生命周期，
            // 作用域释放时一并销毁。
            SemaphoreSlim exclusiveGate = new(1, 1);
            AITool WrapTool(AITool tool) => IsolatedToolFunction.Wrap(
                tool,
                _toolCallTimeout,
                ToolResultBudgets.Resolve(_executionOptions, tool.Name),
                ToolConcurrencyRules.Resolve(tool),
                exclusiveGate,
                _toolLogger);

            // MCP 工具延迟加载：超过阈值时不整体注入（省每轮上下文），模型经
            // search_tools 检索并激活。阈值 ≤0 时保持全量内联。
            List<AITool> chatTools = [.. tools, .. mcpRuntime.Tools];
            DeferredToolCatalog? deferredCatalog = null;
            if (_mcpOptions.DeferredToolThreshold > 0
                && mcpRuntime.Tools.Count > _mcpOptions.DeferredToolThreshold)
            {
                deferredCatalog = new DeferredToolCatalog(mcpRuntime.Tools);
                chatTools = [.. tools];
                chatTools.Add(new ToolSearchFunction(deferredCatalog));
                _logger.LogInformation(
                    "MCP tools deferred: {Deferred} tools hidden behind search_tools (threshold {Threshold})",
                    mcpRuntime.Tools.Count,
                    _mcpOptions.DeferredToolThreshold);
            }

            // 每轮可见工具定义体量观测：名称+描述+schema 字符数（≈4 字符/token），
            // 用于追踪"工具吃上下文"的实际水位。
            int definitionChars = chatTools.Sum(DefinitionChars);
            _logger.LogInformation(
                "Agent tool definitions: {Count} tools, {Chars} chars (~{Tokens} tokens per request)",
                chatTools.Count,
                definitionChars,
                definitionChars / 4);

            // 延迟激活注入在 FICC 内层：工具调用的迭代循环发生在 FICC 内部，
            // 外层包装只覆盖第一轮请求。注入器逐轮把激活工具（经 WrapTool，
            // 与内联工具完全一致的隔离包装）合并进 options，FICC 的函数解析与
            // provider 序列化同轮可见。
            IChatClient innerClient = deferredCatalog != null
                ? new DeferredToolInjector(compactingClient, deferredCatalog, WrapTool)
                : compactingClient;
            // MAF-registered tools (e.g. read_skill_resource) declare required
            // IServiceProvider parameters; without function invocation services the
            // call fails at argument binding with "Services are required for
            // parameter 'serviceProvider'" and surfaces as a 500.
            IChatClient chatClient = new FunctionInvokingChatClient(
                innerClient,
                functionInvocationServices: _services)
            {
                // 同一条 assistant 消息里的多个工具调用并发执行；只有能力源显式
                // 声明 ReadOnly 的工具真正并行（读取类、无可变共享状态），其余
                // （写入/执行/MCP/Skill）由 IsolatedToolFunction 内每轮共享的信号量
                // 串行化，行为与旧版一致。
                AllowConcurrentInvocation = true,
                IncludeDetailedErrors = false,
                MaximumConsecutiveErrorsPerRequest = 3,
                MaximumIterationsPerRequest = profile.Config.MaxTurns > 0
                    ? profile.Config.MaxTurns
                    : AgentConfig.DefaultMaxTurns,
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
                    // 连续失败后重抛导致整轮执行终止；结果同时过分级字符预算
                    // （头尾保留截断），防止超长输出吃穿上下文；Exclusive 工具经
                    // exclusiveGate 串行，ReadOnly 工具随 FICC 并发执行。
                    Tools = chatTools
                        .Select(tool => tool is ToolSearchFunction ? tool : WrapTool(tool))
                        .ToList()
                },
                ChatHistoryProvider = history,
                AIContextProviders = providers,
                UseProvidedChatClientAsIs = true,
                RequirePerServiceCallChatHistoryPersistence = false
            }).AsBuilder()
                .UseOpenTelemetry("OpenAgent.AgentFramework", telemetry => telemetry.EnableSensitiveData = false)
                .Build();
            return new AgentExecutionScope(agent, history, mcpRuntime, skillsRuntime);
        }
        catch
        {
            await mcpRuntime.DisposeAsync().ConfigureAwait(false);
            await skillsRuntime.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>工具定义体量：名称 + 描述 + schema 原文的字符数（近似 token 成本）。</summary>
    private static int DefinitionChars(AITool tool) =>
        tool.Name.Length
        + (tool.Description?.Length ?? 0)
        + (tool is AIFunction function ? function.JsonSchema.GetRawText().Length : 0);

    internal Task EnsureConversationAsync(
        TurnContext turn,
        string input,
        CancellationToken cancellationToken) =>
        _conversations.EnsureConversationAsync(turn, input, cancellationToken);
}
