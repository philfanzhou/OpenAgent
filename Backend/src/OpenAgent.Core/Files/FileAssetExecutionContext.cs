using OpenAgent.Contracts.Files;

namespace OpenAgent.Core.Files;

internal sealed class FileAssetExecutionContext
{
    internal FileAssetScope? Scope { get; private set; }

    internal void Set(FileAssetScope scope)
    {
        Scope = scope;
    }
}
