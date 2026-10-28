using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Mcp;
using OpenAgent.Contracts.Models;
using OpenAgent.Contracts.Rag;
using OpenAgent.Core.Abstract;
using OpenAgent.Engine.Abstractions;
using OpenAgent.Engine.Config;
using OpenAgent.Engine.Host.Middleware;
using OpenAgent.Hosting.Errors;

namespace OpenAgent.Engine.Host.Extensions;

/// <summary>
/// /api/v1/admin 下的配置管理端点（MCP / RAG / LLM / Agent 配置），原为
/// ConfigurationController，统一迁移为 Minimal API + TypedResults。仅开发环境映射。
/// </summary>
internal static class ConfigurationEndpointExtensions
{
    public static IEndpointConventionBuilder MapConfigurationEndpoints(
        this IEndpointRouteBuilder endpoints,
        string pattern = "/api/v1/admin")
    {
        RouteGroupBuilder group = endpoints.MapGroup(pattern).RequireAuthorization();

        MapMcpEndpoints(group);
        MapRagEndpoints(group);
        MapLlmEndpoints(group);
        MapAgentEndpoints(group);

        return group;
    }

    private static void MapMcpEndpoints(RouteGroupBuilder group)
    {
        group.MapPost("/mcp/test-connection", TestMcpAsync)
            .WithName("TestMcpConnection")
            .WithTags("Admin MCP");

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

    private static void MapRagEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/rag", GetRagAsync)
            .WithName("GetRagConfig")
            .WithTags("Admin RAG");

        group.MapPut("/rag/{id}", SaveRagAsync)
            .WithName("SaveRagInstance")
            .WithTags("Admin RAG");

        group.MapDelete("/rag/{id}", DeleteRagAsync)
            .WithName("DeleteRagInstance")
            .WithTags("Admin RAG");

        group.MapPost("/rag/test-connection", TestRagAsync)
            .WithName("TestRagConnection")
            .WithTags("Admin RAG");
    }

    private static void MapLlmEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/llm", ListModelsAsync)
            .WithName("ListLlmProfiles")
            .WithTags("Admin LLM");

        group.MapGet("/llm/{id}", GetModelAsync)
            .WithName("GetLlmProfile")
            .WithTags("Admin LLM");

        group.MapPut("/llm/{id}", SaveModelAsync)
            .WithName("SaveLlmProfile")
            .WithTags("Admin LLM");

        group.MapDelete("/llm/{id}", DeleteModelAsync)
            .WithName("DeleteLlmProfile")
            .WithTags("Admin LLM");

