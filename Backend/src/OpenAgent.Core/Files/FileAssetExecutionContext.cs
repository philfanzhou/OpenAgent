using OpenAgent.Contracts.Files;

namespace OpenAgent.Core.Files;

internal sealed class FileAssetExecutionContext
{
    private readonly List<FileAsset> _published = [];

    internal FileAssetScope? Scope { get; private set; }

    internal IReadOnlyList<FileAsset> Published => _published.AsReadOnly();

    internal void Set(FileAssetScope scope)
    {
        Scope = scope;
    }

    internal void RecordPublished(FileAsset asset)
    {
        if (_published.All(item => !string.Equals(item.FileId, asset.FileId, StringComparison.Ordinal)))
        {
            _published.Add(asset);
        }
    }
}
