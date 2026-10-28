using Microsoft.AspNetCore.Mvc;
using OpenAgent.Hosting.Errors;
using OpenAgent.Router.Models;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace OpenAgent.Router.Endpoints;

/// <summary>
/// 把路由层异常转换为统一的 ProblemDetails（含 traceId/timestamp/code 扩展字段），
/// 与 Engine/Runner 的错误契约一致；调用方用 TypedResults.Problem 包装返回。
/// </summary>
internal static class RouterProblem
{
    internal static ProblemDetails From(AgentRoutingException exception, HttpContext context) =>
        AgentProblemDetails.Create(
            $"{AgentProblemDetails.TypePrefix}/{exception.Code}",
            exception.Title,
            exception.StatusCode,
            exception.Message,
            context.Request.Path,
            AgentProblemDetails.ResolveTraceId(context));
}
