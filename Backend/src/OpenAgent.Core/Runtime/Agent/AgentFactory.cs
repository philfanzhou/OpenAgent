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
    private readonly IFileAssetService _fileAssets;
    private readonly IServiceProvider _services;
    private readonly ILogger<IsolatedToolFunction> _toolLogger;
    private readonly ILogger<AgentFactory> _logger;
    private readonly AgentExecutionOptions _executionOptions;
    private readonly McpExecutionOptions _mcpOptions;

    public AgentFactory(
        IAgentChatClientFactory chatClients,
        ConversationHistoryFactory conversations,
        CapabilityToolFactory capabilities,
        McpToolFactory mcpTools,
        AgentSkillsProviderFactory skills,
        FileAssetExecutionContext files,
        IFileAssetService fileAssets,
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
        _fileAssets = fileAssets;
        _services = services;
        _toolLogger = toolLogger;
        _logger = logger;
        _executionOptions = executionOptions.Value;
        _mcpOptions = mcpOptions.Value;
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
        ToolInvocationPolicy? toolPolicy = null;
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
            toolPolicy = new ToolInvocationPolicy(
                _executionOptions, mcpRuntime.Tools, _fileAssets, _files, _toolLogger);

            // MCP 工具延迟加载：超过阈值时不整体注入（省每轮上下文），模型经
            // search_tools 检索并激活。阈值 ≤0 时保持全量内联。
            List<AITool> chatTools = [.. tools, .. mcpRuntime.Tools];
            DeferredToolCatalog? deferredCatalog = null;
            if (_mcpOptions.DeferredToolThreshold > 0
                && mcpRuntime.Tools.Count > _mcpOptions.DeferredToolThreshold)
            {
                deferredCatalog = new DeferredToolCatalog(mcpRuntime.Tools);
                chatTools = [.. tools, new ToolSearchFunction(deferredCatalog)];
                _logger.LogInformation(
                    "MCP tools deferred: {Deferred} tools hidden behind search_tools (threshold {Threshold})",
                    mcpRuntime.Tools.Count,
                    _mcpOptions.DeferredToolThreshold);
            }
            // read_mcp_resource 桥接工具常驻内联：必须在延迟分支重建 chatTools
            // 之后追加——延迟模式下它是模型取回 resource URI 内容的唯一途径。
            if (mcpRuntime.ResourceReader is { } resourceReader)
            {
                chatTools.Add(resourceReader);
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
            // 外层包装只覆盖第一轮请求。注入器逐轮把激活工具合并进 options，FICC 的函数解析与
            // provider 序列化同轮可见。
            IChatClient innerClient = deferredCatalog != null
                ? new DeferredToolInjector(compactingClient, deferredCatalog)
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
                // （写入/执行/MCP/Skill）由 ToolInvocationPolicy 内每轮共享的信号量
                // 串行化，行为与旧版一致。
                AllowConcurrentInvocation = true,
                FunctionInvoker = toolPolicy.InvokeAsync,
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
                    // All functions, including provider-added Skill tools, pass
                    // through FunctionInvoker at invocation time.
                    Tools = chatTools
                },
                ChatHistoryProvider = history,
                AIContextProviders = providers,
                UseProvidedChatClientAsIs = true,
                RequirePerServiceCallChatHistoryPersistence = false
            }).AsBuilder()
                .UseOpenTelemetry("OpenAgent.AgentFramework", telemetry => telemetry.EnableSensitiveData = true)
                .Build();
            return new AgentExecutionScope(agent, history, mcpRuntime, skillsRuntime, toolPolicy);
        }
        catch
        {
            if (toolPolicy != null)
            {
                await toolPolicy.DisposeAsync().ConfigureAwait(false);
            }
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
