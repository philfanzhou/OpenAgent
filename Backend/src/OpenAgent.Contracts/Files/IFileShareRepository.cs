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
}
