using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAgent.Application.Conversation.History;

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
    CapabilityToolFactory capabilities,
    IEnumerable<IAgentFeatureFactory> factories,
    FileAssetExecutionContext files,
    FileExchangeContext exchange,
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
        // 历史加载、平台工具发现与各 feature 工厂互不依赖（EF 仓储均按调用创建独立
        // DbContext），并行执行消除串行等待；结果仍按注册顺序合并，保持顺序与串行一致。
        // 任务在 try 内增量收集：工厂同步抛出时已启动的任务仍能进入统一的失败回收。
        Task<IReadOnlyList<AITool>>? discovery = null;
        List<Task<AgentFeature>> created = [];
        // 所有权唯一登记：成功产物（按对象身份）只进 scope.Features 一次；合并中途
        // 失败（provider/PrepareTool/重复工具名等）时，catch 只接管尚未登记的产物，
        // 保证每个 feature 资源恰好释放一次。
        HashSet<AgentFeature> owned = new(ReferenceEqualityComparer.Instance);
        void TakeOwnership(AgentFeature feature)
        {
            if (owned.Add(feature))
            {
                scope.Features.Add(feature);
            }
        }
        try
        {
            discovery = capabilities.CreateAsync(profile.AgentId, profile.Config, user, ct);
            foreach (IAgentFeatureFactory factory in factories)
            {
                created.Add(factory.CreateAsync(profile.AgentId, profile.Config, user, ct));
            }
            ChatMessage message = await history.CreateUserMessageAsync(ct).ConfigureAwait(false);
            List<ChatMessage> prior = await history.LoadAsync(ct).ConfigureAwait(false);
            List<AITool> tools = [.. await discovery.ConfigureAwait(false)];
            AgentFeature[] features = await Task.WhenAll(created).ConfigureAwait(false);
            AgentTurnContext context = new()
            {
                Instructions = profile.Config.Instructions
            };
            context.Messages.AddRange(prior);
            context.Messages.Add(message);
            List<RuntimeSkill> skills = [];
            List<RuntimeMcpServer> mcpServers = [];
            foreach (AgentFeature feature in features)
            {
                TakeOwnership(feature);
                tools.AddRange(feature.Tools);
                tools.AddRange(feature.InlineTools);
                skills.AddRange(feature.Skills);
                mcpServers.AddRange(feature.McpServers);
                foreach (IAgentContextProvider provider in feature.ContextProviders)
                {
                    context = await provider.InvokingAsync(context, ct).ConfigureAwait(false);
                }
            }
            // 上下文压缩由 OpenCode 原生负责（单轮内自压缩并上报 compaction_update）；
            // 平台不再做跨轮自动压缩，跨轮整理仅保留手动压缩端点。
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
                    ToolConcurrencyRules.Resolve(tool), scope.ExclusiveGate, logger);
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
                context.Messages.Select(ToRuntimeMessage).ToArray(), exported, skills, mcpServers)
            {
                // 自定义 Provider 可改写非持久化上下文，此时维持完整历史的新会话路径。
                HistoryCheckpoint = features.Any(feature => feature.ContextProviders.Count > 0) ? null : scope.HistoryCheckpoint,
                CommitAsync = scope.CommitAsync,
                PrepareFilesAsync = exchange.Enabled
                    ? (workspace, token) => exchange.PrepareAsync(workspace, attachments, token)
                    : null
            };
            return scope;
        }
        catch (Exception error)
        {
            // 并行启动的任务可能在失败点之后才完成；等待全部落地并接管尚未登记的
            // 产物（已登记的不会重复加入），由统一释放路径按逆序恰好回收一次。
            if (discovery is not null)
            {
                _ = await ObserveAsync(discovery).ConfigureAwait(false);
            }
            foreach (Task<AgentFeature> task in created)
            {
                if (await ObserveAsync(task).ConfigureAwait(false) is { } feature)
                {
                    TakeOwnership(feature);
                }
            }
            await scope.FailAsync(error).ConfigureAwait(false);
            await scope.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<T?> ObserveAsync<T>(Task<T> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            return default;
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
