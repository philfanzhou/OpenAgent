using OpenAgent.Contracts.Runtime;

namespace OpenAgent.Capabilities.Tests.TestDoubles;

internal sealed class RecordingWorkspaceFiles : IRuntimeWorkspaceFiles
{
    internal Dictionary<string, byte[]> Inputs { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, byte[]> Outputs { get; } = new(StringComparer.Ordinal);
    internal Func<CancellationToken, Task>? BeforeSnapshot { get; set; }

    public async Task<RuntimeOutputFile> SnapshotAsync(string relativePath, long maxBytes, CancellationToken cancellationToken)
    {
        if (BeforeSnapshot is not null)
        {
            await BeforeSnapshot(cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        byte[] data = Outputs[relativePath];
        if (data.Length > maxBytes)
        {
            throw new OpenAgent.Contracts.Security.AgentException(OpenAgent.Contracts.Requests.AgentErrorCode.InvalidRequest, "Size limit exceeded.");
        }
        return new(relativePath, Path.GetFileName(relativePath), data.Length,
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data)), new MemoryStream(data, writable: false));
    }

    public Task<RuntimeWorkspaceFile> ImportAsync(RuntimeFile file, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Inputs[file.FileId] = data.ToArray();
        return Task.FromResult(new RuntimeWorkspaceFile(file, $"inputs/{file.FileId}/{file.FileName}"));
    }
}
