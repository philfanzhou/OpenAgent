using Microsoft.AspNetCore.Mvc;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Mcp;
using OpenAgent.Contracts.Models;
using OpenAgent.Contracts.Rag;
using OpenAgent.Contracts.Security;
using OpenAgent.Contracts.Skills;
using OpenAgent.Engine.Config;
using OpenAgent.Engine.Abstractions;
using OpenAgent.Engine.Host.Middleware;
using OpenAgent.Engine.Host.Skills;

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
            if (entity != null && !CanAccessAgent(entity, RequireTenant(context)))
                return Results.Forbid();
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
                || string.IsNullOrWhiteSpace(profile.Endpoint))
            {
                return Results.BadRequest(new { error = "LLM requires id, name and endpoint. Model ID is selected per Agent." });
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
        }).RequireAuthorization(GatewayPermissions.CapabilityTest);

        group.MapPut("/agents/{agentId}/config", async (
            [FromServices] AgentConfigManagementService manager,
            [FromServices] ISkillCatalogStore skillCatalog,
            HttpContext context,
            string agentId,
            [FromBody] AgentConfigEntity entity,
            CancellationToken cancellationToken) =>
        {
            if (!HasScope(context, "agent.config.write"))
                return Results.Forbid();
            string tenantId = RequireTenant(context);
            AgentConfigEntity? existing = await manager.GetAsync(agentId, cancellationToken).ConfigureAwait(false);
            if (existing != null && !CanAccessAgent(existing, tenantId))
                return Results.Forbid();
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
            AgentConfigEntity? saved = await manager.SaveAsync(
                agentId,
                merged,
                context.Request.Headers.IfMatch.FirstOrDefault() ?? entity.CurrentVersion,
                cancellationToken).ConfigureAwait(false);
            return saved == null ? Results.Conflict() : Results.Ok(Redact(saved));
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

        group.MapGet("/mcp", async (
            [FromServices] McpProfileManagementService manager,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            if (!HasScope(context, "agent.config.read"))
                return Results.Forbid();
            IReadOnlyList<McpServerConfig> servers = await manager.ListAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(servers.Select(RedactMcpServer));
        });

        group.MapGet("/mcp/{id}", async (
            [FromServices] McpProfileManagementService manager,
            HttpContext context,
            string id,
            CancellationToken cancellationToken) =>
        {
            if (!HasScope(context, "agent.config.read"))
                return Results.Forbid();
            McpServerConfig? server = await manager.GetAsync(id, cancellationToken).ConfigureAwait(false);
            return server == null ? Results.NotFound() : Results.Ok(RedactMcpServer(server));
        });

        group.MapPut("/mcp/{id}", async (
            [FromServices] McpProfileManagementService manager,
            HttpContext context,
            string id,
            [FromBody] McpServerConfig server,
            CancellationToken cancellationToken) =>
        {
            if (!HasScope(context, "agent.config.write"))
                return Results.Forbid();
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(server.Name))
                return Results.BadRequest(new { error = "MCP requires an id and name." });
            if (string.IsNullOrWhiteSpace(server.Url))
                return Results.BadRequest(new { error = "MCP requires a URL." });

            server.Name = id;
            McpServerConfig saved = await manager.SaveAsync(server, cancellationToken).ConfigureAwait(false);
            return Results.Ok(RedactMcpServer(saved));
        });

        group.MapDelete("/mcp/{id}", async (
            [FromServices] McpProfileManagementService manager,
            HttpContext context,
            string id,
            CancellationToken cancellationToken) =>
        {
            if (!HasScope(context, "agent.config.write"))
                return Results.Forbid();
            return await manager.DeleteAsync(id, cancellationToken).ConfigureAwait(false)
                ? Results.NoContent()
                : Results.NotFound();
        });

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
            if (existing == null)
                return Results.NotFound();

            instance.Id = id;
            RagInstanceConfig? current = existing.Config.Rag.Instances.FirstOrDefault(item =>
                string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            if (current != null && (string.IsNullOrWhiteSpace(instance.ApiKey) || instance.ApiKey.StartsWith("***", StringComparison.Ordinal)))
            {
                instance.ApiKey = current.ApiKey;
            }

            int index = existing.Config.Rag.Instances.FindIndex(item =>
                string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
                existing.Config.Rag.Instances[index] = instance;
            else
                existing.Config.Rag.Instances.Add(instance);
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
            if (existing == null)
                return Results.NotFound();
            int removed = existing.Config.Rag.Instances.RemoveAll(item =>
                string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
                return Results.NotFound();
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
            [FromServices] ISkillCatalogStore catalog,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            if (!HasScope(context, "agent.config.read"))
                return Results.Forbid();
            return Results.Ok(await catalog.ListAsync(
                RequireTenant(context),
                cancellationToken).ConfigureAwait(false));
        });

        group.MapGet("/skills/{skillId}/source", async (
            [FromServices] SkillPackageManagementService packages,
            HttpContext context,
            string skillId,
            CancellationToken cancellationToken) =>
        {
            if (!HasScope(context, "agent.config.read"))
                return Results.Forbid();
            string? markdown = await packages.ReadMarkdownAsync(
                RequireTenant(context),
                skillId,
                cancellationToken).ConfigureAwait(false);
            return markdown == null ? Results.NotFound() : Results.Ok(new { markdown });
        });

        group.MapGet("/skills/{skillId}", async (
            [FromServices] ISkillCatalogStore catalog,
            HttpContext context,
            string skillId,
            CancellationToken cancellationToken) =>
        {
            if (!HasScope(context, "agent.config.read"))
                return Results.Forbid();
            SkillInstanceConfig? skill = await catalog.GetAsync(
                RequireTenant(context),
                skillId,
                cancellationToken).ConfigureAwait(false);
            return skill == null ? Results.NotFound() : Results.Ok(skill);
        });

        group.MapPost("/skills/packages", async (
            [FromServices] SkillPackageManagementService packages,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            if (!HasScope(context, "agent.config.write"))
                return Results.Forbid();
            if (!context.Request.HasFormContentType)
                return Results.BadRequest(new { error = "A multipart .zip or .md Skill file is required." });

            IFormCollection form = await context.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
            IFormFile? file = form.Files.GetFile("file");
            if (file == null)
                return Results.BadRequest(new { error = "The multipart field 'file' is required." });
            if (file.Length > SkillPackageManagementService.MaxPackageBytes)
                return Results.BadRequest(new { error = "Skill package exceeds the 4 MB limit." });

            try
            {
                await using Stream stream = file.OpenReadStream();
                SkillPackageUploadResult result = await packages.UploadAsync(
                    context.GetAgentRequest().User.TenantId ?? "default",
                    context.GetAgentRequest().User.UserId,
                    Path.GetFileName(file.FileName),
                    string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
                    stream,
                    cancellationToken).ConfigureAwait(false);
                return Results.Ok(new { skill = result.Skill, storage = "object-storage-directory" });
            }
            catch (InvalidOperationException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        group.MapDelete("/skills/{skillId}", async (
            [FromServices] SkillPackageManagementService packages,
            HttpContext context,
            string skillId,
            CancellationToken cancellationToken) =>
        {
            if (!HasScope(context, "agent.config.write"))
                return Results.Forbid();
            return await packages.DeleteCatalogAsync(
                RequireTenant(context),
                skillId,
                cancellationToken).ConfigureAwait(false)
                ? Results.NoContent()
                : Results.NotFound();
        });

        group.MapPost("/skills/{agentId}/packages", async (
            [FromServices] SkillPackageManagementService packages,
            HttpContext context,
            string agentId,
            CancellationToken cancellationToken) =>
        {
            if (!HasScope(context, "agent.config.write"))
                return Results.Forbid();
            if (!context.Request.HasFormContentType)
                return Results.BadRequest(new { error = "A multipart .zip or .md Skill file is required." });

            IFormCollection form = await context.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
            IFormFile? file = form.Files.GetFile("file");
            if (file == null)
                return Results.BadRequest(new { error = "The multipart field 'file' is required." });
            string extension = Path.GetExtension(file.FileName);
            if (!string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { error = "Skill upload must be a .zip package or a single .md file." });
            }
            if (file.Length > SkillPackageManagementService.MaxPackageBytes)
                return Results.BadRequest(new { error = "Skill package exceeds the 4 MB limit." });

            SkillPackageInstallResult result;
            try
            {
                await using Stream stream = file.OpenReadStream();
                result = await packages.InstallAsync(
                    agentId,
                    context.GetAgentRequest().User.TenantId ?? "default",
                    context.GetAgentRequest().User.UserId,
                    Path.GetFileName(file.FileName),
                    string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
                    stream,
                    context.Request.Headers.IfMatch.FirstOrDefault(),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
            if (!result.AgentExists)
                return Results.NotFound();
            if (result.HasTenantMismatch)
                return Results.Forbid();
            if (result.HasConflict)
                return Results.Conflict();
            return Results.Ok(new
            {
                skill = result.Skill,
                currentVersion = result.CurrentVersion,
                storage = "object-storage"
            });
        });

        group.MapDelete("/skills/{agentId}/{skillId}", async (
            [FromServices] SkillPackageManagementService packages,
            HttpContext context,
            string agentId,
            string skillId,
            CancellationToken cancellationToken) =>
        {
            if (!HasScope(context, "agent.config.write"))
                return Results.Forbid();
            SkillPackageDeleteResult result = await packages.DeleteAsync(
                agentId,
                RequireTenant(context),
                skillId,
                context.Request.Headers.IfMatch.FirstOrDefault(),
                cancellationToken).ConfigureAwait(false);
            return result switch
            {
                SkillPackageDeleteResult.Deleted => Results.NoContent(),
                SkillPackageDeleteResult.Conflict => Results.Conflict(),
                SkillPackageDeleteResult.TenantMismatch => Results.Forbid(),
                _ => Results.NotFound()
            };
        });

        group.MapPost("/skills/test", async (
            [FromServices] SkillPackageManagementService packages,
            [FromBody] SkillsConfig skills,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            if (!HasScope(context, "capability.test"))
                return Results.Forbid();
            SkillPackageValidationResult result = await packages
                .ValidateAsync(RequireTenant(context), skills, cancellationToken)
                .ConfigureAwait(false);
            return Results.Ok(result);
        });

        return group;
    }

    private static bool HasScope(HttpContext context, string requiredScope)
    {
        return context.User.Identity?.IsAuthenticated == true;
    }

    private static string RequireTenant(HttpContext context) =>
        context.GetAgentRequest().User.TenantId
        ?? throw new InvalidOperationException("TenantId is required.");

    private static bool CanAccessAgent(AgentConfigEntity entity, string tenantId) =>
        string.Equals(entity.TenantId, tenantId, StringComparison.Ordinal)
        || string.IsNullOrWhiteSpace(entity.TenantId)
            && entity.Config.Skills.EnabledSkills.Count == 0
            && entity.Config.Skills.Instances.Count == 0;

    private static AgentConfigEntity MergeSecrets(AgentConfigEntity? existing, AgentConfigEntity requested)
    {
        if (existing == null)
            return requested;
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
        EnabledServerIds = [.. config.EnabledServerIds],
        Servers = config.Servers.Select(RedactMcpServer).ToList()
    };

    private static McpServerConfig RedactMcpServer(McpServerConfig server) => new()
    {
        Name = server.Name,
        Url = server.Url,
        Type = server.Type,
        ProtocolVersion = server.ProtocolVersion
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
