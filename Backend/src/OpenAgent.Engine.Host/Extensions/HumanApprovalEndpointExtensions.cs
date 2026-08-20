using Microsoft.AspNetCore.Mvc;
using OpenAgent.Contracts.Approvals;
using OpenAgent.Engine.Host.Middleware;

namespace OpenAgent.Engine.Host.Extensions;

internal static class HumanApprovalEndpointExtensions
{
    internal static void MapHumanApprovals(this RouteGroupBuilder group)
    {
        group.MapGet("/approvals", ListPendingAsync)
            .RequireAuthorization("approval.decide")
            .WithName("ListPendingHumanApprovals")
            .WithTags("Approval");

        group.MapGet("/approvals/{approvalId}", GetAsync)
            .RequireAuthorization("approval.decide")
            .WithName("GetHumanApproval")
            .WithTags("Approval");

        group.MapPost("/approvals/{approvalId}/decision", DecideAsync)
            .RequireAuthorization("approval.decide")
            .WithName("DecideHumanApproval")
            .WithTags("Approval");

        group.MapPost("/approvals/{approvalId}/withdraw", WithdrawAsync)
            .WithName("WithdrawHumanApproval")
            .WithTags("Approval");
    }

    private static async Task<IResult> ListPendingAsync(
        [FromServices] IHumanApprovalService approvals,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<HumanApprovalRequest> pending = await approvals.ListPendingAsync(
            AgentEndpointRequestMapper.RequireTenant(context),
            context.GetAgentRequest().User,
            cancellationToken).ConfigureAwait(false);
        return Results.Ok(pending);
    }

    private static async Task<IResult> GetAsync(
        [FromServices] IHumanApprovalService approvals,
        HttpContext context,
        string approvalId,
        CancellationToken cancellationToken)
    {
        HumanApprovalRequest? approval = await approvals.GetAsync(
            AgentEndpointRequestMapper.RequireTenant(context),
            approvalId,
            context.GetAgentRequest().User,
            cancellationToken).ConfigureAwait(false);
        return approval == null ? Results.NotFound() : Results.Ok(approval);
    }

    private static async Task<IResult> DecideAsync(
        [FromServices] IHumanApprovalService approvals,
        [FromBody] HumanApprovalDecisionRequest decision,
        HttpContext context,
        string approvalId,
        CancellationToken cancellationToken)
    {
        HumanApprovalDecisionResult result = await approvals.DecideAsync(
            AgentEndpointRequestMapper.RequireTenant(context),
            approvalId,
            decision,
            context.GetAgentRequest().User,
            cancellationToken).ConfigureAwait(false);
        return Results.Ok(result);
    }

    private static async Task<IResult> WithdrawAsync(
        [FromServices] IHumanApprovalService approvals,
        HttpContext context,
        string approvalId,
        CancellationToken cancellationToken)
    {
        HumanApprovalRequest approval = await approvals.WithdrawAsync(
            AgentEndpointRequestMapper.RequireTenant(context),
            approvalId,
            context.GetAgentRequest().User,
            cancellationToken).ConfigureAwait(false);
        return Results.Ok(approval);
    }
}
