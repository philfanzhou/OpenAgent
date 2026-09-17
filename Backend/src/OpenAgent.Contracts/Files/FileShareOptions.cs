namespace OpenAgent.Contracts.Files;

public sealed class FileShareOptions
{
    public const string SectionName = "FileAssets:Share";

    /// <summary>
    /// 有效期硬上限：365 天。任何模式的默认时长与自定义有效期都不能超过它，
    /// 因此不存在永久有效的分享链接；配置值超过该上限会在启动校验时失败。
    /// </summary>
    public const int MaxLifetimeLimitSeconds = 31_536_000;

    /// <summary>
    /// 构建分享链接绝对地址的公网基地址（如 https://engine.example.com）。
    /// 未配置时创建结果只包含相对路径，由调用方自行拼接 origin。
    /// </summary>
    public string? PublicBaseUrl { get; init; }

    public int TemporaryLifetimeSeconds { get; init; } = 900;

    public int SingleUseLifetimeSeconds { get; init; } = 86_400;

    public int LongTermLifetimeSeconds { get; init; } = 2_592_000;

    /// <summary>自定义有效期的上限，防止长期链接被无限延长；最大不能超过 <see cref="MaxLifetimeLimitSeconds"/>（365 天）。</summary>
    public int MaxLifetimeSeconds { get; init; } = MaxLifetimeLimitSeconds;
}
