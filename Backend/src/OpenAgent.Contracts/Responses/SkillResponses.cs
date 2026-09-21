using OpenAgent.Contracts.Configuration;

namespace OpenAgent.Contracts.Responses;

/// <summary>技能 Markdown 源码（GET /api/v1/admin/skills/{skillId}/source）。</summary>
public sealed class SkillMarkdownResponse
{
    public required string Markdown { get; init; }
}

/// <summary>技能包上传到租户目录的结果。</summary>
public sealed class SkillUploadResponse
{
    public SkillInstanceConfig? Skill { get; init; }

    /// <summary>存储位置说明（对象存储目录）。</summary>
    public required string Storage { get; init; }
}

/// <summary>技能包安装到指定 Agent 的结果。</summary>
public sealed class SkillInstallResponse
{
    public SkillInstanceConfig? Skill { get; init; }

    /// <summary>安装后的 Agent 配置当前版本（ETag 依据）。</summary>
    public string? CurrentVersion { get; init; }

    public required string Storage { get; init; }
}
