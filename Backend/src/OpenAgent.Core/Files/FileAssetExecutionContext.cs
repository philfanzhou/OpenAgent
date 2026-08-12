using OpenAgent.Contracts.Files;

namespace OpenAgent.Core.Files;

internal sealed class FileAssetExecutionContext
{
    private readonly List<FileAsset> _created = [];

    internal FileAssetScope? Scope { get; private set; }

    internal IReadOnlyList<FileAsset> Created => _created.AsReadOnly();

    internal void Set(FileAssetScope scope)
    {
        Scope = scope;
    }

    internal void RecordCreated(FileAsset asset)
    {
        _created.Add(asset);
    }
}
