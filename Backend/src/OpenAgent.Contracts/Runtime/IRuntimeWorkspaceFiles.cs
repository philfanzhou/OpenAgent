namespace OpenAgent.Contracts.Runtime;

/// <summary>Turn-bound file access implemented by the runtime; paths are relative to its workspace.</summary>
public interface IRuntimeWorkspaceFiles
{
    Task<RuntimeWorkspaceFile> RestoreAsync(
        RuntimeFile file, string? outputPath, Func<Stream, CancellationToken, Task> copy, CancellationToken cancellationToken);

    IEnumerable<string> EnumerateOutputPaths();

    Task<RuntimeOutputFile> SnapshotAsync(string relativePath, long maxBytes, CancellationToken cancellationToken);
}

public sealed record RuntimeFile(string FileId, string FileName, string MediaType, long Length, string Sha256);

public sealed record RuntimeWorkspaceFile(RuntimeFile File, string RelativePath);

public sealed record RuntimeFilePreparation(IReadOnlyList<RuntimeWorkspaceFile> Files, IAsyncDisposable Lease)
{
    public IReadOnlyList<RuntimeWorkspaceFile> Outputs { get; init; } = [];
    public Func<CancellationToken, Task>? SaveOutputsAsync { get; init; }
}

/// <summary>Stable, bounded snapshot. The caller must dispose it after uploading or rejecting it.</summary>
public sealed record RuntimeOutputFile(string RelativePath, string FileName, long Length, string Sha256, Stream Content) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}
