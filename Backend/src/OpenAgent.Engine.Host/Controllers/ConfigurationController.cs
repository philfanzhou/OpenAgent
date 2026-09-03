using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Mcp;
using OpenAgent.Contracts.Models;
using OpenAgent.Contracts.Rag;
using OpenAgent.Core.Abstract;
using OpenAgent.Engine.Abstractions;
using OpenAgent.Engine.Config;
using OpenAgent.Engine.Host.Extensions;
using OpenAgent.Engine.Host.Middleware;
using static OpenAgent.Engine.Host.Controllers.ConfigurationRedactor;

namespace OpenAgent.Engine.Host.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/admin")]
public sealed class ConfigurationController(ConfigurationService configuration) : ControllerBase
{
    [HttpPost("mcp/test-connection")]
    public async Task<IResult> TestMcpAsync(
        [FromServices] IMcpConnectionTester tester,
        [FromBody] McpConnectionTestRequest request,
        CancellationToken cancellationToken)
    {
        if (!HasScope("capability.test")) return Results.Forbid();
        return Results.Ok(await tester.TestAsync(
            request, HttpContext.GetAgentRequest().User, HttpContext.GetAgentRequest().TraceId,
            cancellationToken).ConfigureAwait(false));
    }

    [HttpGet("mcp")]
    public async Task<IResult> ListMcpAsync(
        [FromServices] McpProfileManagementService mcpProfiles,
        CancellationToken cancellationToken)
    {
        if (!HasScope("agent.config.read")) return Results.Forbid();
        IReadOnlyList<McpServerConfig> servers = await mcpProfiles
            .ListAsync(RequireTenant(HttpContext), cancellationToken).ConfigureAwait(false);
        return Results.Ok(servers.Select(RedactMcpServer));
    }

    [HttpGet("mcp/{id}")]
    public async Task<IResult> GetMcpAsync(
        string id, [FromServices] McpProfileManagementService mcpProfiles, CancellationToken cancellationToken)
    {
        if (!HasScope("agent.config.read")) return Results.Forbid();
        McpServerConfig? server = await mcpProfiles
            .GetAsync(id, RequireTenant(HttpContext), cancellationToken).ConfigureAwait(false);
        return server == null ? Results.NotFound() : Results.Ok(RedactMcpServer(server));
    }

