namespace OpenAgent.Contracts.Files;

public sealed class FileShareRequest
{
    /// <summary>
    /// 显式分享策略；null 表示未指定，按 <see cref="Audience"/> 的默认策略生成。
    /// </summary>
    public FileShareMode? Mode { get; init; }

    /// <summary>
    /// 消费方，决定未指定 Mode 时的默认有效期与下载次数：
    /// MCP 默认 2 小时、2 次下载；User 默认 3 天、不限次数。
    /// </summary>
    public FileShareAudience? Audience { get; init; }

    /// <summary>
    /// 自定义有效期（秒），覆盖所选模式或消费方的默认时长，用于按失效日期精确控制；
    /// 必须为正数且不能超过 <see cref="FileShareOptions.MaxLifetimeSeconds"/>。
    /// </summary>
    public int? ExpiresInSeconds { get; init; }
}
