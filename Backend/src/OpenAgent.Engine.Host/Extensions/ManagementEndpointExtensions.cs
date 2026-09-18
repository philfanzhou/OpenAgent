using Microsoft.AspNetCore.Mvc;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Models;
using OpenAgent.Contracts.Security;
using OpenAgent.Contracts.Skills;
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

        group.MapPut("/skills/{skillId}/source", async (
            [FromServices] SkillPackageManagementService packages,
            [FromBody] SkillMarkdownUpdateRequest request,
            HttpContext context,
            string skillId,
            CancellationToken cancellationToken) =>
        {
            if (!HasScope(context, "agent.config.write"))
                return Results.Forbid();
            try
            {
                SkillInstanceConfig? skill = await packages.UpdateMarkdownAsync(
                    RequireTenant(context),
                    context.GetAgentRequest().User.UserId,
                    skillId,
                    request.Markdown,
                    cancellationToken).ConfigureAwait(false);
                return skill == null ? Results.NotFound() : Results.Ok(skill);
            }
            catch (InvalidOperationException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
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
            if (!TryReadScriptExecutionFlag(form, out bool? scriptExecutionEnabled, out string? scriptFlagError))
                return Results.BadRequest(new { error = scriptFlagError });

            try
            {
                await using Stream stream = file.OpenReadStream();
                SkillPackageUploadResult result = await packages.UploadAsync(
                    context.GetAgentRequest().User.TenantId ?? "default",
                    context.GetAgentRequest().User.UserId,
                    Path.GetFileName(file.FileName),
                    string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
                    stream,
                    cancellationToken,
                    scriptExecutionEnabled: scriptExecutionEnabled).ConfigureAwait(false);
                return Results.Ok(new { skill = result.Skill, storage = "object-storage-directory" });
            }
            catch (InvalidOperationException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        group.MapPatch("/skills/{skillId}", async (
            [FromServices] SkillPackageManagementService packages,
            [FromBody] SkillScriptSettingsRequest request,
            HttpContext context,
            string skillId,
            CancellationToken cancellationToken) =>
        {
            if (!HasScope(context, "agent.config.write"))
                return Results.Forbid();
            try
            {
                SkillInstanceConfig? skill = await packages.UpdateScriptExecutionAsync(
                    RequireTenant(context),
                    skillId,
                    request.ScriptExecutionEnabled,
                    cancellationToken).ConfigureAwait(false);
                return skill == null ? Results.NotFound() : Results.Ok(skill);
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
            if (!TryReadScriptExecutionFlag(form, out bool? scriptExecutionEnabled, out string? scriptFlagError))
                return Results.BadRequest(new { error = scriptFlagError });

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
                    cancellationToken,
                    scriptExecutionEnabled: scriptExecutionEnabled).ConfigureAwait(false);
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

    /// <summary>
    /// Reads the optional "scriptExecutionEnabled" multipart field. Absent means
    /// "default off"; a present-but-invalid value is rejected so callers notice
    /// typos instead of silently uploading with scripts disabled.
    /// </summary>
    private static bool TryReadScriptExecutionFlag(
        IFormCollection form,
        out bool? scriptExecutionEnabled,
        out string? error)
    {
        scriptExecutionEnabled = null;
        error = null;
        string? raw = form["scriptExecutionEnabled"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (bool.TryParse(raw, out bool parsed))
        {
            scriptExecutionEnabled = parsed;
            return true;
        }

        error = "The 'scriptExecutionEnabled' field must be true or false.";
        return false;
    }

    private static string RequireTenant(HttpContext context) =>
        context.GetAgentRequest().User.TenantId
        ?? throw new InvalidOperationException("TenantId is required.");

}
