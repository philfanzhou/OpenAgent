namespace OpenAgent.Contracts.Runtime;

/// <summary>Turn-bound file access implemented by the runtime; paths are relative to its workspace.</summary>
public interface IRuntimeWorkspaceFiles
{
    Task<RuntimeWorkspaceFile> ImportAsync(RuntimeFile file, ReadOnlyMemory<byte> data, CancellationToken cancellationToken);
}

public sealed record RuntimeFile(string FileId, string FileName, string MediaType, long Length, string Sha256);

public sealed record RuntimeWorkspaceFile(RuntimeFile File, string RelativePath);

public sealed record RuntimeFilePreparation(IReadOnlyList<RuntimeWorkspaceFile> Files, IAsyncDisposable Lease);
