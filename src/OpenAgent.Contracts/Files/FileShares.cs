using System.Security.Cryptography;
using System.Text;

namespace OpenAgent.Contracts.Files;

// 文件分享链接的完整契约：配置、请求/响应模型、枚举与解析、令牌工具、服务与仓储接口。
// 整合为单文件便于查阅；仅撤销方式为软删除（标记失效），不物理删除记录。

public interface IFileShareService
{
    /// <summary>分享下载端点的路由前缀；完整地址为 {PublicBaseUrl}/api/v1/share/{token}。</summary>
    public const string RoutePrefix = "/api/v1/share";

    /// <summary>
    /// 为属于当前租户/用户的就绪文件创建分享链接。链接由本服务核销下载，
    /// 不生成也不会暴露对象存储（S3）的预签名地址。
    /// </summary>
    Task<FileShareLink> CreateAsync(
        string fileId,
        FileAssetScope scope,
        FileShareRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// 核销分享链接并返回文件内容。链接不存在、已过期或超出下载次数上限时返回 null。
    /// 单次链接的核销是原子的：并发请求中只有最先到达的一次成功。
    /// </summary>
    Task<FileShareRedemption?> RedeemAsync(
        string token,
        CancellationToken cancellationToken);

    /// <summary>查询当前用户在某租户下仍然有效的分享链接（未过期、未用尽、未撤销），按创建时间倒序。</summary>
    Task<IReadOnlyList<FileShareSummary>> ListAsync(
        FileAssetScope scope,
        CancellationToken cancellationToken);

    /// <summary>
    /// 撤销当前用户的一条分享链接（软删除：把失效时间改写为当前时刻）。
    /// 链接不存在、已失效或不属于该用户/租户时返回 false；撤销成功后令牌立即失效。
    /// </summary>
    Task<bool> RevokeAsync(
        string shareIdHash,
        FileAssetScope scope,
        CancellationToken cancellationToken);
}

public interface IFileShareRepository
{
    Task CreateAsync(FileShareLinkRecord record, CancellationToken cancellationToken);

    Task<FileShareLinkRecord?> GetAsync(string shareIdHash, CancellationToken cancellationToken);

    /// <summary>
    /// 原子核销：仅当链接未过期且未达到下载次数上限时把下载计数加一并返回 true，
    /// 用于多实例部署下严格执行“单次下载”语义。
    /// </summary>
    Task<bool> TryRedeemAsync(string shareIdHash, CancellationToken cancellationToken);

    /// <summary>列出某租户内指定用户仍然有效的分享记录（未过期、未用尽），按创建时间倒序。</summary>
    Task<IReadOnlyList<FileShareLinkRecord>> ListByOwnerAsync(
        string tenantId,
        string ownerId,
        CancellationToken cancellationToken);

    /// <summary>
    /// 软删除（撤销）：仅当记录属于该租户/用户且尚未过期时，把 ExpiresAt 改写为当前时刻并返回 true；
    /// 改写后链接立即失效（兑换返回 404）且不再出现于查询结果。记录本身保留。
    /// </summary>
    Task<bool> TryRevokeAsync(
        string shareIdHash,
        string tenantId,
        string ownerId,
        CancellationToken cancellationToken);
}

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

    /// <summary>交给最终用户（未显式指定 mode）时的默认有效期：3 天。</summary>
    public int UserAudienceLifetimeSeconds { get; init; } = 259_200;

    /// <summary>交给第三方 MCP 工具时的默认有效期：2 小时。</summary>
    public int McpAudienceLifetimeSeconds { get; init; } = 7_200;

    /// <summary>交给第三方 MCP 工具时的默认下载次数上限。</summary>
    public int McpAudienceMaxDownloads { get; init; } = 2;

    /// <summary>自定义有效期的上限，防止长期链接被无限延长；最大不能超过 <see cref="MaxLifetimeLimitSeconds"/>（365 天）。</summary>
    public int MaxLifetimeSeconds { get; init; } = MaxLifetimeLimitSeconds;
}

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

