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

    public IEnumerable<string> EnumerateOutputPaths() => Outputs.Keys.ToArray();

    public async Task<RuntimeWorkspaceFile> RestoreAsync(
        RuntimeFile file, string? outputPath, Func<Stream, CancellationToken, Task> copy, CancellationToken cancellationToken)
    {
        using MemoryStream buffer = new();
        await copy(buffer, cancellationToken);
        byte[] bytes = buffer.ToArray();
        if (bytes.LongLength != file.Length || !Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))
            .Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new OpenAgent.Contracts.Security.AgentException(OpenAgent.Contracts.Requests.AgentErrorCode.InvalidRequest, "Integrity check failed.");
        }
        if (outputPath is null)
        {
            Inputs[file.FileId] = bytes;
        }
        else
        {
            Outputs[outputPath] = bytes;
        }
        return new(file, outputPath ?? $"inputs/{file.FileId}/{file.FileName}");
    }
}
