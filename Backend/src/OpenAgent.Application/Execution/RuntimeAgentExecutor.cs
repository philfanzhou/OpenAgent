using System.Runtime.CompilerServices;
using System.Text;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using OpenAgent.Application.Execution.Admission;
using OpenAgent.Application.Conversation;
using OpenAgent.Application.Observability;
using OpenAgent.Capabilities.Files.Requests;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Application.Execution;

/// <summary>平台执行协调：身份/模型授权、文件、历史与事件持久化不下放给外部 Agent。</summary>
public sealed class RuntimeAgentExecutor
{
    private readonly IAgentRuntimeResolver _profiles;
    private readonly IAgentRuntimeDriver _driver;
    private readonly ConversationAgentResolver _conversations;
    private readonly FileAssetRequestResolver _files;
    private readonly RuntimePreparation _preparation;
    private readonly TurnAdmission _admission;
    private readonly AdmissionOptions _limits;
    private readonly IRuntimeProfileRepository? _runtimes;

    internal RuntimeAgentExecutor(
        IAgentRuntimeResolver profiles,
        IAgentRuntimeDriver driver,
        ConversationAgentResolver conversations,
        FileAssetRequestResolver files,
        RuntimePreparation preparation,
        TurnAdmission admission,
        IOptions<AdmissionOptions> limits,
        IRuntimeProfileRepository? runtimes = null)
    {
        _profiles = profiles;
        _driver = driver;
        _conversations = conversations;
        _files = files;
        _preparation = preparation;
        _admission = admission;
        _limits = limits.Value;
        _runtimes = runtimes;
    }

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
        string agentId = await _conversations.ResolveAsync(request, user, cancellationToken).ConfigureAwait(false) ?? "default";
        TurnContext turn = new()
        {
            TenantId = user.TenantId ?? string.Empty,
            UserId = user.UserId,
            AgentId = agentId,
            ConversationId = request.ConversationId ?? Guid.NewGuid().ToString("N"),
            ConversationType = request.ConversationType,
            TraceId = string.IsNullOrWhiteSpace(request.TraceId) ? Guid.NewGuid().ToString("N") : request.TraceId
        };
        AgentRuntimeProfile profile = await _profiles.ResolveAsync(agentId, request.LlmProfileId ?? string.Empty, user, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(request.RuntimeId))
        {
            RuntimeProfile? runtime = _runtimes is null ? null
                : await _runtimes.GetAsync(turn.TenantId, request.RuntimeId, cancellationToken).ConfigureAwait(false);
            if (runtime is null || runtime.Provider != "OpenCode" || runtime.TurnTimeoutSeconds is < 1 or > 86400)
            {
                throw new AgentException(AgentErrorCode.InvalidRequest, "The selected runtime is unavailable.");
            }
            profile.Runtime = runtime;
        }
        using AdmissionTicket ticket = _admission.Enter(turn.TenantId, cancellationToken);
        if (ticket.WasQueued)
        {
            yield return new AgentStreamEvent { Type = AgentStreamEventType.ExecutionStatus, Status = "queued" };
        }
        await ticket.Ready.Task.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Min(_limits.ExecutionTimeoutSeconds,
            profile.Runtime?.TurnTimeoutSeconds ?? _limits.ExecutionTimeoutSeconds)));
        yield return new AgentStreamEvent
        {
            Type = AgentStreamEventType.ExecutionStatus, Status = "started",
            QueueMilliseconds = (long)Stopwatch.GetElapsedTime(ticket.EnqueuedAt, ticket.StartedAt).TotalMilliseconds
        };
        Exception Normalize(Exception error) => error is OperationCanceledException && deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested
            ? new AgentException(AgentErrorCode.RuntimeExecutionTimeout, "Runtime execution exceeded its time limit.") : error;
        await using IAsyncEnumerator<AgentStreamEvent> admitted = ExecuteAdmittedAsync(request, user, profile, turn, measurement, Normalize, deadline.Token).GetAsyncEnumerator();
        while (true)
        {
            bool available;
            try
            {
                available = await admitted.MoveNextAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new AgentException(AgentErrorCode.RuntimeExecutionTimeout, "Runtime execution exceeded its time limit.");
            }
            if (!available) break;
            yield return admitted.Current;
        }
    }

    private async IAsyncEnumerable<AgentStreamEvent> ExecuteAdmittedAsync(
        AgentRequest request, IAgentUserContext user, AgentRuntimeProfile profile, TurnContext turn,
        EngineMeter.EngineExecutionMeasurement measurement, Func<Exception, Exception> normalizeFailure,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AgentRequest resolved = new()
        {
            Query = request.Query,
            AgentId = turn.AgentId,
            LlmProfileId = request.LlmProfileId,
            RuntimeId = request.RuntimeId,
            ConversationId = turn.ConversationId,
            ConversationType = request.ConversationType,
            TraceId = turn.TraceId,
            FileIds = request.FileIds
        };
        if (resolved.FileIds.Count > 0)
        {
            await _preparation.EnsureConversationAsync(turn, request.Query, cancellationToken).ConfigureAwait(false);
        }
        ResolvedFileRequest attachments = await _files.ResolveAsync(resolved, user, cancellationToken).ConfigureAwait(false);
        await using RuntimePreparationScope scope = await _preparation.CreateAsync(profile, turn, user, request.Query, attachments.Files, normalizeFailure, cancellationToken).ConfigureAwait(false);
        IAsyncEnumerator<AgentStreamEvent> events = _driver.RunAsync(scope.InvocationRequest, cancellationToken).GetAsyncEnumerator();
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
                    failure = normalizeFailure(error);
                    if (!ReferenceEquals(failure, error)) throw failure;
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
                failure = normalizeFailure(error);
                if (!ReferenceEquals(failure, error)) throw failure;
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