    [HttpPut("mcp/{id}")]
    public async Task<IResult> SaveMcpAsync(
        string id, [FromBody] McpServerConfig server,
        [FromServices] McpProfileManagementService mcpProfiles, CancellationToken cancellationToken)
    {
        if (!HasScope("agent.config.write")) return Results.Forbid();
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(server.Name))
            return Results.BadRequest(new { error = "MCP requires an id and name." });
        if (string.IsNullOrWhiteSpace(server.Url))
            return Results.BadRequest(new { error = "MCP requires a URL." });
        server.Name = id;
        McpServerConfig saved = await mcpProfiles
            .SaveAsync(server, RequireTenant(HttpContext), cancellationToken).ConfigureAwait(false);
        return Results.Ok(RedactMcpServer(saved));
    }

    [HttpDelete("mcp/{id}")]
    public async Task<IResult> DeleteMcpAsync(
        string id, [FromServices] McpProfileManagementService mcpProfiles, CancellationToken cancellationToken)
    {
        if (!HasScope("agent.config.write")) return Results.Forbid();
        return await mcpProfiles.DeleteAsync(id, RequireTenant(HttpContext), cancellationToken).ConfigureAwait(false)
            ? Results.NoContent() : Results.NotFound();
    }

    [HttpGet("rag")]
    public async Task<IResult> GetRagAsync([FromQuery] string agentId, CancellationToken cancellationToken)
    {
        if (!HasScope("agent.config.read")) return Results.Forbid();
        AgentConfigEntity? entity = await configuration
            .GetAgentAsync(agentId, RequireTenant(HttpContext), cancellationToken).ConfigureAwait(false);
        return entity == null ? Results.NotFound() : Results.Ok(new RagConfig
        {
            Enabled = entity.Config.Rag.Enabled,
            EnabledRagInstanceIds = [.. entity.Config.Rag.EnabledRagInstanceIds],
            Instances = entity.Config.Rag.Instances.Select(RedactRag).ToList()
        });
    }

    [HttpPut("rag/{id}")]
    public async Task<IResult> SaveRagAsync(
        string id, [FromQuery] string agentId, [FromBody] RagInstanceConfig instance,
        CancellationToken cancellationToken)
    {
        if (!HasScope("agent.config.write")) return Results.Forbid();
        string tenantId = RequireTenant(HttpContext);
        AgentConfigEntity? existing = await configuration
            .GetAgentAsync(agentId, tenantId, cancellationToken).ConfigureAwait(false);
        if (existing == null) return Results.NotFound();
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
            agentId, tenantId, existing, HttpContext.Request.Headers.IfMatch.FirstOrDefault(), cancellationToken)
            .ConfigureAwait(false);
        return saved == null ? Results.Conflict() : Results.Ok(RedactRag(instance));
    }

    [HttpDelete("rag/{id}")]
    public async Task<IResult> DeleteRagAsync(
        string id, [FromQuery] string agentId, CancellationToken cancellationToken)
    {
        if (!HasScope("agent.config.write")) return Results.Forbid();
        string tenantId = RequireTenant(HttpContext);
        AgentConfigEntity? existing = await configuration
            .GetAgentAsync(agentId, tenantId, cancellationToken).ConfigureAwait(false);
        if (existing == null) return Results.NotFound();
        int removed = existing.Config.Rag.Instances.RemoveAll(item =>
            string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) return Results.NotFound();
        existing.Config.Rag.EnabledRagInstanceIds.RemoveAll(item =>
            string.Equals(item, id, StringComparison.OrdinalIgnoreCase));
        AgentConfigEntity? saved = await configuration.SaveAgentAsync(
            agentId, tenantId, existing, HttpContext.Request.Headers.IfMatch.FirstOrDefault(), cancellationToken)
            .ConfigureAwait(false);
        return saved == null ? Results.Conflict() : Results.NoContent();
    }

    [HttpPost("rag/test-connection")]
    public async Task<IResult> TestRagAsync(
        [FromServices] IHttpClientFactory httpClientFactory,
        [FromServices] IAgentSecretResolver secrets,
        [FromBody] RagInstanceConfig instance,
        CancellationToken cancellationToken)
    {
        if (!HasScope("capability.test")) return Results.Forbid();
        if (string.IsNullOrWhiteSpace(instance.ApiEndpoint))
            return Results.Ok(new RagConnectionTestResult { Error = "RAG endpoint is required.", TraceId = HttpContext.GetAgentRequest().TraceId });
        if (string.IsNullOrWhiteSpace(instance.ApiKey)
            && !string.IsNullOrWhiteSpace(instance.ApiKeySecretRef))
        {
            instance.ApiKey = await secrets.ResolveAsync(
                RequireTenant(HttpContext), instance.ApiKeySecretRef, cancellationToken).ConfigureAwait(false) ?? string.Empty;
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
            return Results.Ok(new RagConnectionTestResult
            {
                Success = response.IsSuccessStatusCode, Connected = true, StatusCode = (int)response.StatusCode,
                LatencyMs = stopwatch.ElapsedMilliseconds,
                Error = response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}",
                TraceId = HttpContext.GetAgentRequest().TraceId
            });
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            stopwatch.Stop();
            return Results.Ok(new RagConnectionTestResult
            {
                Success = false, Connected = false, LatencyMs = stopwatch.ElapsedMilliseconds,
                Error = exception.Message, TraceId = HttpContext.GetAgentRequest().TraceId
            });
        }
    }
    [HttpGet("agents")]
    public async Task<IResult> ListAgentsAsync(
        CancellationToken cancellationToken)
    {
        return Results.Ok(await configuration
            .ListAgentsAsync(RequireTenant(HttpContext), cancellationToken)
            .ConfigureAwait(false));
    }

    [HttpGet("agents/{agentId}")]
    public async Task<IResult> GetAgentAsync(
        string agentId,
        CancellationToken cancellationToken)
    {
        AgentConfigEntity? entity = await configuration
            .GetAgentAsync(agentId, RequireTenant(HttpContext), cancellationToken)
            .ConfigureAwait(false);
        return entity == null ? Results.NotFound() : Results.Ok(Redact(entity));
    }

    [HttpGet("llm")]
    public async Task<IResult> ListModelsAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyList<LlmProviderProfile> profiles = await configuration
            .ListAsync(RequireTenant(HttpContext), cancellationToken)
            .ConfigureAwait(false);
        return Results.Ok(profiles.Select(RedactLlm));
    }

    [HttpGet("llm/{id}")]
    public async Task<IResult> GetModelAsync(
        string id,
        CancellationToken cancellationToken)
    {
        LlmProviderProfile? profile = await configuration
            .GetAsync(RequireTenant(HttpContext), id, cancellationToken)
            .ConfigureAwait(false);
        return profile == null ? Results.NotFound() : Results.Ok(RedactLlm(profile));
    }

    [HttpPut("llm/{id}")]
    public async Task<IResult> SaveModelAsync(
        string id,
        [FromBody] LlmProviderProfile profile,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(profile.Name)
            || string.IsNullOrWhiteSpace(profile.Endpoint)
            || string.IsNullOrWhiteSpace(profile.ModelId)
            || profile.ContextTokens <= 0
            || !Enum.IsDefined(profile.Modality) || !Enum.IsDefined(profile.Format))
        {
            return Results.BadRequest(new
            {
                error = "LLM requires id, name, endpoint, modelId and a positive contextTokens value."
            });
        }

        profile.Id = id;
        LlmProviderProfile saved = await configuration
            .SaveLlmAsync(profile, RequireTenant(HttpContext), cancellationToken)
            .ConfigureAwait(false);
        return Results.Ok(RedactLlm(saved));
    }

    [HttpDelete("llm/{id}")]
    public async Task<IResult> DeleteModelAsync(
        string id,
        CancellationToken cancellationToken)
    {
        return await configuration.DeleteLlmAsync(id, RequireTenant(HttpContext), cancellationToken).ConfigureAwait(false)
            ? Results.NoContent()
            : Results.NotFound();
    }

    [HttpPost("llm/test-connection")]
    public async Task<IResult> TestModelAsync(
        [FromServices] IHttpClientFactory httpClientFactory,
        [FromBody] LlmProviderProfile profile,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(profile.Id)
            && (string.IsNullOrWhiteSpace(profile.ApiKey)
                || profile.ApiKey.StartsWith("***", StringComparison.Ordinal)))
        {
            LlmProviderProfile? stored = await configuration.GetAsync(
                RequireTenant(HttpContext),
                profile.Id,
                cancellationToken).ConfigureAwait(false);
            if (stored != null)
            {
                profile.ApiKey = stored.ApiKey;
            }
        }
        string traceId = HttpContext.GetAgentRequest().TraceId ?? string.Empty;
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
    }

    [HttpPut("agents/{agentId}/config")]
    public async Task<IResult> SaveAgentAsync(
        [FromServices] ISkillCatalog skillCatalog,
        string agentId,
        [FromBody] AgentConfigEntity entity,
        CancellationToken cancellationToken)
    {
        string tenantId = RequireTenant(HttpContext);
        AgentConfigEntity? existing = await configuration
            .GetAgentAsync(agentId, tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(entity.TenantId)
            && !string.Equals(entity.TenantId, tenantId, StringComparison.Ordinal))
            return Results.Forbid();
        entity.TenantId = tenantId;
        entity.Config.TenantId = tenantId;
        foreach (SkillInstanceConfig skill in entity.Config.Skills.Instances)
        {
            if (!string.IsNullOrWhiteSpace(skill.TenantId)
                && !string.Equals(skill.TenantId, tenantId, StringComparison.Ordinal))
                return Results.Forbid();
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
                return Results.BadRequest(new { error = $"Skill '{skillId}' is not available to this tenant." });
        }
        AgentConfigEntity merged = MergeSecrets(existing, entity);
        AgentConfigEntity? saved = await configuration.SaveAgentAsync(
            agentId,
            tenantId,
            merged,
            HttpContext.Request.Headers.IfMatch.FirstOrDefault() ?? entity.CurrentVersion,
            cancellationToken).ConfigureAwait(false);
        return saved == null ? Results.Conflict() : Results.Ok(Redact(saved));
    }

    private static string RequireTenant(HttpContext context) =>
        AgentEndpointRequestMapper.RequireTenant(context);

    private bool HasScope(string requiredScope) =>
        User.Identity?.IsAuthenticated == true;

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
