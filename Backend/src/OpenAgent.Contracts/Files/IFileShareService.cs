namespace OpenAgent.Contracts.Files;

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

    /// <summary>查询当前用户在某租户下的全部分享链接（含已过期/已用尽的），按创建时间倒序。</summary>
    Task<IReadOnlyList<FileShareSummary>> ListAsync(
        FileAssetScope scope,
        CancellationToken cancellationToken);

    /// <summary>
    /// 撤销（删除）当前用户的一条分享链接。链接不存在或不属于该用户/租户时返回 false；
    /// 撤销成功后令牌立即失效。
    /// </summary>
    Task<bool> RevokeAsync(
        string shareIdHash,
        FileAssetScope scope,
        CancellationToken cancellationToken);
}
