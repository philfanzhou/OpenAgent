using Microsoft.AspNetCore.Mvc;
using OpenAgent.Contracts.Approvals;
using OpenAgent.Engine.Host.Middleware;

namespace OpenAgent.Engine.Host.Extensions;

internal static class HumanApprovalEndpointExtensions
{
    internal static void MapHumanApprovals(this RouteGroupBuilder group)
    {
        group.MapPost("/approvals/{approvalId}/decision", DecideAsync)
            .RequireAuthorization("approval.decide")
            .WithName("DecideHumanApproval")
            .WithTags("Approval");
    }

    private static async Task<IResult> DecideAsync(
        [FromRoute] string approvalId,
        [FromBody] HumanApprovalDecisionRequest decision,
        [FromServices] IHumanApprovalService approvals,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        string tenantId = AgentEndpointRequestMapper.RequireTenant(context);
        HumanApprovalDecisionResult result = await approvals.DecideAsync(
            tenantId,
            approvalId,
            decision,
            context.GetAgentRequest().User,
            cancellationToken).ConfigureAwait(false);
        return Results.Ok(result);
    }
}
