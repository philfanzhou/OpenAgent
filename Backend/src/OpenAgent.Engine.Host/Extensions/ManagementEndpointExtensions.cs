using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Responses;
using OpenAgent.Contracts.Security;
using OpenAgent.Contracts.Skills;
using OpenAgent.Engine.Abstractions;
using OpenAgent.Engine.Host.Middleware;
using OpenAgent.Engine.Host.Skills;
using OpenAgent.Hosting.Errors;

namespace OpenAgent.Engine.Host.Extensions;

internal static class ManagementEndpointExtensions
{
    public static IEndpointConventionBuilder MapManagementEndpoints(
        this IEndpointRouteBuilder endpoints,
        string pattern = "/api/v1/admin")
    {
        RouteGroupBuilder group = endpoints.MapGroup(pattern).RequireAuthorization();

        group.MapGet("/skills", ListSkillsAsync)
            .WithName("ListSkills")
            .WithTags("Admin Skills")
            .WithSummary("列出租户技能目录");

        group.MapGet("/skills/{skillId}/source", ReadSkillSourceAsync)
            .WithName("ReadSkillSource")
            .WithTags("Admin Skills")
            .WithSummary("读取技能 Markdown 源码");

        group.MapPut("/skills/{skillId}/source", UpdateSkillSourceAsync)
            .WithName("UpdateSkillSource")
            .WithTags("Admin Skills")
            .WithSummary("更新技能 Markdown 源码");

        group.MapGet("/skills/{skillId}", GetSkillAsync)
            .WithName("GetSkill")
            .WithTags("Admin Skills")
            .WithSummary("获取单个技能配置");

        group.MapPost("/skills/packages", UploadSkillPackageAsync)
            .DisableAntiforgery()
            .WithName("UploadSkillPackage")
            .WithTags("Admin Skills")
            .WithSummary("上传技能包到租户目录");

        group.MapPatch("/skills/{skillId}", UpdateSkillScriptExecutionAsync)
            .WithName("UpdateSkillScriptExecution")
            .WithTags("Admin Skills")
            .WithSummary("更新技能脚本执行开关");

        group.MapDelete("/skills/{skillId}", DeleteSkillAsync)
            .WithName("DeleteSkill")
            .WithTags("Admin Skills")
            .WithSummary("从租户目录删除技能");

        group.MapPost("/skills/{agentId}/packages", InstallSkillPackageAsync)
            .DisableAntiforgery()
            .WithName("InstallSkillPackage")
            .WithTags("Admin Skills")
            .WithSummary("为指定 Agent 安装技能包");

        group.MapDelete("/skills/{agentId}/{skillId}", DeleteAgentSkillAsync)
            .WithName("DeleteAgentSkill")
            .WithTags("Admin Skills")
            .WithSummary("从 Agent 卸载技能");

        group.MapPost("/skills/test", ValidateSkillsAsync)
            .WithName("ValidateSkills")
            .WithTags("Admin Skills")
            .WithSummary("校验技能配置");

        return group;
    }

