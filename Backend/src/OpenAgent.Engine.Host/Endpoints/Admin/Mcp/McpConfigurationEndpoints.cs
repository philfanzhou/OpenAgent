using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Engine.Config;
using OpenAgent.Engine.Host.Endpoints;
using OpenAgent.Engine.Hosting.Errors;

namespace OpenAgent.Engine.Host.Endpoints.Admin.Mcp;

internal static class McpConfigurationEndpoints
{
    internal static void MapMcpConfigurationEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/mcp", ListMcpAsync)
            .WithName("ListMcpServers")
            .WithTags("Admin MCP");

        group.MapGet("/mcp/{id}", GetMcpAsync)
            .WithName("GetMcpServer")
            .WithTags("Admin MCP");

        group.MapPut("/mcp/{id}", SaveMcpAsync)
            .WithName("SaveMcpServer")
            .WithTags("Admin MCP");

        group.MapDelete("/mcp/{id}", DeleteMcpAsync)
            .WithName("DeleteMcpServer")
            .WithTags("Admin MCP");
    }

    private static async Task<Results<Ok<IReadOnlyList<McpServerConfig>>, ProblemHttpResult>> ListMcpAsync(
        [FromServices] McpProfileManagementService mcpProfiles,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!HasScope(context, "agent.config.read"))
            return Forbidden(context, "agent.config.read");
        IReadOnlyList<McpServerConfig> servers = await mcpProfiles
            .ListAsync(RequireTenant(context), cancellationToken).ConfigureAwait(false);
        IReadOnlyList<McpServerConfig> redacted = servers
            .Select(ConfigurationRedactor.RedactMcpServer).ToList();
        return TypedResults.Ok(redacted);
    }

    private static async Task<Results<Ok<McpServerConfig>, ProblemHttpResult>> GetMcpAsync(
        [FromServices] McpProfileManagementService mcpProfiles,
        string id,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!HasScope(context, "agent.config.read"))
            return Forbidden(context, "agent.config.read");
        McpServerConfig? server = await mcpProfiles
            .GetAsync(id, RequireTenant(context), cancellationToken).ConfigureAwait(false);
        return server == null
            ? TypedResults.Problem(AgentProblemDetails.NotFound($"MCP server '{id}' was not found.", context))
            : TypedResults.Ok(ConfigurationRedactor.RedactMcpServer(server));
    }

    private static async Task<Results<Ok<McpServerConfig>, ProblemHttpResult>> SaveMcpAsync(
        string id,
        [FromBody] McpServerConfig server,
        [FromServices] McpProfileManagementService mcpProfiles,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!HasScope(context, "agent.config.write"))
            return Forbidden(context, "agent.config.write");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(server.Name))
            return TypedResults.Problem(AgentProblemDetails.Invalid("MCP requires an id and name.", context));
        if (string.IsNullOrWhiteSpace(server.Url))
            return TypedResults.Problem(AgentProblemDetails.Invalid("MCP requires a URL.", context));
        server.Name = id;
        McpServerConfig saved = await mcpProfiles
            .SaveAsync(server, RequireTenant(context), cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(ConfigurationRedactor.RedactMcpServer(saved));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteMcpAsync(
        string id,
        [FromServices] McpProfileManagementService mcpProfiles,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!HasScope(context, "agent.config.write"))
            return Forbidden(context, "agent.config.write");
        return await mcpProfiles.DeleteAsync(id, RequireTenant(context), cancellationToken).ConfigureAwait(false)
            ? TypedResults.NoContent()
            : TypedResults.Problem(AgentProblemDetails.NotFound($"MCP server '{id}' was not found.", context));
    }

    private static ProblemHttpResult Forbidden(HttpContext context, string requiredScope) =>
        TypedResults.Problem(AgentProblemDetails.PermissionDenied(
            $"Access denied due to insufficient permissions. Missing required scope: {requiredScope}.", context));

    private static bool HasScope(HttpContext context, string requiredScope) =>
        context.User.Identity?.IsAuthenticated == true;

    private static string RequireTenant(HttpContext context) =>
        AgentEndpointRequestMapper.RequireTenant(context);
}
