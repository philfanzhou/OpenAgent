using OpenAgent.Contracts.Configuration;

namespace OpenAgent.Contracts.Skills;

/// <summary>
/// Skill 包管理的纯命令/结果契约：宿主只负责解析 HTTP 请求与租户身份，
/// 包校验、版本冲突与失败补偿（旧对象在更新提交后清理）由 Engine 内部实现完成。
/// </summary>
public interface ISkillPackageManagementService
{
    public const long MaxPackageBytes = 4 * 1024 * 1024;

    Task<SkillPackageInstallResult> InstallAsync(
        string agentId,
        string tenantId,
        string userId,
        string fileName,
        string mediaType,
        Stream package,
        string? expectedVersion,
        CancellationToken cancellationToken);

    Task<SkillPackageUploadResult> UploadAsync(
        string tenantId,
        string userId,
        string fileName,
        string mediaType,
        Stream package,
        CancellationToken cancellationToken);

    Task<SkillInstanceConfig?> UpdateMarkdownAsync(
        string tenantId,
        string userId,
        string skillId,
        string markdown,
        CancellationToken cancellationToken);

    Task<bool> DeleteCatalogAsync(
        string tenantId,
        string skillId,
        CancellationToken cancellationToken);

    Task<string?> ReadMarkdownAsync(
        string tenantId,
        string skillId,
        CancellationToken cancellationToken);

    Task<SkillPackageDeleteResult> DeleteAsync(
        string agentId,
        string tenantId,
        string skillId,
        string? expectedVersion,
        CancellationToken cancellationToken);
}

public sealed record SkillMarkdownUpdateRequest(string Markdown);

public sealed record SkillPackageInstallResult(
    SkillInstanceConfig? Skill,
    string? CurrentVersion,
    bool AgentExists,
    bool HasConflict,
    bool HasTenantMismatch)
{
    public static SkillPackageInstallResult Installed(SkillInstanceConfig skill, string currentVersion) =>
        new(skill, currentVersion, AgentExists: true, HasConflict: false, HasTenantMismatch: false);

    public static SkillPackageInstallResult NotFound() =>
        new(null, null, AgentExists: false, HasConflict: false, HasTenantMismatch: false);

    public static SkillPackageInstallResult Conflict() =>
        new(null, null, AgentExists: true, HasConflict: true, HasTenantMismatch: false);

    public static SkillPackageInstallResult TenantMismatch() =>
        new(null, null, AgentExists: true, HasConflict: false, HasTenantMismatch: true);
}

public sealed record SkillPackageUploadResult(SkillInstanceConfig Skill);

public enum SkillPackageDeleteResult
{
    Deleted,
    AgentNotFound,
    SkillNotFound,
    Conflict,
    TenantMismatch
}
