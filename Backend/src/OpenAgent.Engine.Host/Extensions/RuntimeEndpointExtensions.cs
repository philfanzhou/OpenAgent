using OpenAgent.Contracts.Configuration;
using OpenAgent.Engine.Hosting.Errors;

namespace OpenAgent.Engine.Host.Extensions;

internal static class RuntimeEndpointExtensions
{
    internal static void MapRuntimeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder group = endpoints.MapGroup("/api/v1/admin/runtimes").RequireAuthorization();
        group.MapGet("", async (IRuntimeProfileRepository repository, HttpContext context, CancellationToken ct) =>
            TypedResults.Ok(await repository.ListAsync(Tenant(context), ct).ConfigureAwait(false)));
        group.MapPut("/{id}", async (string id, RuntimeProfile profile, IRuntimeProfileRepository repository, HttpContext context, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(profile.Name)
                || profile.Provider != "OpenCode" || profile.TurnTimeoutSeconds is < 1 or > 86400)
            {
                return Results.Problem(AgentProblemDetails.Invalid("Only OpenCode is enabled. Runtime name and a timeout between 1 and 86400 seconds are required.", context));
            }
            profile.Id = id;
            await repository.SaveAsync(Tenant(context), profile, ct).ConfigureAwait(false);
            return Results.Ok(profile);
        });
        group.MapDelete("/{id}", async (string id, IRuntimeProfileRepository repository, HttpContext context, CancellationToken ct) =>
        {
            return await repository.DeleteAsync(Tenant(context), id, ct).ConfigureAwait(false)
                ? Results.NoContent() : Results.NotFound();
        });
    }

    private static string Tenant(HttpContext context) =>
        AgentEndpointRequestMapper.RequireTenant(context);
}
