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
            CancellationToken cancellationToken) =>
        {
            return Results.Ok(await provider.ListAgentsAsync(cancellationToken).ConfigureAwait(false));
        }).RequireAuthorization(GatewayPermissions.AgentRead);

        group.MapGet("/agents/{agentId}", async (
            [FromServices] AgentConfigManagementService manager,
            string agentId,
            CancellationToken cancellationToken) =>
        {
            AgentConfigEntity? entity = await manager.GetAsync(agentId, cancellationToken).ConfigureAwait(false);
            return entity == null ? Results.NotFound() : Results.Ok(Redact(entity));
        }).RequireAuthorization(GatewayPermissions.AgentConfigRead);

        group.MapGet("/llm", async (
            [FromServices] LlmProfileManagementService manager,
            CancellationToken cancellationToken) =>
        {
            IReadOnlyList<LlmProviderProfile> profiles = await manager.ListAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(profiles.Select(RedactLlm));
        }).RequireAuthorization(GatewayPermissions.AgentConfigRead);

        group.MapGet("/llm/{id}", async (
            [FromServices] LlmProfileManagementService manager,
            string id,
            CancellationToken cancellationToken) =>
        {
            LlmProviderProfile? profile = await manager.GetAsync(id, cancellationToken).ConfigureAwait(false);
            return profile == null ? Results.NotFound() : Results.Ok(RedactLlm(profile));
        }).RequireAuthorization(GatewayPermissions.AgentConfigRead);

        group.MapPut("/llm/{id}", async (
            [FromServices] LlmProfileManagementService manager,
            string id,
            [FromBody] LlmProviderProfile profile,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(profile.Name)
                || string.IsNullOrWhiteSpace(profile.ModelId) || string.IsNullOrWhiteSpace(profile.Endpoint))
            {
                return Results.BadRequest(new { error = "LLM requires id, name, modelId and endpoint." });
            }

            profile.Id = id;
            LlmProviderProfile? existing = await manager.GetAsync(id, cancellationToken).ConfigureAwait(false);
            if (existing != null && (string.IsNullOrWhiteSpace(profile.ApiKey)
                || profile.ApiKey.StartsWith("***", StringComparison.Ordinal)))
            {
                profile.ApiKey = existing.ApiKey;
            }

            LlmProviderProfile saved = await manager.SaveAsync(profile, cancellationToken).ConfigureAwait(false);
            return Results.Ok(RedactLlm(saved));
        }).RequireAuthorization(GatewayPermissions.AgentConfigWrite);

        group.MapDelete("/llm/{id}", async (
            [FromServices] LlmProfileManagementService manager,
            string id,
            CancellationToken cancellationToken) =>
        {
            return await manager.DeleteAsync(id, cancellationToken).ConfigureAwait(false)
                ? Results.NoContent()
                : Results.NotFound();
        }).RequireAuthorization(GatewayPermissions.AgentConfigWrite);

        group.MapPost("/llm/test-connection", async (
            [FromServices] IHttpClientFactory httpClientFactory,
            [FromBody] LlmConnectionTestRequest request,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            LlmProviderProfile profile = request.Profile;
            string traceId = context.GetAgentRequest().TraceId ?? string.Empty;
            if (string.IsNullOrWhiteSpace(profile.Endpoint))
            {
                return Results.Ok(new LlmConnectionTestResult
                {
                    ModelId = profile.ModelId,
                    Error = "LLM endpoint is required.",
                    TraceId = traceId
                });
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                string endpoint = profile.Endpoint.TrimEnd('/');
                if (!endpoint.EndsWith("/models", StringComparison.OrdinalIgnoreCase)) endpoint += "/models";
                using HttpRequestMessage httpRequest = new(HttpMethod.Get, endpoint);
                if (!string.IsNullOrWhiteSpace(profile.ApiKey) && !profile.ApiKey.StartsWith("***", StringComparison.Ordinal))
                {
                    if (profile.Format == ApiFormat.AnthropicMessages)
                    {
                        httpRequest.Headers.TryAddWithoutValidation("x-api-key", profile.ApiKey);
                        httpRequest.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                    }
                    else
                    {
                        httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", profile.ApiKey);
                    }
                }

                using HttpResponseMessage response = await httpClientFactory
                    .CreateClient("AgentLogin")
                    .SendAsync(httpRequest, cancellationToken)
                    .ConfigureAwait(false);
                stopwatch.Stop();
                return Results.Ok(new LlmConnectionTestResult
                {
                    Success = response.IsSuccessStatusCode,
                    Connected = true,
                    StatusCode = (int)response.StatusCode,
                    LatencyMs = stopwatch.ElapsedMilliseconds,
                    ModelId = profile.ModelId,
                    Error = response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}",
                    TraceId = traceId
                });
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                stopwatch.Stop();
                return Results.Ok(new LlmConnectionTestResult
                {
                    Success = false,
                    Connected = false,
                    LatencyMs = stopwatch.ElapsedMilliseconds,
                    ModelId = profile.ModelId,
                    Error = exception.Message,
                    TraceId = traceId
                });
            }
        }).RequireAuthorization(GatewayPermissions.CapabilityTest);

        group.MapPut("/agents/{agentId}/config", async (
            [FromServices] AgentConfigManagementService manager,
            HttpContext context,
            string agentId,
            [FromBody] AgentConfigEntity entity,
            CancellationToken cancellationToken) =>
        {
            AgentConfigEntity? existing = await manager.GetAsync(agentId, cancellationToken).ConfigureAwait(false);
            AgentConfigEntity merged = MergeSecrets(existing, entity);
            AgentConfigEntity? saved = await manager.SaveAsync(
                agentId,
                merged,
                context.Request.Headers.IfMatch.FirstOrDefault() ?? entity.CurrentVersion,
                cancellationToken).ConfigureAwait(false);
            return saved == null ? Results.Conflict() : Results.Ok(Redact(saved));
        }).RequireAuthorization(GatewayPermissions.AgentConfigWrite);

        group.MapGet("/mcp", async (
            [FromServices] AgentConfigManagementService manager,
            [FromQuery] string agentId,
            CancellationToken cancellationToken) =>
        {
            AgentConfigEntity? entity = await manager.GetAsync(agentId, cancellationToken).ConfigureAwait(false);
            return entity == null ? Results.NotFound() : Results.Ok(RedactMcp(entity.Config.Mcp));
        }).RequireAuthorization(GatewayPermissions.AgentConfigRead);

        group.MapPut("/mcp/{id}", async (
            [FromServices] AgentConfigManagementService manager,
            HttpContext context,
            string id,
            [FromQuery] string agentId,
            [FromBody] McpServerConfig server,
            CancellationToken cancellationToken) =>
        {
            AgentConfigEntity? existing = await manager.GetAsync(agentId, cancellationToken).ConfigureAwait(false);
            if (existing == null) return Results.NotFound();

            server.Name = id;
            int index = existing.Config.Mcp.Servers.FindIndex(item =>
                string.Equals(item.Name, id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
                existing.Config.Mcp.Servers[index] = MergeMcpSecrets(
                    existing.Config.Mcp.Servers[index],
                    server);
            else
                existing.Config.Mcp.Servers.Add(server);

            AgentConfigEntity? saved = await manager.SaveAsync(
                agentId,
                existing,
                context.Request.Headers.IfMatch.FirstOrDefault(),
                cancellationToken).ConfigureAwait(false);
            return saved == null ? Results.Conflict() : Results.Ok(RedactMcpServer(server));
        }).RequireAuthorization(GatewayPermissions.AgentConfigWrite);

        group.MapDelete("/mcp/{id}", async (
            [FromServices] AgentConfigManagementService manager,
            HttpContext context,
            string id,
            [FromQuery] string agentId,
            CancellationToken cancellationToken) =>
        {
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
        }).RequireAuthorization(GatewayPermissions.AgentConfigWrite);

        group.MapPost("/mcp/test-connection", async (
            [FromServices] IMcpConnectionTester tester,
            [FromBody] McpConnectionTestRequest request,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            McpConnectionTestResult result = await tester.TestAsync(
                request,
                context.GetAgentRequest().User,
                context.GetAgentRequest().TraceId,
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(result);
        }).RequireAuthorization(GatewayPermissions.CapabilityTest);

        group.MapGet("/rag", async (
            [FromServices] AgentConfigManagementService manager,
            [FromQuery] string agentId,
            CancellationToken cancellationToken) =>
        {
            AgentConfigEntity? entity = await manager.GetAsync(agentId, cancellationToken).ConfigureAwait(false);
            return entity == null
                ? Results.NotFound()
                : Results.Ok(new RagConfig
                {
                    Enabled = entity.Config.Rag.Enabled,
                    EnabledRagInstanceIds = [.. entity.Config.Rag.EnabledRagInstanceIds],
                    Instances = entity.Config.Rag.Instances.Select(RedactRag).ToList()
                });
        }).RequireAuthorization(GatewayPermissions.AgentConfigRead);

        group.MapPut("/rag/{id}", async (
            [FromServices] AgentConfigManagementService manager,
            HttpContext context,
            string id,
            [FromQuery] string agentId,
            [FromBody] RagInstanceConfig instance,
            CancellationToken cancellationToken) =>
        {
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
        }).RequireAuthorization(GatewayPermissions.AgentConfigWrite);

        group.MapDelete("/rag/{id}", async (
            [FromServices] AgentConfigManagementService manager,
            HttpContext context,
            string id,
            [FromQuery] string agentId,
            CancellationToken cancellationToken) =>
        {
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
        }).RequireAuthorization(GatewayPermissions.AgentConfigWrite);

        group.MapPost("/rag/test-connection", async (
            [FromServices] IHttpClientFactory httpClientFactory,
            [FromBody] RagConnectionTestRequest request,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
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
        }).RequireAuthorization(GatewayPermissions.CapabilityTest);

        group.MapGet("/skills", async (
            [FromServices] AgentConfigManagementService manager,
            [FromQuery] string agentId,
            CancellationToken cancellationToken) =>
        {
            AgentConfigEntity? entity = await manager.GetAsync(agentId, cancellationToken).ConfigureAwait(false);
            return entity == null ? Results.NotFound() : Results.Ok(entity.Config.Skills);
        }).RequireAuthorization(GatewayPermissions.AgentConfigRead);

        group.MapPut("/skills/{agentId}", async (
            [FromServices] AgentConfigManagementService manager,
            HttpContext context,
            string agentId,
            [FromBody] SkillsConfig skills,
            CancellationToken cancellationToken) =>
        {
            AgentConfigEntity? existing = await manager.GetAsync(agentId, cancellationToken).ConfigureAwait(false);
            if (existing == null) return Results.NotFound();
            existing.Config.Skills = skills;
            AgentConfigEntity? saved = await manager.SaveAsync(
                agentId,
                existing,
                context.Request.Headers.IfMatch.FirstOrDefault(),
                cancellationToken).ConfigureAwait(false);
            return saved == null ? Results.Conflict() : Results.Ok(saved.Config.Skills);
        }).RequireAuthorization(GatewayPermissions.AgentConfigWrite);

        group.MapPost("/skills/test", (
            [FromBody] SkillsConfig skills) =>
        {
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
        }).RequireAuthorization(GatewayPermissions.CapabilityTest);

        return group;
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

        foreach (McpServerConfig requestedServer in requested.Config.Mcp.Servers)
        {
            McpServerConfig? existingServer = existing.Config.Mcp.Servers.FirstOrDefault(item =>
                string.Equals(item.Name, requestedServer.Name, StringComparison.OrdinalIgnoreCase));
            MergeMcpSecrets(existingServer, requestedServer);
        }

        return requested;
    }

    internal static AgentConfigEntity Redact(AgentConfigEntity entity)
    {
        entity.Config.Llm.ApiKey = string.IsNullOrWhiteSpace(entity.Config.Llm.ApiKey)
            ? string.Empty
            : "***";
        entity.Config.Mcp = RedactMcp(entity.Config.Mcp);
        entity.Config.Rag.Instances = entity.Config.Rag.Instances.Select(RedactRag).ToList();
        return entity;
    }

    internal static McpConfig RedactMcp(McpConfig config) => new()
    {
        Servers = config.Servers.Select(RedactMcpServer).ToList()
    };

    internal static McpServerConfig MergeMcpSecrets(
        McpServerConfig? existing,
        McpServerConfig requested)
    {
        if (existing == null)
        {
            return requested;
        }

        foreach ((string key, string value) in requested.EnvironmentVariables.ToArray())
        {
            if (value.StartsWith("***", StringComparison.Ordinal)
                && existing.EnvironmentVariables.TryGetValue(key, out string? secret))
            {
                requested.EnvironmentVariables[key] = secret;
            }
        }

        return requested;
    }

    private static McpServerConfig RedactMcpServer(McpServerConfig server) => new()
    {
        Name = server.Name,
        Url = server.Url,
        Type = server.Type,
        Command = server.Command,
        Arguments = [.. server.Arguments],
        WorkingDirectory = server.WorkingDirectory,
        EnvironmentVariables = server.EnvironmentVariables.ToDictionary(
            item => item.Key,
            item => string.IsNullOrEmpty(item.Value) ? string.Empty : "***",
            StringComparer.OrdinalIgnoreCase)
    };

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

    internal static LlmProviderProfile RedactLlm(LlmProviderProfile profile)
    {
        return new LlmProviderProfile
        {
            Id = profile.Id,
            Name = profile.Name,
            Format = profile.Format,
            ModelId = profile.ModelId,
            Endpoint = profile.Endpoint,
            ApiKey = string.IsNullOrWhiteSpace(profile.ApiKey) ? string.Empty : "***",
            Temperature = profile.Temperature
        };
    }
}
