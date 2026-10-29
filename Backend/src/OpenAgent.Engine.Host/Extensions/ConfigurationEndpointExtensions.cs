using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using OpenAgent.Contracts.Capabilities;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Models;
using OpenAgent.Contracts.Rag;
using OpenAgent.Engine.Config;
using OpenAgent.Engine.Host.Endpoints;
using OpenAgent.Engine.Host.Endpoints.Admin;
using OpenAgent.Engine.Host.Endpoints.Admin.Agents;
using OpenAgent.Engine.Host.Endpoints.Admin.Models;
using OpenAgent.Engine.Host.Endpoints.Admin.Mcp;
using OpenAgent.Engine.Host.Middleware;
using OpenAgent.Engine.Hosting.Errors;

namespace OpenAgent.Engine.Host.Extensions;

/// <summary>
/// /api/v1/admin 下的配置管理端点汇总门面（MCP / RAG / LLM / Agent 配置），统一迁移为
/// Minimal API + TypedResults。仅开发环境映射。MCP/LLM/Agent 子域归
/// Endpoints/Admin/{Mcp,Models,Agents}；RAG 端点按任务范围限制保持现状，留在本文件。
/// </summary>
internal static class ConfigurationEndpointExtensions
{
    public static IEndpointConventionBuilder MapConfigurationEndpoints(
        this IEndpointRouteBuilder endpoints,
        string pattern = "/api/v1/admin")
    {
        RouteGroupBuilder group = endpoints.MapGroup(pattern).RequireAuthorization();

        group.MapMcpConfigurationEndpoints();
        MapRagEndpoints(group);
        group.MapLlmConfigurationEndpoints();
        group.MapAgentConfigurationEndpoints();

        return group;
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

    private static ProblemHttpResult Forbidden(HttpContext context, string requiredScope) =>
        TypedResults.Problem(AgentProblemDetails.PermissionDenied(
            $"Access denied due to insufficient permissions. Missing required scope: {requiredScope}.", context));

    private static bool HasScope(HttpContext context, string requiredScope) =>
        context.User.Identity?.IsAuthenticated == true;

    private static string RequireTenant(HttpContext context) =>
        AgentEndpointRequestMapper.RequireTenant(context);
}
