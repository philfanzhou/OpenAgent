using System.Diagnostics;
using OpenAgent.Contracts.Runtime;

namespace OpenAgent.Core.Observability;

/// <summary>
/// 把轮次级身份坐标发布到当前活动（<see cref="Activity.Current"/>），
/// 供外部观测平面关联——最直接的使用方是 OpenAgent.Audit 的 Masking Gateway，
/// 它按这些键把 trace 归集到会话/用户/Agent，并写入密文行的保留期归属。
///
/// 为什么放在执行器而不是请求中间件：会话 id 只有在端点解析请求时才会生成
/// （见 AgentEndpointRequestMapper），此时请求中间件的 <c>finally</c> 尚未执行，
/// 但中间件无法预知该值；而 <c>CreateTurn</c> 是 TurnContext 的唯一构造点，
/// 能同时看到 conversation / user / agent / tenant 四个坐标。
///
/// 这里只打标签，不创建 span：不改变现有 trace 结构，也不引入新的采样边界。
/// </summary>
internal static class AuditActivityEnrichment
{
    internal static void Publish(TurnContext turn)
    {
        Activity? activity = Activity.Current;
        if (activity is null)
        {
            return;
        }

        foreach (KeyValuePair<string, string?> attribute in turn.ToAuditAttributes())
        {
            if (!string.IsNullOrWhiteSpace(attribute.Value))
            {
                activity.SetTag(attribute.Key, attribute.Value);
            }
        }
    }
}