using System.Text;
using OpenAgent.Contracts.Capabilities;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Conversation;
using OpenAgent.Core.Files;

using FileAssetScope = OpenAgent.Contracts.Files.FileAssetScope;

namespace OpenAgent.Core.Capabilities.Context;

/// <summary>
/// 每轮执行的有效模型上下文信息（由 AgentFactory 在构建代理时写入）。
/// </summary>
internal sealed class RunModelContext
{
    /// <summary>本轮模型的上下文窗口（tokens）；0 表示未配置（工具返回不可用）。</summary>
    internal int ContextTokens { get; set; }
}

/// <summary>
/// get_context_remaining：向模型暴露上下文余量的近似值（对标 Codex get_context_remaining）。
/// 用量口径：已完成消息的持久化 TokenUsage 求和 + 无用量消息的 UTF8 字节/4 估算；
/// 不含当前在途轮次的输出，属于保守下限。模型据此决定是否收敛、分块或要求压缩。
/// </summary>
internal sealed class ContextCapabilitySource(
    IConversationStore store,
    FileAssetExecutionContext context,
    RunModelContext modelContext) : ICapabilitySource
{
    private const string Name = "get_context_remaining";
    private const string Description =
        "Get the approximate remaining capacity of the model's context window for this conversation. "
        + "Returns the configured window size, the approximate used tokens (sum of completed turns' usage "
        + "plus a chars/4 estimate for messages without recorded usage) and the remaining tokens. "
        + "The estimate excludes the current in-flight turn, so treat it as an upper bound on what is left. "
        + "Check it before large tool outputs or long documents, and prefer narrow reads when remaining is low.";
    private const string Schema =
        """{"type":"object","properties":{},"additionalProperties":false}""";

    public Task<IReadOnlyList<CapabilityDefinition>> DiscoverAsync(
        string agentId,
        AgentConfig config,
        IAgentUserContext user,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<CapabilityDefinition> definitions =
        [
            new CapabilityDefinition(
                Name,
                Description,
                Schema,
                AgentResourceType.Tool,
                Name,
                RemainingAsync,
                Concurrency: ToolConcurrency.ReadOnly)
        ];
        return Task.FromResult(definitions);
    }

    private async Task<ToolResult> RemainingAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        if (modelContext.ContextTokens <= 0)
        {
            return ToolResult.Text(System.Text.Json.JsonSerializer.Serialize(new
            {
                available = false,
                reason = "The context window is not configured for this model."
            }));
        }
        FileAssetScope? scope = context.Scope;
        if (scope == null || string.IsNullOrWhiteSpace(scope.ConversationId))
        {
            return ToolResult.Text(System.Text.Json.JsonSerializer.Serialize(new
            {
                available = false,
                reason = "No conversation is bound to this request."
            }));
        }

        ConversationRecord? record = await store.GetRecordAsync(
            scope.TenantId ?? string.Empty,
            scope.ConversationId).ConfigureAwait(false);
        long used = 0;
        int countedMessages = 0;
        if (record != null)
        {
            foreach (ConversationMessage message in record.Messages)
            {
                int? recorded = message.TokenUsage?.TotalTokens;
                if (recorded is > 0)
                {
                    used += recorded.Value;
                }
                else
                {
                    used += Estimate(message.Content);
                }
                countedMessages++;
            }
        }
        long remaining = Math.Max(0, modelContext.ContextTokens - used);
        return ToolResult.Text(System.Text.Json.JsonSerializer.Serialize(new
        {
            available = true,
            contextTokens = modelContext.ContextTokens,
            usedTokensApprox = used,
            remainingTokensApprox = remaining,
            messages = countedMessages,
            basis = "completed-turn usage sum + chars/4 estimate; current in-flight turn excluded"
        }));
    }

    private static int Estimate(string? content) =>
        string.IsNullOrEmpty(content) ? 0 : Encoding.UTF8.GetByteCount(content) / 4;
}
