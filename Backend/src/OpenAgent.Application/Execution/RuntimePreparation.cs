using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAgent.Application.Conversation.History;

using OpenAgent.Application.ModelProviders;
using OpenAgent.Application.Conversation.Compaction;
using OpenAgent.Application.Observability;
using OpenAgent.Application.Mapping;
using OpenAgent.Application.Runtime.Agent;
using OpenAgent.Application.Tooling.Abstractions;
using OpenAgent.Application.Tooling.Discovery;
using OpenAgent.Application.Tooling.Invocation;
using OpenAgent.Capabilities.Files.Requests;
using OpenAgent.Capabilities.Security;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Application.Execution;

/// <summary>复用平台工具和 Skill 的准备逻辑；推理只发生在运行时驱动，不执行任何进程内推理循环。</summary>
internal sealed class RuntimePreparation(
    ConversationHistoryFactory conversations,
    IAgentChatClientFactory summaries,
    CapabilityToolFactory capabilities,
    IEnumerable<IAgentFeatureFactory> factories,
    FileAssetExecutionContext files,
    AgentAuthorizationGate authorization,
    IServiceProvider services,
    IOptions<AgentExecutionOptions> options,
    ILogger<IsolatedToolFunction> logger)
{
    internal Task EnsureConversationAsync(TurnContext turn, string query, CancellationToken ct)
    {
        return conversations.EnsureConversationAsync(turn, query, ct);
    }

    internal async Task<RuntimePreparationScope> CreateAsync(
        AgentRuntimeProfile profile,
        TurnContext turn,
        IAgentUserContext user,
        string input,
        IReadOnlyList<FileAsset> attachments,
        CancellationToken ct)
    {
        files.Set(turn);
        PlatformChatHistory history = conversations.Create(turn, profile.Model.ModelId, input, attachments, profile.Model.Modality == ModelModality.Multimodal);
        RuntimePreparationScope scope = new(history, logger);
        try
        {
            ChatMessage message = await history.CreateUserMessageAsync(ct).ConfigureAwait(false);
            List<ChatMessage> prior = await history.LoadAsync(ct).ConfigureAwait(false);
            AgentTurnContext context = new()
            {
                Instructions = profile.Config.Instructions
            };
            context.Messages.AddRange(prior);
            context.Messages.Add(message);
            List<AITool> tools = [.. await capabilities.CreateAsync(profile.AgentId, profile.Config, user, ct).ConfigureAwait(false)];
            List<RuntimeSkill> skills = [];
            List<RuntimeMcpServer> mcpServers = [];
            foreach (IAgentFeatureFactory factory in factories)
            {
                AgentFeature feature = await factory.CreateAsync(profile.AgentId, profile.Config, user, ct).ConfigureAwait(false);
                scope.Features.Add(feature);
                tools.AddRange(feature.Tools);
                tools.AddRange(feature.InlineTools);
                skills.AddRange(feature.Skills);
                mcpServers.AddRange(feature.McpServers);
                foreach (IAgentContextProvider provider in feature.ContextProviders)
                {
                    context = await provider.InvokingAsync(context, ct).ConfigureAwait(false);
                }
            }
            if (conversations.AutoCompactionEnabled)
            {
                // 平台摘要仍走现有独立压缩服务，保留摘要预算/审计；聊天推理只走运行时驱动。
                IChatClient summarizer = summaries.CreateSummarizationClient(profile.Model, profile.Config.ContextPolicy,
                    turn.ToCapture(LlmInteractionSource.Compaction));
                scope.Clients.Add(summarizer);
                CompactionContextProvider compaction = new(conversations.CreateCompaction(profile.Model.ContextTokens,
                    profile.Config.ContextPolicy, summarizer, turn));
                context = await compaction.InvokingAsync(context, ct).ConfigureAwait(false);
                scope.Features.Add(new AgentFeature { ContextProviders = [compaction] });
            }
            tools.AddRange(context.Tools ?? []);
            List<RuntimeTool> exported = [];
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (AITool tool in tools)
            {
                AITool prepared = tool;
                foreach (AgentFeature feature in scope.Features)
                {
                    prepared = feature.PrepareTool?.Invoke(prepared) ?? prepared;
                }
                AgentExecutionOptions policy = options.Value;
                AIFunction function = (AIFunction)IsolatedToolFunction.Wrap(prepared,
                    policy.ToolCallTimeoutSeconds > 0 ? TimeSpan.FromSeconds(policy.ToolCallTimeoutSeconds) : Timeout.InfiniteTimeSpan,
                    ToolResultBudgets.Resolve(policy, tool.Name),
                    ToolConcurrencyRules.Resolve(tool), scope.Invocation.ExclusiveGate, logger);
                if (!names.Add(function.Name))
                {
                    throw new InvalidOperationException("Duplicate runtime tool name: " + function.Name);
                }
                exported.Add(new RuntimeTool(function.Name, function.Description ?? string.Empty, function.JsonSchema.GetRawText(), async (arguments, token) =>
                {
                    // 每次调用重新查授权，避免 tools/list 之后权限变化仍沿用旧授权。
                    await authorization.EnsureAuthorizedAsync(profile.AgentId, AgentResourceType.Tool, function.Name, "invoke", user, token).ConfigureAwait(false);
                    await authorization.EnsureAuthorizedAsync(profile.AgentId, AgentResourceType.Function, function.Name, "invoke", user, token).ConfigureAwait(false);
                    object? result = await function.InvokeAsync(new AIFunctionArguments(arguments.ToDictionary(item => item.Key, item => item.Value)) { Services = services }, token).ConfigureAwait(false);
                    return ToolResultText.Render(result) ?? string.Empty;
                }));
            }
            scope.Context = context;
            scope.InvocationRequest = new RuntimeInvocation(profile, turn, context.Instructions ?? string.Empty,
                context.Messages.Select(ToRuntimeMessage).ToArray(), exported, skills, mcpServers);
            return scope;
        }
        catch (Exception error)
        {
            await scope.FailAsync(error).ConfigureAwait(false);
            await scope.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>把 Conversation 侧压缩策略适配为轮次上下文提供器；分层上 Conversation 不引用 Tooling。</summary>
    private sealed class CompactionContextProvider(AuditedCompactionStrategy strategy) : IAgentContextProvider
    {
        public async Task<AgentTurnContext> InvokingAsync(AgentTurnContext context, CancellationToken cancellationToken)
        {
            IEnumerable<ChatMessage> compacted = await CompactionProvider.CompactAsync(
                strategy, context.Messages, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, cancellationToken).ConfigureAwait(false);
            context.Messages.Clear();
            context.Messages.AddRange(compacted);
            return context;
        }
    }

    private static RuntimeMessage ToRuntimeMessage(ChatMessage message)
    {
        StringBuilder text = new(message.Text);
        foreach (AIContent content in message.Contents)
        {
            if (content is FunctionCallContent call)
            {
                text.AppendLine().Append("Tool call ").Append(call.Name).Append(": ").Append(JsonSerializer.Serialize(call.Arguments));
            }
            else if (content is FunctionResultContent result)
            {
                text.AppendLine().Append("Tool result ").Append(result.CallId).Append(": ").Append(ToolResultText.Render(result.Result));
            }
        }
        RuntimeAttachment[] attachments = message.Contents.OfType<DataContent>()
            .Select(data => new RuntimeAttachment(data.MediaType, data.Data)).ToArray();
        return new RuntimeMessage(message.Role.Value, text.ToString(), attachments);
    }
}
