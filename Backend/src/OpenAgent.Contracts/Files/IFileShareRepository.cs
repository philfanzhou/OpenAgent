namespace OpenAgent.Contracts.Files;

public interface IFileShareRepository
{
    Task CreateAsync(FileShareLinkRecord record, CancellationToken cancellationToken);

    Task<FileShareLinkRecord?> GetAsync(string shareIdHash, CancellationToken cancellationToken);

    /// <summary>
    /// 原子核销：仅当链接未过期且未达到下载次数上限时把下载计数加一并返回 true，
    /// 用于多实例部署下严格执行“单次下载”语义。
    /// </summary>
    Task<bool> TryRedeemAsync(string shareIdHash, CancellationToken cancellationToken);

    /// <summary>列出某租户内指定用户的所有分享记录，按创建时间倒序。</summary>
    Task<IReadOnlyList<FileShareLinkRecord>> ListByOwnerAsync(
        string tenantId,
        string ownerId,
        CancellationToken cancellationToken);

    /// <summary>
    /// 删除（撤销）一条分享记录；仅当记录存在且属于该租户/用户时才删除，
    /// 返回是否实际删除。删除后令牌立即失效（兑换返回 404）。
    /// </summary>
    Task<bool> DeleteAsync(
        string shareIdHash,
        string tenantId,
        string ownerId,
        CancellationToken cancellationToken);
}
