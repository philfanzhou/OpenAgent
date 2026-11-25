using System.Diagnostics;
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
    /// <summary>
    /// 把 <paramref name="turn"/> 的审计属性写入当前 Activity。
    /// 返回轮次身份是否写入具名来源的 Activity。
    /// </summary>
    public static bool Publish(TurnContext turn, string? username = null)
    {
        ArgumentNullException.ThrowIfNull(turn);

        Activity? activity = Activity.Current;
        // A bare Activity uses the unnamed default source, which is absent from the host's OTLP subscription.
        if (activity is null || activity.Source.Name.Length == 0)
        {
            return false;
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
        return !string.IsNullOrWhiteSpace(turn.ConversationId) && !string.IsNullOrWhiteSpace(turn.UserId);
    }

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

    /// <summary>Uses the observation fields masked by the audit gateway.</summary>
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
