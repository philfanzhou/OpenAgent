using System.Collections.Concurrent;
using System.Diagnostics;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Contracts.Runtime;

namespace OpenAgent.Application.Observability;

/// <summary>
/// 把轮次身份写入当前 Activity，供 OTLP 导出携带到审计平面。
/// </summary>
/// <remarks>
/// 审计网关收到 trace 后要回答两个问题：这段对话属于谁、属于哪个会话。
/// trace 的 span 上如果没有这些标签，网关只能按时间把它们堆在一起，
/// 无法还原成一次可检索的会话。
///
/// 这里只写当前 Activity，不创建新的 span：轮次本身已经有 ASP.NET Core
/// 或调用方创建的父级 Activity，多建一层只会淹没真正的层次结构。
/// </remarks>
public static class AuditActivityEnrichment
{
    private static readonly ActivitySource ToolSource = new("OpenAgent");
    private static readonly ConcurrentDictionary<string, Activity?> PendingTools = new();
    private const int MaxAuditContentChars = 4000;

    private static string Truncate(string? value) => value is null || value.Length <= MaxAuditContentChars
        ? value ?? string.Empty
        : value[..MaxAuditContentChars] + "…[审计截断]";
    /// <summary>
    /// 把 <paramref name="turn"/> 的审计属性写入当前 Activity。
    /// 无活动 Activity 时静默返回——未开启追踪的部署不应因此失败。
    /// </summary>
    public static void Publish(TurnContext turn, string? username = null)
    {
        ArgumentNullException.ThrowIfNull(turn);

        Activity? activity = Activity.Current;
        if (activity is null)
        {
            return;
        }

        foreach (KeyValuePair<string, string> attribute in turn.ToAuditAttributes())
        {
            // SetTag 不覆盖已有值以外的行为；重复设置同名标签会覆盖旧值，
            // 因此只在轮次开始时设置一次，后续中间件不重复写入。
            activity.SetTag(attribute.Key, attribute.Value);
        }
        // 登录名让审计台的用户列显示域账号而不是平台内部的用户 UUID；
        // 网关把它列为用户身份属性的首选来源。
        if (!string.IsNullOrEmpty(username))
        {
            activity.SetTag("openagent.username", username);
        }

        activity.SetBaggage(AuditAttributeNames.TraceId, turn.TraceId);
    }

    /// <summary>
    /// <summary>
    /// 工具调用开始：为本次调用创建子 span（OpenAgent 源已被 OTLP 导出器订阅），
    /// 携带身份属性与调用参数——审计台由此展示一次会话的工具调用时间线。
    /// 子 span 自动继承轮次 Activity 的 trace 上下文，与输入输出同属一条 trace。
    /// 无监听器时返回 null——未开启追踪的部署不应因此失败。
    /// </summary>
    public static Activity? StartToolCall(TurnContext turn, string? username, AgentStreamEvent call)
    {
        ArgumentNullException.ThrowIfNull(turn);
        var activity = ToolSource.StartActivity(call.ToolName ?? "tool", ActivityKind.Internal);
        if (activity is null)
        {
            return null;
        }

        activity.SetTag("openagent.conversation_id", turn.ConversationId ?? string.Empty);
        activity.SetTag("openagent.user_id", turn.UserId);
        activity.SetTag("openagent.username", username ?? string.Empty);
        if (!string.IsNullOrEmpty(turn.AgentId))
        {
            activity.SetTag("openagent.agent_id", turn.AgentId);
        }
        activity.SetTag("openagent.tool.call_id", call.ToolCallId ?? string.Empty);
        activity.SetTag("langfuse.observation.type", "tool");
        activity.SetTag("langfuse.observation.input", Truncate(call.ToolArguments?.ToString() ?? call.Content));

        if (!string.IsNullOrEmpty(call.ToolCallId))
        {
            PendingTools[call.ToolCallId] = activity;
        }

        return activity;
    }

    /// <summary>
    /// 工具调用结束：记录结果（截断）与状态，并结束 span。
    /// </summary>
    public static void EndToolCall(AgentStreamEvent result)
    {
        ArgumentNullException.ThrowIfNull(result);

        Activity? activity = null;
        if (!string.IsNullOrEmpty(result.ToolCallId))
        {
            PendingTools.TryRemove(result.ToolCallId, out activity);
        }

        activity ??= ToolSource.StartActivity(result.ToolName ?? "tool", ActivityKind.Internal);
        if (activity is null)
        {
            return;
        }

        activity.SetTag("langfuse.observation.type", "tool");
        activity.SetTag("langfuse.observation.output", Truncate(result.Content));
        activity.SetTag("openagent.tool.status", "completed");
        activity.Stop();
    }

    /// <summary>
    /// 轮次收尾时关闭仍未匹配到结果的工具 span（轮次失败/驱动异常的兜底）。
    /// </summary>
    public static void ClosePendingToolSpans()
    {
        foreach (var pair in PendingTools)
        {
            if (PendingTools.TryRemove(pair.Key, out var activity) && activity is not null)
            {
                activity.SetTag("openagent.tool.status", "aborted");
                activity.Stop();
            }
        }
    }
    /// 把对话内容（用户输入与助手回复）写入当前 Activity，供审计平面展示。
    /// </summary>
    /// <remarks>
    /// 属性名沿用 Langfuse OTLP 的观察字段约定（langfuse.observation.input/output），
    /// 同时被审计网关的内容前缀（langfuse.observation）覆盖——网关会在转发前掩码
    /// 其中的敏感值并把原文密文入库，因此到达 Langfuse 的内容永远是脱敏后的。
    ///
    /// 在轮次结束时调用一次；无活动 Activity 时静默返回。
    /// </remarks>
    /// <summary>
    /// 把 Agent 的可读名称写入当前 Activity。审计网关优先取它（低于 agent_id
    /// 的编号）作为标签值，审计台 Agent 列由此显示名称而不是 agent-xxxx 编号。
    /// </summary>
    public static void PublishAgentName(string? agentName)
    {
        Activity? activity = Activity.Current;
        if (activity is null || string.IsNullOrEmpty(agentName))
        {
            return;
        }

        activity.SetTag("openagent.agent_name", agentName);
    }

    public static void PublishContent(string? query, string? response)
    {
        Activity? activity = Activity.Current;
        if (activity is null)
        {
            return;
        }

        if (!string.IsNullOrEmpty(query))
        {
            activity.SetTag("langfuse.observation.input", query);
        }

        if (!string.IsNullOrEmpty(response))
        {
            activity.SetTag("langfuse.observation.output", response);
        }
    }
}