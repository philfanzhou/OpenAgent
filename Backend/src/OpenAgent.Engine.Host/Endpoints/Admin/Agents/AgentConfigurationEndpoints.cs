using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using OpenAgent.Contracts.Security;
using OpenAgent.Contracts.Capabilities;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Models;
using OpenAgent.Engine.Config;
using OpenAgent.Engine.Host.Endpoints;
using OpenAgent.Engine.Host.Middleware;
using OpenAgent.Engine.Hosting.Errors;

namespace OpenAgent.Engine.Host.Endpoints.Admin.Agents;

internal static class AgentConfigurationEndpoints
{
    internal static void MapAgentConfigurationEndpoints(this RouteGroupBuilder group)
    {
        group = group.MapGroup(string.Empty).AddEndpointFilter<ConfigurationWriteFilter>();
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

    private static async Task<Ok<IReadOnlyList<AgentSummary>>> ListAgentsAsync(
        [FromServices] ConfigurationService configuration,
        [FromServices] IResourceAuthorization authorization,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var items = await configuration
            .ListAgentsAsync(RequireTenant(context), cancellationToken)
            .ConfigureAwait(false);
        return TypedResults.Ok(await ResourceAccess.FilterAsync(items, item => item.AgentId, AgentResourceType.Agent, "execute", authorization, context, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<Results<Ok<AgentConfigEntity>, ProblemHttpResult>> GetAgentAsync(
        string agentId,
        [FromServices] ConfigurationService configuration,
        [FromServices] IResourceAuthorization authorization,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!await authorization.IsAuthorizedAsync(new(agentId, AgentResourceType.Agent, agentId, "execute"),
                context.GetAgentRequest().User, cancellationToken).ConfigureAwait(false))
            return TypedResults.Problem(AgentProblemDetails.PermissionDenied("Resource access denied.", context));
        AgentConfigEntity? entity = await configuration
            .GetAgentAsync(agentId, RequireTenant(context), cancellationToken)
            .ConfigureAwait(false);
        return entity == null
            ? TypedResults.Problem(AgentProblemDetails.NotFound($"Agent '{agentId}' was not found.", context))
            : TypedResults.Ok(ConfigurationRedactor.Redact(entity));
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
        // OpenCode 内嵌模型的密钥在响应中已脱敏：保存时空值/掩码不覆盖已存密钥。
        if (entity.Config.OpenCode.Model is { } openCodeModel
            && existing?.Config.OpenCode.Model is { } storedModel
            && string.IsNullOrWhiteSpace(openCodeModel.ApiKey))
        {
            openCodeModel.ApiKey = storedModel.ApiKey;
        }
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
