namespace OpenAgent.Contracts.Files;

public sealed class FileShareRequest
{
    public FileShareMode Mode { get; init; } = FileShareMode.Temporary;

    /// <summary>
    /// 自定义有效期（秒），覆盖所选模式的默认时长，用于按失效日期精确控制；
    /// 不能超过 <see cref="FileShareOptions.MaxLifetimeSeconds"/>。
    /// </summary>
    public int? ExpiresInSeconds { get; init; }
}
