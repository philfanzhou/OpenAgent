using Microsoft.AspNetCore.Mvc;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Mcp;
using OpenAgent.Contracts.Models;
using OpenAgent.Contracts.Security;
using OpenAgent.Engine.Config;
using OpenAgent.Engine.Host.Middleware;

namespace OpenAgent.Engine.Host.Extensions;

internal static class ManagementEndpointExtensions
{
    public static IEndpointConventionBuilder MapManagementEndpoints(
        this IEndpointRouteBuilder endpoints,
        string pattern = "/api/v1/admin")
    {
        RouteGroupBuilder group = endpoints.MapGroup(pattern).RequireAuthorization();

        group.MapGet("/agents", async (
            [FromServices] IAgentConfigProvider provider,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            if (!HasScope(context, "agent.read")) return Results.Forbid();
            return Results.Ok(await provider.ListAgentsAsync(cancellationToken).ConfigureAwait(false));
        });

        group.MapGet("/agents/{agentId}", async (
            [FromServices] AgentConfigManagementService manager,
            HttpContext context,
            string agentId,
            CancellationToken cancellationToken) =>
        {
            if (!HasScope(context, "agent.config.read")) return Results.Forbid();
            AgentConfigEntity? entity = await manager.GetAsync(agentId, cancellationToken).ConfigureAwait(false);
            return entity == null ? Results.NotFound() : Results.Ok(Redact(entity));
        });

        group.MapPut("/agents/{agentId}/config", async (
            [FromServices] AgentConfigManagementService manager,
            HttpContext context,
            string agentId,
            [FromBody] AgentConfigEntity entity,
            CancellationToken cancellationToken) =>
        {
            if (!HasScope(context, "agent.config.write")) return Results.Forbid();
            AgentConfigEntity? existing = await manager.GetAsync(agentId, cancellationToken).ConfigureAwait(false);
            AgentConfigEntity merged = MergeSecrets(existing, entity);
            AgentConfigEntity? saved = await manager.SaveAsync(
                agentId,
                merged,
                context.Request.Headers.IfMatch.FirstOrDefault() ?? entity.CurrentVersion,
                cancellationToken).ConfigureAwait(false);
            return saved == null ? Results.Conflict() : Results.Ok(Redact(saved));
        });

        group.MapGet("/mcp", async (
            [FromServices] AgentConfigManagementService manager,
            HttpContext context,
            [FromQuery] string agentId,
            CancellationToken cancellationToken) =>
        {
            if (!HasScope(context, "agent.config.read")) return Results.Forbid();
            AgentConfigEntity? entity = await manager.GetAsync(agentId, cancellationToken).ConfigureAwait(false);
            return entity == null ? Results.NotFound() : Results.Ok(entity.Config.Mcp);
        });

        group.MapPut("/mcp/{id}", async (
            [FromServices] AgentConfigManagementService manager,
            HttpContext context,
            string id,
            [FromQuery] string agentId,
            [FromBody] McpServerConfig server,
            CancellationToken cancellationToken) =>
        {
            if (!HasScope(context, "agent.config.write")) return Results.Forbid();
            AgentConfigEntity? existing = await manager.GetAsync(agentId, cancellationToken).ConfigureAwait(false);
            if (existing == null) return Results.NotFound();

            server.Name = id;
            int index = existing.Config.Mcp.Servers.FindIndex(item =>
                string.Equals(item.Name, id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) existing.Config.Mcp.Servers[index] = server;
            else existing.Config.Mcp.Servers.Add(server);

            AgentConfigEntity? saved = await manager.SaveAsync(
                agentId,
                existing,
                context.Request.Headers.IfMatch.FirstOrDefault(),
                cancellationToken).ConfigureAwait(false);
            return saved == null ? Results.Conflict() : Results.Ok(server);
        });

        group.MapPost("/mcp/test-connection", async (
            [FromServices] IMcpConnectionTester tester,
            [FromBody] McpConnectionTestRequest request,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            if (!HasScope(context, "capability.test")) return Results.Forbid();
            McpConnectionTestResult result = await tester.TestAsync(
                request,
                context.GetAgentRequest().User,
                context.GetAgentRequest().TraceId,
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(result);
        });

        group.MapGet("/skills", async (
            [FromServices] AgentConfigManagementService manager,
            HttpContext context,
            [FromQuery] string agentId,
            CancellationToken cancellationToken) =>
        {
            if (!HasScope(context, "agent.config.read")) return Results.Forbid();
            AgentConfigEntity? entity = await manager.GetAsync(agentId, cancellationToken).ConfigureAwait(false);
            return entity == null ? Results.NotFound() : Results.Ok(entity.Config.Skills);
        });

        group.MapPut("/skills/{agentId}", async (
            [FromServices] AgentConfigManagementService manager,
            HttpContext context,
            string agentId,
            [FromBody] SkillsConfig skills,
            CancellationToken cancellationToken) =>
        {
            if (!HasScope(context, "agent.config.write")) return Results.Forbid();
            AgentConfigEntity? existing = await manager.GetAsync(agentId, cancellationToken).ConfigureAwait(false);
            if (existing == null) return Results.NotFound();
            existing.Config.Skills = skills;
            AgentConfigEntity? saved = await manager.SaveAsync(
                agentId,
                existing,
                context.Request.Headers.IfMatch.FirstOrDefault(),
                cancellationToken).ConfigureAwait(false);
            return saved == null ? Results.Conflict() : Results.Ok(saved.Config.Skills);
        });

        group.MapPost("/skills/test", (
            [FromBody] SkillsConfig skills,
            HttpContext context) =>
        {
            if (!HasScope(context, "capability.test")) return Results.Forbid();
            string[] invalid = skills.Instances
                .Where(item => string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Name))
                .Select(item => string.IsNullOrWhiteSpace(item.Id) ? item.Name : item.Id)
                .ToArray();
            return Results.Ok(new
            {
                success = invalid.Length == 0,
                enabledCount = skills.EnabledSkills.Count,
                instanceCount = skills.Instances.Count,
                invalidSkills = invalid,
                error = invalid.Length == 0 ? null : "Skill instances require both id and name."
            });
        });

        return group;
    }

    private static bool HasScope(HttpContext context, string requiredScope)
    {
        if (string.Equals(context.User.Identity?.AuthenticationType, "PassThrough", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (context.User.IsInRole("Admin")
            || context.User.Claims.Any(claim =>
                (claim.Type is "scope" or "scp" or "permissions")
                && claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Contains("agent.admin", StringComparer.OrdinalIgnoreCase)))
        {
            return true;
        }

        return context.User.Claims
            .Where(claim => claim.Type is "scope" or "scp" or "permissions")
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Contains(requiredScope, StringComparer.OrdinalIgnoreCase);
    }

    private static AgentConfigEntity MergeSecrets(AgentConfigEntity? existing, AgentConfigEntity requested)
    {
        if (existing == null) return requested;
        if (string.IsNullOrWhiteSpace(requested.Config.Llm.ApiKey)
            || requested.Config.Llm.ApiKey.StartsWith("***", StringComparison.Ordinal))
        {
            requested.Config.Llm.ApiKey = existing.Config.Llm.ApiKey;
        }

        return requested;
    }

    private static AgentConfigEntity Redact(AgentConfigEntity entity)
    {
        entity.Config.Llm.ApiKey = string.IsNullOrWhiteSpace(entity.Config.Llm.ApiKey) ? string.Empty : "***redacted***";
        return entity;
    }
}
