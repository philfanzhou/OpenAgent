using Microsoft.AspNetCore.Mvc;
using OpenAgent.Contracts.Security;
using OpenAgent.Engine.Host.Middleware;
using OpenAgent.Engine.Hosting.Errors;

namespace OpenAgent.Engine.Host.Endpoints.Admin;

internal static class ResourcePolicyEndpoints
{
    internal static void MapResourcePolicyEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/resource-policies/{resourceType}/{**resourceId}", ReadAsync).WithName("GetResourcePolicy");
        group.MapPut("/resource-policies/{resourceType}/{**resourceId}", SaveAsync).WithName("SaveResourcePolicy");
    }

    private static async Task<IResult> ReadAsync(AgentResourceType resourceType, string resourceId,
        [FromServices] IResourcePolicyStore store, HttpContext context, CancellationToken ct)
    {
        if (!ResourceAccess.HasScope(context.GetAgentRequest().User, "resource.policy.write"))
            return TypedResults.Problem(AgentProblemDetails.PermissionDenied("Policy administration permission is required.", context));
        if (!Enum.IsDefined(resourceType)) return TypedResults.BadRequest();
        ResourceAccessSnapshot snapshot = await store.ReadAsync(AgentEndpointRequestMapper.RequireTenant(context),
            new(string.Empty, resourceType, resourceId, "use"), ct).ConfigureAwait(false);
        return TypedResults.Ok(snapshot.Policy ?? new ResourcePolicy());
    }

    private static async Task<IResult> SaveAsync(AgentResourceType resourceType, string resourceId,
        [FromBody] ResourcePolicy policy, [FromServices] IResourcePolicyStore store,
        HttpContext context, CancellationToken ct)
    {
        if (!ResourceAccess.HasScope(context.GetAgentRequest().User, "resource.policy.write"))
            return TypedResults.Problem(AgentProblemDetails.PermissionDenied("Policy administration permission is required.", context));
        try
        {
            ResourcePolicy? saved = await store.SaveAsync(AgentEndpointRequestMapper.RequireTenant(context),
                resourceType, resourceId, policy, policy.Revision, ct).ConfigureAwait(false);
            return saved is null
                ? TypedResults.Problem(AgentProblemDetails.Conflict("Policy revision changed. Reload before saving.", context))
                : TypedResults.Ok(saved);
        }
        catch (ArgumentException)
        {
            return TypedResults.Problem(AgentProblemDetails.Invalid("Invalid resource policy.", context));
        }
    }
}
