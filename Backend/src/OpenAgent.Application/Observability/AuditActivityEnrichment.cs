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
    /// 无活动 Activity 时静默返回——未开启追踪的部署不应因此失败。
    /// </summary>
    public static void Publish(TurnContext turn)
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

        activity.SetBaggage(AuditAttributeNames.TraceId, turn.TraceId);
    }

    /// <summary>
    /// 把对话内容（用户输入与助手回复）写入当前 Activity，供审计平面展示。
    /// </summary>
    /// <remarks>
    /// 属性名沿用 Langfuse OTLP 的观察字段约定（langfuse.observation.input/output），
    /// 同时被审计网关的内容前缀（langfuse.observation）覆盖——网关会在转发前掩码
    /// 其中的敏感值并把原文密文入库，因此到达 Langfuse 的内容永远是脱敏后的。
    ///
    /// 在轮次结束时调用一次；无活动 Activity 时静默返回。
    /// </remarks>
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