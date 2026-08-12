namespace OpenAgent.Contracts.Files;

public interface IFileAssetRepository
{
    Task CreateAsync(FileAsset asset, CancellationToken cancellationToken);
    Task UpdateAsync(FileAsset asset, CancellationToken cancellationToken);
    Task<FileAsset?> GetAsync(string fileId, CancellationToken cancellationToken);
}