        group.MapPost("/llm/test-connection", TestModelAsync)
            .WithName("TestLlmConnection")
            .WithTags("Admin LLM");
    }

    private static void MapAgentEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/agents", ListAgentsAsync)
            .WithName("ListAgentConfigs")
            .WithTags("Admin Agents");

        group.MapGet("/agents/{agentId}", GetAgentAsync)
            .WithName("GetAgentConfig")
            .WithTags("Admin Agents");

        group.MapPut("/agents/{agentId}/config", SaveAgentAsync)
            .WithName("SaveAgentConfig")
            .WithTags("Admin Agents");
    }

    private static async Task<Results<Ok<McpConnectionTestResult>, ProblemHttpResult>> TestMcpAsync(
        [FromServices] IMcpConnectionTester tester,
        [FromBody] McpConnectionTestRequest request,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!HasScope(context, "capability.test"))
            return Forbidden(context, "capability.test");
        return TypedResults.Ok(await tester.TestAsync(
            request, context.GetAgentRequest().User, context.GetAgentRequest().TraceId,
            cancellationToken).ConfigureAwait(false));
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

    private static async Task<Results<Ok<RagConfig>, ProblemHttpResult>> GetRagAsync(
        [FromQuery] string agentId,
        [FromServices] ConfigurationService configuration,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!HasScope(context, "agent.config.read"))
            return Forbidden(context, "agent.config.read");
        AgentConfigEntity? entity = await configuration
            .GetAgentAsync(agentId, RequireTenant(context), cancellationToken).ConfigureAwait(false);
        return entity == null ? TypedResults.Problem(AgentProblemDetails.NotFound(
            $"Agent '{agentId}' was not found.", context)) : TypedResults.Ok(new RagConfig
        {
            Enabled = entity.Config.Rag.Enabled,
            EnabledRagInstanceIds = [.. entity.Config.Rag.EnabledRagInstanceIds],
            Instances = entity.Config.Rag.Instances.Select(ConfigurationRedactor.RedactRag).ToList()
        });
    }

    private static async Task<Results<Ok<RagInstanceConfig>, ProblemHttpResult>> SaveRagAsync(
        string id,
        [FromQuery] string agentId,
        [FromBody] RagInstanceConfig instance,
        [FromServices] ConfigurationService configuration,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!HasScope(context, "agent.config.write"))
            return Forbidden(context, "agent.config.write");
        string tenantId = RequireTenant(context);
        AgentConfigEntity? existing = await configuration
            .GetAgentAsync(agentId, tenantId, cancellationToken).ConfigureAwait(false);
        if (existing == null)
            return TypedResults.Problem(AgentProblemDetails.NotFound(
                $"Agent '{agentId}' was not found.", context));
        instance.Id = id;
        RagInstanceConfig? current = existing.Config.Rag.Instances.FirstOrDefault(item =>
            string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
        if (current != null && (string.IsNullOrWhiteSpace(instance.ApiKey)
            || instance.ApiKey.StartsWith("***", StringComparison.Ordinal)))
        {
            instance.ApiKey = current.ApiKey;
            if (string.IsNullOrWhiteSpace(instance.ApiKeySecretRef))
                instance.ApiKeySecretRef = current.ApiKeySecretRef;
        }
        int index = existing.Config.Rag.Instances.FindIndex(item =>
            string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) existing.Config.Rag.Instances[index] = instance;
        else existing.Config.Rag.Instances.Add(instance);
        if (instance.Enabled && !existing.Config.Rag.EnabledRagInstanceIds.Contains(id, StringComparer.OrdinalIgnoreCase))
            existing.Config.Rag.EnabledRagInstanceIds.Add(id);
        AgentConfigEntity? saved = await configuration.SaveAgentAsync(
            agentId, tenantId, existing, context.Request.Headers.IfMatch.FirstOrDefault(), cancellationToken)
            .ConfigureAwait(false);
        return saved == null
            ? TypedResults.Problem(AgentProblemDetails.Conflict(
                "The agent configuration changed concurrently. Retry with a fresh If-Match version.", context))
            : TypedResults.Ok(ConfigurationRedactor.RedactRag(instance));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteRagAsync(
        string id,
        [FromQuery] string agentId,
        [FromServices] ConfigurationService configuration,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!HasScope(context, "agent.config.write"))
            return Forbidden(context, "agent.config.write");
        string tenantId = RequireTenant(context);
        AgentConfigEntity? existing = await configuration
            .GetAgentAsync(agentId, tenantId, cancellationToken).ConfigureAwait(false);
        if (existing == null)
            return TypedResults.Problem(AgentProblemDetails.NotFound(
                $"Agent '{agentId}' was not found.", context));
        int removed = existing.Config.Rag.Instances.RemoveAll(item =>
            string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
            return TypedResults.Problem(AgentProblemDetails.NotFound(
                $"RAG instance '{id}' was not found on agent '{agentId}'.", context));
        existing.Config.Rag.EnabledRagInstanceIds.RemoveAll(instanceId =>
            string.Equals(instanceId, id, StringComparison.OrdinalIgnoreCase));
        AgentConfigEntity? saved = await configuration.SaveAgentAsync(
            agentId, tenantId, existing, context.Request.Headers.IfMatch.FirstOrDefault(), cancellationToken)
            .ConfigureAwait(false);
        return saved == null
            ? TypedResults.Problem(AgentProblemDetails.Conflict(
                "The agent configuration changed concurrently. Retry with a fresh If-Match version.", context))
            : TypedResults.NoContent();
    }

    private static async Task<Results<Ok<RagConnectionTestResult>, ProblemHttpResult>> TestRagAsync(
        [FromServices] IHttpClientFactory httpClientFactory,
        [FromServices] IAgentSecretResolver secrets,
        [FromBody] RagInstanceConfig instance,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!HasScope(context, "capability.test"))
            return Forbidden(context, "capability.test");
        if (string.IsNullOrWhiteSpace(instance.ApiEndpoint))
            return TypedResults.Ok(new RagConnectionTestResult
            {
                Error = "RAG endpoint is required.",
                TraceId = context.GetAgentRequest().TraceId
            });
        if (string.IsNullOrWhiteSpace(instance.ApiKey)
            && !string.IsNullOrWhiteSpace(instance.ApiKeySecretRef))
        {
            instance.ApiKey = await secrets.ResolveAsync(
                RequireTenant(context), instance.ApiKeySecretRef, cancellationToken).ConfigureAwait(false) ?? string.Empty;
        }
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using HttpRequestMessage httpRequest = new(HttpMethod.Get, instance.ApiEndpoint);
            if (!string.IsNullOrWhiteSpace(instance.ApiKey))
                httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", instance.ApiKey);
            using HttpResponseMessage response = await httpClientFactory.CreateClient("AgentLogin")
                .SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            return TypedResults.Ok(new RagConnectionTestResult
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
            return TypedResults.Ok(new RagConnectionTestResult
            {
                Success = false,
                Connected = false,
                LatencyMs = stopwatch.ElapsedMilliseconds,
                Error = exception.Message,
                TraceId = context.GetAgentRequest().TraceId
            });
        }
    }

    private static async Task<Ok<IReadOnlyList<AgentSummary>>> ListAgentsAsync(
        [FromServices] ConfigurationService configuration,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        return TypedResults.Ok(await configuration
            .ListAgentsAsync(RequireTenant(context), cancellationToken)
            .ConfigureAwait(false));
    }

    private static async Task<Results<Ok<AgentConfigEntity>, ProblemHttpResult>> GetAgentAsync(
        string agentId,
        [FromServices] ConfigurationService configuration,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        AgentConfigEntity? entity = await configuration
            .GetAgentAsync(agentId, RequireTenant(context), cancellationToken)
            .ConfigureAwait(false);
        return entity == null
            ? TypedResults.Problem(AgentProblemDetails.NotFound($"Agent '{agentId}' was not found.", context))
            : TypedResults.Ok(ConfigurationRedactor.Redact(entity));
    }

    private static async Task<Results<Ok<IReadOnlyList<LlmProviderProfile>>, ProblemHttpResult>> ListModelsAsync(
        [FromServices] ConfigurationService configuration,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<LlmProviderProfile> profiles = await configuration
            .ListAsync(RequireTenant(context), cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<LlmProviderProfile> redacted = profiles
            .Select(ConfigurationRedactor.RedactLlm).ToList();
        return TypedResults.Ok(redacted);
    }

    private static async Task<Results<Ok<LlmProviderProfile>, ProblemHttpResult>> GetModelAsync(
        string id,
        [FromServices] ConfigurationService configuration,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        LlmProviderProfile? profile = await configuration
            .GetAsync(RequireTenant(context), id, cancellationToken)
            .ConfigureAwait(false);
        return profile == null
            ? TypedResults.Problem(AgentProblemDetails.NotFound($"LLM profile '{id}' was not found.", context))
            : TypedResults.Ok(ConfigurationRedactor.RedactLlm(profile));
    }

    private static async Task<Results<Ok<LlmProviderProfile>, ProblemHttpResult>> SaveModelAsync(
        string id,
        [FromBody] LlmProviderProfile profile,
        [FromServices] ConfigurationService configuration,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(profile.Name)
            || string.IsNullOrWhiteSpace(profile.Endpoint)
            || string.IsNullOrWhiteSpace(profile.ModelId)
            || profile.ContextTokens <= 0
            || !Enum.IsDefined(profile.Modality) || !Enum.IsDefined(profile.Format))
        {
            return TypedResults.Problem(AgentProblemDetails.Invalid(
                "LLM requires id, name, endpoint, modelId and a positive contextTokens value.", context));
        }

        profile.Id = id;
        LlmProviderProfile saved = await configuration
            .SaveLlmAsync(profile, RequireTenant(context), cancellationToken)
            .ConfigureAwait(false);
        return TypedResults.Ok(ConfigurationRedactor.RedactLlm(saved));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteModelAsync(
        string id,
        [FromServices] ConfigurationService configuration,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        return await configuration.DeleteLlmAsync(id, RequireTenant(context), cancellationToken).ConfigureAwait(false)
            ? TypedResults.NoContent()
            : TypedResults.Problem(AgentProblemDetails.NotFound($"LLM profile '{id}' was not found.", context));
    }

    private static async Task<Results<Ok<LlmConnectionTestResult>, ProblemHttpResult>> TestModelAsync(
        [FromServices] IHttpClientFactory httpClientFactory,
        [FromServices] ConfigurationService configuration,
        [FromBody] LlmProviderProfile profile,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(profile.Id)
            && (string.IsNullOrWhiteSpace(profile.ApiKey)
                || profile.ApiKey.StartsWith("***", StringComparison.Ordinal)))
        {
            LlmProviderProfile? stored = await configuration.GetAsync(
                RequireTenant(context),
                profile.Id,
                cancellationToken).ConfigureAwait(false);
            if (stored != null)
            {
                profile.ApiKey = stored.ApiKey;
            }
        }
        string traceId = context.GetAgentRequest().TraceId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(profile.Endpoint))
        {
            return TypedResults.Ok(new LlmConnectionTestResult
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
            if (!endpoint.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
                endpoint += "/models";
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
            return TypedResults.Ok(new LlmConnectionTestResult
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
            return TypedResults.Ok(new LlmConnectionTestResult
            {
                Success = false,
                Connected = false,
                LatencyMs = stopwatch.ElapsedMilliseconds,
                ModelId = profile.ModelId,
                Error = exception.Message,
                TraceId = traceId
            });
        }
    }

    private static async Task<Results<Ok<AgentConfigEntity>, ProblemHttpResult>> SaveAgentAsync(
        [FromServices] ISkillCatalog skillCatalog,
        [FromServices] ConfigurationService configuration,
        string agentId,
        [FromBody] AgentConfigEntity entity,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        string tenantId = RequireTenant(context);
        AgentConfigEntity? existing = await configuration
            .GetAgentAsync(agentId, tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(entity.TenantId)
            && !string.Equals(entity.TenantId, tenantId, StringComparison.Ordinal))
            return Forbidden(context, "agent.config.write");
        entity.TenantId = tenantId;
        entity.Config.TenantId = tenantId;
        foreach (SkillInstanceConfig skill in entity.Config.Skills.Instances)
        {
            if (!string.IsNullOrWhiteSpace(skill.TenantId)
                && !string.Equals(skill.TenantId, tenantId, StringComparison.Ordinal))
                return Forbidden(context, "agent.config.write");
            skill.TenantId = tenantId;
        }
        foreach (string skillId in entity.Config.Skills.EnabledSkills)
        {
            bool isEmbedded = entity.Config.Skills.Instances.Any(skill =>
                string.Equals(skill.Id, skillId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(skill.Name, skillId, StringComparison.OrdinalIgnoreCase));
            if (isEmbedded)
                continue;

            SkillInstanceConfig? packageSkill = await skillCatalog.GetAsync(
                tenantId,
                skillId,
                cancellationToken).ConfigureAwait(false);
            if (packageSkill == null)
                return TypedResults.Problem(AgentProblemDetails.Invalid(
                    $"Skill '{skillId}' is not available to this tenant.", context));
        }
        AgentConfigEntity merged = MergeSecrets(existing, entity);
        AgentConfigEntity? saved = await configuration.SaveAgentAsync(
            agentId,
            tenantId,
            merged,
            context.Request.Headers.IfMatch.FirstOrDefault() ?? entity.CurrentVersion,
            cancellationToken).ConfigureAwait(false);
        return saved == null
            ? TypedResults.Problem(AgentProblemDetails.Conflict(
                "The agent configuration changed concurrently. Retry with a fresh If-Match version.", context))
            : TypedResults.Ok(ConfigurationRedactor.Redact(saved));
    }

    private static ProblemHttpResult Forbidden(HttpContext context, string requiredScope) =>
        TypedResults.Problem(AgentProblemDetails.PermissionDenied(
            $"Access denied due to insufficient permissions. Missing required scope: {requiredScope}.", context));

    private static bool HasScope(HttpContext context, string requiredScope) =>
        context.User.Identity?.IsAuthenticated == true;

    private static string RequireTenant(HttpContext context) =>
        AgentEndpointRequestMapper.RequireTenant(context);

    private static AgentConfigEntity MergeSecrets(
        AgentConfigEntity? existing,
        AgentConfigEntity requested)
    {
        foreach (RagInstanceConfig requestedRag in requested.Config.Rag.Instances)
        {
            RagInstanceConfig? existingRag = existing?.Config.Rag.Instances.FirstOrDefault(item =>
                string.Equals(item.Id, requestedRag.Id, StringComparison.OrdinalIgnoreCase));
            if (existingRag != null && (string.IsNullOrWhiteSpace(requestedRag.ApiKey)
                || requestedRag.ApiKey.StartsWith("***", StringComparison.Ordinal)))
            {
                requestedRag.ApiKey = existingRag.ApiKey;
                if (string.IsNullOrWhiteSpace(requestedRag.ApiKeySecretRef))
                    requestedRag.ApiKeySecretRef = existingRag.ApiKeySecretRef;
            }
        }

        return requested;
    }
}
