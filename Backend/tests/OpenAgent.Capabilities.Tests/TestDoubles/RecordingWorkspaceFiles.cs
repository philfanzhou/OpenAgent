using OpenAgent.Contracts.Runtime;

namespace OpenAgent.Capabilities.Tests.TestDoubles;

internal sealed class RecordingWorkspaceFiles : IRuntimeWorkspaceFiles
{
    internal Dictionary<string, byte[]> Inputs { get; } = new(StringComparer.Ordinal);

    public Task<RuntimeWorkspaceFile> ImportAsync(RuntimeFile file, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Inputs[file.FileId] = data.ToArray();
        return Task.FromResult(new RuntimeWorkspaceFile(file, $"inputs/{file.FileId}/{file.FileName}"));
    }
}
