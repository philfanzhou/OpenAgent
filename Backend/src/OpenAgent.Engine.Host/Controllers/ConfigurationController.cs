using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Models;
using OpenAgent.Core.Abstract;
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
        [FromBody] LlmConnectionTestRequest request,
        CancellationToken cancellationToken)
    {
        LlmProviderProfile profile = request.Profile;
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
        if (HasInlineSecrets(entity))
            return Results.BadRequest(new { error = "Inline API keys are not persisted. Configure apiKeySecretRef instead." });
        AgentConfigEntity merged = MergeSecretReferences(existing, entity);
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

    private static bool HasInlineSecrets(AgentConfigEntity entity) =>
        entity.Config.Rag.Instances.Any(instance => IsInlineSecret(instance.ApiKey));

    private static bool IsInlineSecret(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !value.StartsWith("***", StringComparison.Ordinal);

    private static AgentConfigEntity MergeSecretReferences(
        AgentConfigEntity? existing,
        AgentConfigEntity requested)
    {
        foreach (RagInstanceConfig requestedRag in requested.Config.Rag.Instances)
        {
            RagInstanceConfig? existingRag = existing?.Config.Rag.Instances.FirstOrDefault(item =>
                string.Equals(item.Id, requestedRag.Id, StringComparison.OrdinalIgnoreCase));
            if (existingRag != null && string.IsNullOrWhiteSpace(requestedRag.ApiKeySecretRef))
            {
                requestedRag.ApiKeySecretRef = existingRag.ApiKeySecretRef;
            }
            requestedRag.ApiKey = string.Empty;
        }

        return requested;
    }
}
