using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using OpenAgent.Contracts.Security;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Models;
using OpenAgent.Engine.Config;
using OpenAgent.Engine.Host.Endpoints;
using OpenAgent.Engine.Host.Middleware;
using OpenAgent.Engine.Hosting.Errors;

namespace OpenAgent.Engine.Host.Endpoints.Admin.Models;

internal static class LlmConfigurationEndpoints
{
    internal static void MapLlmConfigurationEndpoints(this RouteGroupBuilder group)
    {
        group = group.MapGroup(string.Empty).AddEndpointFilter<ConfigurationWriteFilter>();
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

    private static async Task<Results<Ok<IReadOnlyList<LlmProviderProfile>>, ProblemHttpResult>> ListModelsAsync(
        [FromServices] ConfigurationService configuration,
        [FromServices] IResourceAuthorization authorization,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<LlmProviderProfile> profiles = await configuration
            .ListAsync(RequireTenant(context), cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<LlmProviderProfile> redacted = profiles
            .Select(ConfigurationRedactor.RedactLlm).ToList();
        return TypedResults.Ok(await ResourceAccess.FilterAsync(redacted, item => item.Id, AgentResourceType.Model, "invoke", authorization, context, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<Results<Ok<LlmProviderProfile>, ProblemHttpResult>> GetModelAsync(
        string id,
        [FromServices] ConfigurationService configuration,
        [FromServices] IResourceAuthorization authorization,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!await authorization.IsAuthorizedAsync(new(string.Empty, AgentResourceType.Model, id, "invoke"),
                context.GetAgentRequest().User, cancellationToken).ConfigureAwait(false))
            return TypedResults.Problem(AgentProblemDetails.PermissionDenied("Resource access denied.", context));
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
        if (string.IsNullOrWhiteSpace(id) || id.StartsWith("@agent/", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(profile.Name)
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
        [FromServices] IResourceAuthorization authorization,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(profile.Id)
            && !await authorization.IsAuthorizedAsync(new(string.Empty, AgentResourceType.Model, profile.Id, "invoke"),
                context.GetAgentRequest().User, cancellationToken).ConfigureAwait(false))
            return TypedResults.Problem(AgentProblemDetails.PermissionDenied("Resource access denied.", context));
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

    private static string RequireTenant(HttpContext context) =>
        AgentEndpointRequestMapper.RequireTenant(context);
}
