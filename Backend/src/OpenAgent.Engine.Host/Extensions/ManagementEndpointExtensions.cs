using Microsoft.AspNetCore.Mvc;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Mcp;
using OpenAgent.Contracts.Models;
using OpenAgent.Contracts.Rag;
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

        group.MapDelete("/mcp/{id}", async (
            [FromServices] AgentConfigManagementService manager,
            HttpContext context,
            string id,
            [FromQuery] string agentId,
            CancellationToken cancellationToken) =>
        {
            if (!HasScope(context, "agent.config.write")) return Results.Forbid();
            AgentConfigEntity? existing = await manager.GetAsync(agentId, cancellationToken).ConfigureAwait(false);
            if (existing == null) return Results.NotFound();

            int removed = existing.Config.Mcp.Servers.RemoveAll(item =>
                string.Equals(item.Name, id, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return Results.NotFound();

            AgentConfigEntity? saved = await manager.SaveAsync(
                agentId,
                existing,
                context.Request.Headers.IfMatch.FirstOrDefault(),
                cancellationToken).ConfigureAwait(false);
            return saved == null ? Results.Conflict() : Results.NoContent();
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

        group.MapGet("/rag", async (
            [FromServices] AgentConfigManagementService manager,
            HttpContext context,
            [FromQuery] string agentId,
            CancellationToken cancellationToken) =>
        {
            if (!HasScope(context, "agent.config.read")) return Results.Forbid();
            AgentConfigEntity? entity = await manager.GetAsync(agentId, cancellationToken).ConfigureAwait(false);
            return entity == null
                ? Results.NotFound()
                : Results.Ok(new RagConfig
                {
                    Enabled = entity.Config.Rag.Enabled,
                    EnabledRagInstanceIds = [.. entity.Config.Rag.EnabledRagInstanceIds],
                    Instances = entity.Config.Rag.Instances.Select(RedactRag).ToList()
                });
        });

        group.MapPut("/rag/{id}", async (
            [FromServices] AgentConfigManagementService manager,
            HttpContext context,
            string id,
            [FromQuery] string agentId,
            [FromBody] RagInstanceConfig instance,
            CancellationToken cancellationToken) =>
        {
            if (!HasScope(context, "agent.config.write")) return Results.Forbid();
            AgentConfigEntity? existing = await manager.GetAsync(agentId, cancellationToken).ConfigureAwait(false);
            if (existing == null) return Results.NotFound();

            instance.Id = id;
            RagInstanceConfig? current = existing.Config.Rag.Instances.FirstOrDefault(item =>
                string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            if (current != null && (string.IsNullOrWhiteSpace(instance.ApiKey) || instance.ApiKey.StartsWith("***", StringComparison.Ordinal)))
            {
                instance.ApiKey = current.ApiKey;
            }

            int index = existing.Config.Rag.Instances.FindIndex(item =>
                string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) existing.Config.Rag.Instances[index] = instance;
            else existing.Config.Rag.Instances.Add(instance);
            if (instance.Enabled && !existing.Config.Rag.EnabledRagInstanceIds.Contains(id, StringComparer.OrdinalIgnoreCase))
            {
                existing.Config.Rag.EnabledRagInstanceIds.Add(id);
            }

            AgentConfigEntity? saved = await manager.SaveAsync(
                agentId,
                existing,
                context.Request.Headers.IfMatch.FirstOrDefault(),
                cancellationToken).ConfigureAwait(false);
            return saved == null ? Results.Conflict() : Results.Ok(RedactRag(instance));
        });

        group.MapDelete("/rag/{id}", async (
            [FromServices] AgentConfigManagementService manager,
            HttpContext context,
            string id,
            [FromQuery] string agentId,
            CancellationToken cancellationToken) =>
        {
            if (!HasScope(context, "agent.config.write")) return Results.Forbid();
            AgentConfigEntity? existing = await manager.GetAsync(agentId, cancellationToken).ConfigureAwait(false);
            if (existing == null) return Results.NotFound();
            int removed = existing.Config.Rag.Instances.RemoveAll(item =>
                string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return Results.NotFound();
            existing.Config.Rag.EnabledRagInstanceIds.RemoveAll(item =>
                string.Equals(item, id, StringComparison.OrdinalIgnoreCase));
            AgentConfigEntity? saved = await manager.SaveAsync(
                agentId,
                existing,
                context.Request.Headers.IfMatch.FirstOrDefault(),
                cancellationToken).ConfigureAwait(false);
            return saved == null ? Results.Conflict() : Results.NoContent();
        });

        group.MapPost("/rag/test-connection", async (
            [FromServices] IHttpClientFactory httpClientFactory,
            [FromBody] RagConnectionTestRequest request,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            if (!HasScope(context, "capability.test")) return Results.Forbid();
            if (string.IsNullOrWhiteSpace(request.Instance.ApiEndpoint))
            {
                return Results.Ok(new RagConnectionTestResult
                {
                    Error = "RAG endpoint is required.",
                    TraceId = context.GetAgentRequest().TraceId
                });
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using HttpRequestMessage httpRequest = new(HttpMethod.Get, request.Instance.ApiEndpoint);
                if (!string.IsNullOrWhiteSpace(request.Instance.ApiKey))
                {
                    httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", request.Instance.ApiKey);
                }
                using HttpResponseMessage response = await httpClientFactory
                    .CreateClient("AgentLogin")
                    .SendAsync(httpRequest, cancellationToken)
                    .ConfigureAwait(false);
                stopwatch.Stop();
                return Results.Ok(new RagConnectionTestResult
                {
                    Success = response.IsSuccessStatusCode,
                    Connected = true,
                    StatusCode = (int)response.StatusCode,
                    LatencyMs = stopwatch.ElapsedMilliseconds,
                    Error = response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}",
                    TraceId = context.GetAgentRequest().TraceId
                });
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                stopwatch.Stop();
                return Results.Ok(new RagConnectionTestResult
                {
                    Success = false,
                    Connected = false,
                    LatencyMs = stopwatch.ElapsedMilliseconds,
                    Error = exception.Message,
                    TraceId = context.GetAgentRequest().TraceId
                });
            }
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

        foreach (RagInstanceConfig requestedRag in requested.Config.Rag.Instances)
        {
            RagInstanceConfig? existingRag = existing.Config.Rag.Instances.FirstOrDefault(item =>
                string.Equals(item.Id, requestedRag.Id, StringComparison.OrdinalIgnoreCase));
            if (existingRag != null
                && (string.IsNullOrWhiteSpace(requestedRag.ApiKey)
                    || requestedRag.ApiKey.StartsWith("***", StringComparison.Ordinal)))
            {
                requestedRag.ApiKey = existingRag.ApiKey;
            }
        }

        return requested;
    }

    private static AgentConfigEntity Redact(AgentConfigEntity entity)
    {
        entity.Config.Llm.ApiKey = string.IsNullOrWhiteSpace(entity.Config.Llm.ApiKey) ? string.Empty : "***redacted***";
        entity.Config.Rag.Instances = entity.Config.Rag.Instances.Select(RedactRag).ToList();
        return entity;
    }

    private static RagInstanceConfig RedactRag(RagInstanceConfig instance)
    {
        return new RagInstanceConfig
        {
            Id = instance.Id,
            Name = instance.Name,
            Enabled = instance.Enabled,
            Type = instance.Type,
            CollectionName = instance.CollectionName,
            ApiEndpoint = instance.ApiEndpoint,
            ApiKey = string.IsNullOrWhiteSpace(instance.ApiKey) ? string.Empty : "***",
            AdapterConfig = instance.AdapterConfig,
            AllowedUserIds = [.. instance.AllowedUserIds],
            AllowedGroups = [.. instance.AllowedGroups],
            AllowedTenantIds = [.. instance.AllowedTenantIds],
            AllowedRoles = [.. instance.AllowedRoles]
        };
    }
}
