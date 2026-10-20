using System.Runtime.CompilerServices;
using System.Text;
using OpenAgent.Application.Conversation;
using OpenAgent.Application.Observability;
using OpenAgent.Capabilities.Files.Requests;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Application.Execution;

/// <summary>平台执行协调：身份/模型授权、文件、历史与事件持久化不下放给外部 Agent。</summary>
internal sealed class RuntimeAgentExecutor(
    IAgentRuntimeResolver profiles,
    IAgentRuntimeDriver driver,
    ConversationAgentResolver conversations,
    FileAssetRequestResolver files,
    RuntimePreparation preparation) : IAgentExecution
{
    public async Task<AgentResponse> ExecuteAsync(AgentRequest request, IAgentUserContext user, CancellationToken cancellationToken)
    {
        StringBuilder content = new();
        AgentStreamEvent? usage = null;
        await foreach (AgentStreamEvent item in ExecuteEventsAsync(request, user, "sync", cancellationToken).ConfigureAwait(false))
        {
            if (item.Type == AgentStreamEventType.Content)
            {
                content.Append(item.Content);
            }
            if (item.Type == AgentStreamEventType.Usage)
            {
                usage = item;
            }
        }
        return new AgentResponse
        {
            Content = content.ToString(),
            TokenUsage = usage?.Usage,
            ModelId = usage?.ModelId,
            TraceId = usage?.TraceId,
            Success = true
        };
    }

    public IAsyncEnumerable<AgentStreamEvent> ExecuteStreamingAsync(AgentRequest request, IAgentUserContext user, CancellationToken cancellationToken) =>
        ExecuteEventsAsync(request, user, "stream", cancellationToken);

    private async IAsyncEnumerable<AgentStreamEvent> ExecuteEventsAsync(
        AgentRequest request,
        IAgentUserContext user,
        string mode,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using EngineMeter.EngineExecutionMeasurement measurement = EngineMeter.StartAgentCall(mode);
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            throw new AgentException(AgentErrorCode.MissingRequiredField, "Query is required.");
        }
        string agentId = await conversations.ResolveAsync(request, user, cancellationToken).ConfigureAwait(false) ?? "default";
        TurnContext turn = new()
        {
            TenantId = user.TenantId ?? string.Empty,
            UserId = user.UserId,
            AgentId = agentId,
            ConversationId = request.ConversationId ?? Guid.NewGuid().ToString("N"),
            ConversationType = request.ConversationType,
            TraceId = string.IsNullOrWhiteSpace(request.TraceId) ? Guid.NewGuid().ToString("N") : request.TraceId
        };
        AgentRuntimeProfile profile = await profiles.ResolveAsync(agentId, request.LlmProfileId ?? string.Empty, user, cancellationToken).ConfigureAwait(false);
        AgentRequest resolved = new()
        {
            Query = request.Query,
            AgentId = agentId,
            LlmProfileId = request.LlmProfileId,
            ConversationId = turn.ConversationId,
            ConversationType = request.ConversationType,
            TraceId = turn.TraceId,
            FileIds = request.FileIds,
            ExternalContext = request.ExternalContext,
            ClientType = request.ClientType,
            IdempotencyKey = request.IdempotencyKey
        };
        if (resolved.FileIds.Count > 0)
        {
            await preparation.EnsureConversationAsync(turn, request.Query, cancellationToken).ConfigureAwait(false);
        }
        ResolvedFileRequest attachments = await files.ResolveAsync(resolved, user, cancellationToken).ConfigureAwait(false);
        await using RuntimePreparationScope scope = await preparation.CreateAsync(profile, turn, user, request.Query, attachments.Files, cancellationToken).ConfigureAwait(false);
        IAsyncEnumerator<AgentStreamEvent> events = driver.RunAsync(scope.InvocationRequest, cancellationToken).GetAsyncEnumerator();
        bool finalized = false;
        Exception? failure = null;
        TokenUsage? usage = null;
        AgentStreamEvent? terminal = null;
        try
        {
            while (true)
            {
                AgentStreamEvent item;
                try
                {
                    if (!await events.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }
                    item = events.Current;
                    scope.Observe(item);
                    usage = item.Usage ?? usage;
                }
                catch (Exception error)
                {
                    failure = error;
                    throw;
                }
                if (item.Type == AgentStreamEventType.Usage)
                {
                    terminal = item;
                }
                else
                {
                    yield return item;
                    // 平台 update_plan 工具的结果同步映射为 PlanUpdated 事件，前端任务
                    // 清单不依赖解析通用工具结果；ACP 原生 plan 更新由驱动事件另行覆盖。
                    if (item is { Type: AgentStreamEventType.ToolResult, ToolName: "update_plan" } plan)
                    {
                        yield return new AgentStreamEvent { Type = AgentStreamEventType.PlanUpdated, Content = plan.Content };
                    }
                }
            }
            try
            {
                await scope.CompleteAsync(cancellationToken).ConfigureAwait(false);
                measurement.Complete(usage);
                finalized = true;
            }
            catch (Exception error)
            {
                failure = error;
                throw;
            }
            // done/usage 只能在保存成功后发送，客户端收到终态即可安全刷新会话。
            yield return terminal ?? new AgentStreamEvent
            {
                Type = AgentStreamEventType.Usage,
                ModelId = profile.Model.ModelId,
                TraceId = turn.TraceId,
                Usage = usage
            };
        }
        finally
        {
            try
            {
                // 先确认取消并回收进程/工具，随后才持久化中止状态并释放会话锁。
                // 否则下一轮可能拿到锁，而上一轮工具仍在修改同一会话的数据。
                await events.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                if (!finalized)
                {
                    await scope.FailAsync(failure ?? new OperationCanceledException("Runtime event consumer stopped.")).ConfigureAwait(false);
                }
            }
        }
    }
}