    private static async Task<Results<Ok<IReadOnlyList<SkillInstanceConfig>>, ProblemHttpResult>> ListSkillsAsync(
        [FromServices] ISkillCatalogStore catalog,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!HasScope(context, "agent.config.read"))
            return Forbidden(context, "agent.config.read");
        return TypedResults.Ok(await catalog.ListAsync(
            RequireTenant(context),
            cancellationToken).ConfigureAwait(false));
    }

    private static async Task<Results<Ok<SkillMarkdownResponse>, ProblemHttpResult>> ReadSkillSourceAsync(
        [FromServices] SkillPackageManagementService packages,
        HttpContext context,
        string skillId,
        CancellationToken cancellationToken)
    {
        if (!HasScope(context, "agent.config.read"))
            return Forbidden(context, "agent.config.read");
        string? markdown = await packages.ReadMarkdownAsync(
            RequireTenant(context),
            skillId,
            cancellationToken).ConfigureAwait(false);
        return markdown == null
            ? TypedResults.Problem(AgentProblemDetails.NotFound($"Skill '{skillId}' was not found.", context))
            : TypedResults.Ok(new SkillMarkdownResponse { Markdown = markdown });
    }

    private static async Task<Results<Ok<SkillInstanceConfig>, ProblemHttpResult>> UpdateSkillSourceAsync(
        [FromServices] SkillPackageManagementService packages,
        [FromBody] SkillMarkdownUpdateRequest request,
        HttpContext context,
        string skillId,
        CancellationToken cancellationToken)
    {
        if (!HasScope(context, "agent.config.write"))
            return Forbidden(context, "agent.config.write");
        try
        {
            SkillInstanceConfig? skill = await packages.UpdateMarkdownAsync(
                RequireTenant(context),
                context.GetAgentRequest().User.UserId,
                skillId,
                request.Markdown,
                cancellationToken).ConfigureAwait(false);
            return skill == null
                ? TypedResults.Problem(AgentProblemDetails.NotFound($"Skill '{skillId}' was not found.", context))
                : TypedResults.Ok(skill);
        }
        catch (InvalidOperationException exception)
        {
            return TypedResults.Problem(AgentProblemDetails.Invalid(exception.Message, context));
        }
    }

    private static async Task<Results<Ok<SkillInstanceConfig>, ProblemHttpResult>> GetSkillAsync(
        [FromServices] ISkillCatalogStore catalog,
        HttpContext context,
        string skillId,
        CancellationToken cancellationToken)
    {
        if (!HasScope(context, "agent.config.read"))
            return Forbidden(context, "agent.config.read");
        SkillInstanceConfig? skill = await catalog.GetAsync(
            RequireTenant(context),
            skillId,
            cancellationToken).ConfigureAwait(false);
        return skill == null
            ? TypedResults.Problem(AgentProblemDetails.NotFound($"Skill '{skillId}' was not found.", context))
            : TypedResults.Ok(skill);
    }

    private static async Task<Results<Ok<SkillUploadResponse>, ProblemHttpResult>> UploadSkillPackageAsync(
        [FromServices] SkillPackageManagementService packages,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!HasScope(context, "agent.config.write"))
            return Forbidden(context, "agent.config.write");
        if (!context.Request.HasFormContentType)
            return TypedResults.Problem(AgentProblemDetails.Invalid(
                "A multipart .zip or .md Skill file is required.", context));

        IFormCollection form = await context.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        IFormFile? file = form.Files.GetFile("file");
        if (file == null)
            return TypedResults.Problem(AgentProblemDetails.Invalid(
                "The multipart field 'file' is required.", context));
        if (file.Length > SkillPackageManagementService.MaxPackageBytes)
            return TypedResults.Problem(AgentProblemDetails.Invalid(
                "Skill package exceeds the 4 MB limit.", context));
        if (!TryReadScriptExecutionFlag(form, out bool? scriptExecutionEnabled, out string? scriptFlagError))
            return TypedResults.Problem(AgentProblemDetails.Invalid(scriptFlagError!, context));

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
            return TypedResults.Ok(new SkillUploadResponse
            {
                Skill = result.Skill,
                Storage = "object-storage-directory"
            });
        }
        catch (InvalidOperationException exception)
        {
            return TypedResults.Problem(AgentProblemDetails.Invalid(exception.Message, context));
        }
    }

    private static async Task<Results<Ok<SkillInstanceConfig>, ProblemHttpResult>> UpdateSkillScriptExecutionAsync(
        [FromServices] SkillPackageManagementService packages,
        [FromBody] SkillScriptSettingsRequest request,
        HttpContext context,
        string skillId,
        CancellationToken cancellationToken)
    {
        if (!HasScope(context, "agent.config.write"))
            return Forbidden(context, "agent.config.write");
        try
        {
            SkillInstanceConfig? skill = await packages.UpdateScriptExecutionAsync(
                RequireTenant(context),
                skillId,
                request.ScriptExecutionEnabled,
                cancellationToken).ConfigureAwait(false);
            return skill == null
                ? TypedResults.Problem(AgentProblemDetails.NotFound($"Skill '{skillId}' was not found.", context))
                : TypedResults.Ok(skill);
        }
        catch (InvalidOperationException exception)
        {
            return TypedResults.Problem(AgentProblemDetails.Invalid(exception.Message, context));
        }
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteSkillAsync(
        [FromServices] SkillPackageManagementService packages,
        HttpContext context,
        string skillId,
        CancellationToken cancellationToken)
    {
        if (!HasScope(context, "skill.config.write"))
            return Forbidden(context, "skill.config.write");
        return await packages.DeleteCatalogAsync(
            RequireTenant(context),
            skillId,
            cancellationToken).ConfigureAwait(false)
            ? TypedResults.NoContent()
            : TypedResults.Problem(AgentProblemDetails.NotFound($"Skill '{skillId}' was not found.", context));
    }

    private static async Task<Results<Ok<SkillInstallResponse>, ProblemHttpResult>> InstallSkillPackageAsync(
        [FromServices] SkillPackageManagementService packages,
        HttpContext context,
        string agentId,
        CancellationToken cancellationToken)
    {
        if (!HasScope(context, "skill.config.write"))
            return Forbidden(context, "skill.config.write");
        if (!context.Request.HasFormContentType)
            return TypedResults.Problem(AgentProblemDetails.Invalid(
                "A multipart .zip or .md Skill file is required.", context));

        IFormCollection form = await context.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        IFormFile? file = form.Files.GetFile("file");
        if (file == null)
            return TypedResults.Problem(AgentProblemDetails.Invalid(
                "The multipart field 'file' is required.", context));
        string extension = Path.GetExtension(file.FileName);
        if (!string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase))
        {
            return TypedResults.Problem(AgentProblemDetails.Invalid(
                "Skill upload must be a .zip package or a single .md file.", context));
        }
        if (file.Length > SkillPackageManagementService.MaxPackageBytes)
            return TypedResults.Problem(AgentProblemDetails.Invalid(
                "Skill package exceeds the 4 MB limit.", context));
        if (!TryReadScriptExecutionFlag(form, out bool? scriptExecutionEnabled, out string? scriptFlagError))
            return TypedResults.Problem(AgentProblemDetails.Invalid(scriptFlagError!, context));

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
            return TypedResults.Problem(AgentProblemDetails.Invalid(exception.Message, context));
        }
        if (!result.AgentExists)
            return TypedResults.Problem(AgentProblemDetails.NotFound($"Agent '{agentId}' was not found.", context));
        if (result.HasTenantMismatch)
            return Forbidden(context, "skill.config.write");
        if (result.HasConflict)
            return TypedResults.Problem(AgentProblemDetails.Conflict(
                "The agent configuration changed concurrently. Retry with a fresh If-Match version.", context));
        return TypedResults.Ok(new SkillInstallResponse
        {
            Skill = result.Skill,
            CurrentVersion = result.CurrentVersion,
            Storage = "object-storage"
        });
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAgentSkillAsync(
        [FromServices] SkillPackageManagementService packages,
        HttpContext context,
        string agentId,
        string skillId,
        CancellationToken cancellationToken)
    {
        if (!HasScope(context, "skill.config.write"))
            return Forbidden(context, "skill.config.write");
        SkillPackageDeleteResult result = await packages.DeleteAsync(
            agentId,
            RequireTenant(context),
            skillId,
            context.Request.Headers.IfMatch.FirstOrDefault(),
            cancellationToken).ConfigureAwait(false);
        return result switch
        {
            SkillPackageDeleteResult.Deleted => TypedResults.NoContent(),
            SkillPackageDeleteResult.Conflict => TypedResults.Problem(AgentProblemDetails.Conflict(
                "The agent configuration changed concurrently. Retry with a fresh If-Match version.", context)),
            SkillPackageDeleteResult.TenantMismatch => Forbidden(context, "skill.config.write"),
            _ => TypedResults.Problem(AgentProblemDetails.NotFound(
                $"Skill '{skillId}' was not found on agent '{agentId}'.", context))
        };
    }

    private static async Task<Results<Ok<SkillPackageValidationResult>, ProblemHttpResult>> ValidateSkillsAsync(
        [FromServices] SkillPackageManagementService packages,
        [FromBody] SkillsConfig skills,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!HasScope(context, "capability.test"))
            return Forbidden(context, "capability.test");
        SkillPackageValidationResult result = await packages
            .ValidateAsync(RequireTenant(context), skills, cancellationToken)
            .ConfigureAwait(false);
        return TypedResults.Ok(result);
    }

    private static ProblemHttpResult Forbidden(HttpContext context, string requiredScope) =>
        TypedResults.Problem(AgentProblemDetails.PermissionDenied(
            $"Access denied due to insufficient permissions. Missing required scope: {requiredScope}.", context));

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