/// <summary>创建分享链接后返回给所有者的结果。Url 是唯一的下载凭证，不暴露对象存储地址。</summary>
public sealed class FileShareLink
{
    /// <summary>分享的持久化标识（令牌哈希），用于查询与撤销；凭它不能兑换下载。</summary>
    public required string ShareId { get; init; }
    public required string FileId { get; init; }
    public required string FileName { get; init; }
    public required string MediaType { get; init; }
    public long Length { get; init; }
    public required FileShareMode Mode { get; init; }
    public required string Url { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public int? MaxDownloads { get; init; }
    public int DownloadCount { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// 分享链接的查询视图。明文令牌只在创建响应中出现，这里不返回 Url，
/// 仅提供用于撤销的 <see cref="ShareId"/> 与策略信息；查询结果只含仍然有效的链接。
/// </summary>
public sealed class FileShareSummary
{
    public required string ShareId { get; init; }
    public required string FileId { get; init; }
    public required string FileName { get; init; }
    public required string MediaType { get; init; }
    public long Length { get; init; }
    public required FileShareMode Mode { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public int? MaxDownloads { get; init; }
    public int DownloadCount { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>核销成功后返回的文件内容快照。</summary>
public sealed class FileShareRedemption
{
    public required string FileName { get; init; }
    public required string MediaType { get; init; }
    public long Length { get; init; }
    public required byte[] Data { get; init; }
}

/// <summary>
/// 分享链接的持久化记录。数据库只保存令牌的 SHA-256 哈希（<see cref="ShareIdHash"/>），
/// 不保存明文令牌；下载时凭哈希定位记录并原子核销。撤销为软删除：把 ExpiresAt 改写为撤销时刻，
/// 记录保留作审计。
/// </summary>
public sealed class FileShareLinkRecord
{
    public required string ShareIdHash { get; init; }
    public required string FileId { get; init; }
    public required string TenantId { get; init; }
    public required string OwnerUserId { get; init; }
    public required string ObjectKey { get; init; }
    public required string FileName { get; init; }
    public required string MediaType { get; init; }
    public long Length { get; init; }
    public FileShareMode Mode { get; init; }
    public required DateTimeOffset ExpiresAt { get; set; }

    /// <summary>最大下载次数；null 表示有效期内不限次数。</summary>
    public int? MaxDownloads { get; init; }
    public int DownloadCount { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }
}

public enum FileShareMode
{
    /// <summary>临时分享：短期有效，有效期内不限下载次数。</summary>
    Temporary = 0,

    /// <summary>单次分享：仅允许一次成功下载，超时或下载后立即失效。</summary>
    SingleUse = 1,

    /// <summary>长期分享：较长的有效期，有效期内不限下载次数。</summary>
    LongTerm = 2,

    /// <summary>
    /// 按消费方（audience）默认策略生成的分享：未显式指定 mode 时，
    /// 有效期与下载次数来自 audience 配置预设，实际值以 ExpiresAt/MaxDownloads 为准。
    /// </summary>
    Custom = 3
}

public static class FileShareModeParser
{
    /// <summary>
    /// 严格解析模式名；null/空表示未指定（走 audience 默认策略），无法识别返回 false。
    /// </summary>
    public static bool TryParse(string? value, out FileShareMode mode)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case null or "":
                mode = FileShareMode.Temporary;
                return false;
            case "temporary":
                mode = FileShareMode.Temporary;
                return true;
            case "singleuse" or "single":
                mode = FileShareMode.SingleUse;
                return true;
            case "longterm" or "long-term" or "long":
                mode = FileShareMode.LongTerm;
                return true;
            default:
                mode = FileShareMode.Temporary;
                return false;
        }
    }
}

/// <summary>
/// 分享链接的消费方。不同消费方有不同的默认策略：
/// MCP（交给第三方工具，暴露面大）默认短有效期、限次下载；User（交给最终用户）默认较长有效期、不限次。
/// 显式指定 <see cref="FileShareMode"/> 时以 mode 为准，audience 默认被覆盖。
/// </summary>
public enum FileShareAudience
{
    /// <summary>最终用户下载/分享；默认 3 天、不限次数。</summary>
    User = 0,

    /// <summary>第三方 MCP 工具拉取；默认 2 小时、最多 2 次下载。</summary>
    Mcp = 1
}

public static class FileShareAudienceParser
{
    /// <summary>严格解析消费方名称；null/空/无法识别返回 false，由调用方决定默认值。</summary>
    public static bool TryParse(string? value, out FileShareAudience audience)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "user":
                audience = FileShareAudience.User;
                return true;
            case "mcp":
                audience = FileShareAudience.Mcp;
                return true;
            default:
                audience = FileShareAudience.User;
                return false;
        }
    }
}

/// <summary>
/// 分享令牌生成与哈希。令牌是 128 位随机 GUID 的十六进制表示，仅在创建响应中出现；
/// 持久层只保存其 SHA-256 哈希，与第三方 API Key 的存储约定一致。
/// </summary>
public static class FileShareTokens
{
    public static string NewToken() => Guid.NewGuid().ToString("N");

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
